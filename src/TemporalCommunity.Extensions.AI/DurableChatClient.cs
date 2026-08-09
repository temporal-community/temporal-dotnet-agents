using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// A <see cref="DelegatingChatClient"/> middleware that wraps <see cref="IChatClient.GetResponseAsync"/>
/// as a Temporal activity when running inside a Temporal workflow.
/// </summary>
/// <remarks>
/// <para>
/// Context-aware behavior:
/// <list type="bullet">
///   <item>Inside a Temporal workflow → dispatches via <c>Workflow.ExecuteActivityAsync</c></item>
///   <item>Inside a Temporal activity → passes through to inner client (avoids double-wrapping)</item>
///   <item>External (neither) → passes through to inner client</item>
/// </list>
/// </para>
/// <para>
/// <b>Streaming inside a Temporal workflow is buffered.</b> When
/// <see cref="GetStreamingResponseAsync"/> is called from workflow context, the implementation
/// executes the non-streaming activity and replays the complete response as a sequence of
/// synthetic <see cref="ChatResponseUpdate"/> chunks. True token-by-token streaming is not
/// possible across the workflow/activity boundary because Temporal activities return a single
/// serialized result. If per-token latency is not a requirement, prefer
/// <see cref="GetResponseAsync"/> in workflow context — it is semantically equivalent and
/// avoids the overhead of converting a <see cref="ChatResponse"/> to updates. Outside a
/// workflow the real streaming path is preserved.
/// </para>
/// <para>
/// <b>Limitation:</b> <see cref="ChatOptions.RawRepresentationFactory"/> is not serializable and
/// will not be available on the worker side when invoked as an activity.
/// </para>
/// </remarks>
/// <param name="innerClient">The inner chat client to delegate to.</param>
/// <param name="durableOptions">Durable execution configuration.</param>
public sealed class DurableChatClient(IChatClient innerClient, DurableExecutionOptions durableOptions)
    : DelegatingChatClient(innerClient)
{
    // Field initializer validates durableOptions at construction time. ArgumentNullException.ThrowIfNull()
    // cannot be used here — primary constructors have no body for guard statements; field
    // initializers are the only available validation site.
    private readonly DurableExecutionOptions _durableOptions =
        durableOptions ?? throw new ArgumentNullException(nameof(durableOptions));

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!Workflow.InWorkflow)
        {
            // Outside a workflow — pass through directly, stripping Temporal-internal keys
            // that the inner client does not understand.
            return await base.GetResponseAsync(
                messages,
                Internal.ChatOptionsSanitizer.PrepareForProvider(options),
                cancellationToken)
                .ConfigureAwait(false);
        }

        // Inside a workflow — dispatch as an activity.
        var input = CreateInput(messages, options);

        // Keep this continuation on Temporal's workflow task scheduler so subsequent
        // workflow commands are issued through the active workflow context.
        var response = await Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(input),
            CreateActivityOptions(options));

        return response;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Inside a Temporal workflow, streaming is buffered.</b> The activity is executed as a
    /// non-streaming call and the complete <see cref="ChatResponse"/> is then converted to a
    /// sequence of <see cref="ChatResponseUpdate"/> objects. Callers will observe a deferred
    /// batch — all updates arrive together after the activity completes — rather than
    /// token-by-token updates. This is an inherent constraint of the Temporal activity model,
    /// which serializes a single result value across the workflow/activity boundary.
    /// <para>
    /// Outside a workflow the real streaming path is preserved and updates are forwarded
    /// directly from the inner <see cref="IChatClient"/>.
    /// </para>
    /// </remarks>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Workflow.InWorkflow)
        {
            // Outside a workflow — real streaming is preserved. Pass through directly,
            // stripping Temporal-internal keys that the inner client does not understand.
            await foreach (var update in base.GetStreamingResponseAsync(
                messages,
                Internal.ChatOptionsSanitizer.PrepareForProvider(options),
                cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }
            yield break;
        }

        // Inside a workflow — buffered strategy: execute as a non-streaming activity, then
        // replay the full response as a synthetic ChatResponseUpdate sequence.
        // Temporal activities return a single serialized result; true token-by-token streaming
        // across the workflow/activity boundary is not supported.
        var input = CreateInput(messages, options);

        // Keep this continuation on Temporal's workflow task scheduler so subsequent
        // workflow commands are issued through the active workflow context.
        var response = await Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(input),
            CreateActivityOptions(options));

        // Convert the buffered response to streaming updates.
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(DurableExecutionOptions) && serviceKey is null)
        {
            return _durableOptions;
        }

        return base.GetService(serviceType, serviceKey);
    }

    private DurableChatInput CreateInput(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        if (options?.Tools is { Count: > 0 })
        {
            throw new Exceptions.DurableConfigurationException(
                "ChatOptions.Tools cannot be used from a durable workflow call. " +
                "Use DurableChatSessionClient with tools registered through AddDurableTools.");
        }

        // TurnNumber is omitted (defaults to 0) in the middleware path: DurableChatClient is a
        // DI singleton shared across all workflow instances, so a per-instance counter would
        // aggregate across unrelated sessions and be meaningless. In the managed session path
        // (DurableChatWorkflowBase.RunTurnAsync), the per-session _turnCount is passed directly
        // into DurableChatInput when constructing the activity input inside the workflow.
        return new DurableChatInput
        {
            Messages = messages as IList<ChatMessage> ?? messages.ToList(),
            Options = Internal.ChatOptionsSanitizer.PrepareForDurableTransport(options),
            ConversationId = Workflow.Info.WorkflowId,
            ClientKey = options.GetChatClientKey() ?? _durableOptions.DefaultChatClientKey,
        };
    }

    internal ActivityOptions CreateActivityOptions(ChatOptions? chatOptions = null)
    {
        var activityOptions = new ActivityOptions
        {
            TaskQueue = _durableOptions.TaskQueue,
            StartToCloseTimeout = chatOptions.GetActivityTimeout() ?? _durableOptions.ActivityTimeout,
            HeartbeatTimeout = chatOptions.GetHeartbeatTimeout() ?? _durableOptions.HeartbeatTimeout,
            // A null policy would otherwise delegate to Temporal's unlimited server default.
            // Keep custom-workflow middleware consistent with managed chat sessions.
            RetryPolicy = Internal.DefaultRetryPolicy.Resolve(_durableOptions.RetryPolicy),
            Summary = BuildActivitySummary(chatOptions),
        };

        // Per-request retry override via AdditionalProperties.
        var maxRetry = chatOptions.GetMaxRetryAttempts();
        if (maxRetry.HasValue)
        {
            activityOptions.RetryPolicy = new Temporalio.Common.RetryPolicy
            {
                MaximumAttempts = maxRetry.Value,
            };
        }

        return activityOptions;
    }

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the model id when available; returns null otherwise so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(ChatOptions? chatOptions)
    {
        var modelId = chatOptions?.ModelId;
        return string.IsNullOrWhiteSpace(modelId) ? null : modelId;
    }

}
