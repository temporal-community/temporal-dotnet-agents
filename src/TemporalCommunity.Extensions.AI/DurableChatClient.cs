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
            return await base.GetResponseAsync(messages, StripTemporalOptions(options), cancellationToken)
                .ConfigureAwait(false);
        }

        // Inside a workflow — dispatch as an activity.
        var input = CreateInput(messages, options);

        var response = await Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(input),
            CreateActivityOptions(options)).ConfigureAwait(false);

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
            await foreach (var update in base.GetStreamingResponseAsync(messages, StripTemporalOptions(options), cancellationToken)
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

        var response = await Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(input),
            CreateActivityOptions(options)).ConfigureAwait(false);

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
            Options = StripNonSerializableOptions(options),
            ConversationId = Workflow.Info.WorkflowId,
            ClientKey = options.GetChatClientKey() ?? _durableOptions.DefaultChatClientKey,
        };
    }

    internal ActivityOptions CreateActivityOptions(ChatOptions? chatOptions = null)
    {
        var activityOptions = new ActivityOptions
        {
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

    /// <summary>
    /// Creates a serializable copy of ChatOptions, stripping non-serializable fields
    /// and Temporal-internal keys from AdditionalProperties.
    /// </summary>
    private static ChatOptions? StripNonSerializableOptions(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        // Clone the options to avoid mutating the caller's instance.
        //
        // ALLOW (copied — serializable and materially steer the model):
        //   Temperature, MaxOutputTokens, TopP, TopK, StopSequences, FrequencyPenalty,
        //   PresencePenalty, Seed, ModelId, ResponseFormat, ToolMode,
        //   AdditionalProperties (Temporal keys stripped), ConversationId,
        //   Instructions, Reasoning, AllowMultipleToolCalls, AllowBackgroundResponses.
        //
        // DENY (intentionally dropped — delegate-backed / not durably serializable):
        //   - RawRepresentationFactory: a delegate; cannot be serialized.
        //   - ContinuationToken: a provider-specific opaque token (experimental,
        //     ResponseContinuationToken) that is not meaningful to replay across the
        //     durable boundary; dropping it keeps the durable request self-contained.
        return new ChatOptions
        {
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            TopP = options.TopP,
            TopK = options.TopK,
            StopSequences = options.StopSequences,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            Seed = options.Seed,
            ModelId = options.ModelId,
            ResponseFormat = options.ResponseFormat,
            Tools = options.Tools,
            ToolMode = options.ToolMode,
            AdditionalProperties = StripTemporalKeys(options.AdditionalProperties),
            ConversationId = options.ConversationId,
            Instructions = options.Instructions,
            Reasoning = options.Reasoning,
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            AllowBackgroundResponses = options.AllowBackgroundResponses,
        };
    }

    /// <summary>
    /// Returns a shallow copy of <paramref name="options"/> with Temporal-internal keys removed
    /// from <see cref="ChatOptions.AdditionalProperties"/>. Used for the pass-through path.
    /// Returns null when <paramref name="options"/> is null.
    /// </summary>
    private static ChatOptions? StripTemporalOptions(ChatOptions? options)
    {
        if (options is null) return null;
        if (options.AdditionalProperties is not { Count: > 0 }) return options;

        // Only allocate a stripped copy when there are Temporal keys to remove.
        bool hasTemporalKeys = false;
        foreach (var kvp in options.AdditionalProperties)
        {
            if (IsTemporalKey(kvp.Key))
            {
                hasTemporalKeys = true;
                break;
            }
        }

        if (!hasTemporalKeys) return options;

        return new ChatOptions
        {
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            TopP = options.TopP,
            TopK = options.TopK,
            StopSequences = options.StopSequences,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            Seed = options.Seed,
            ModelId = options.ModelId,
            ResponseFormat = options.ResponseFormat,
            Tools = options.Tools,
            ToolMode = options.ToolMode,
            AdditionalProperties = StripTemporalKeys(options.AdditionalProperties),
            ConversationId = options.ConversationId,
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="props"/> with Temporal-internal keys removed,
    /// or null if <paramref name="props"/> is null or all entries are Temporal keys.
    /// </summary>
    private static AdditionalPropertiesDictionary? StripTemporalKeys(AdditionalPropertiesDictionary? props)
    {
        if (props is null) return null;

        AdditionalPropertiesDictionary? result = null;
        foreach (var kvp in props)
        {
            if (IsTemporalKey(kvp.Key))
                continue;
            result ??= new AdditionalPropertiesDictionary();
            result[kvp.Key] = kvp.Value;
        }
        return result;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the key is a Temporal-internal marker that must be
    /// stripped before passing options through to the inner <see cref="IChatClient"/>. Centralized
    /// here so adding new keys only requires one update site.
    /// </summary>
    private static bool IsTemporalKey(string key) =>
        key is TemporalChatOptionsExtensions.ActivityTimeoutKey
            or TemporalChatOptionsExtensions.HeartbeatTimeoutKey
            or TemporalChatOptionsExtensions.MaxRetryAttemptsKey
            or TemporalChatOptionsExtensions.ChatClientKeySettingKey
            or TemporalChatOptionsExtensions.ChatClientFactoryKeySettingKey
            || key.StartsWith(TemporalChatOptionsExtensions.ChatClientTagsKeyPrefix, StringComparison.Ordinal);
}
