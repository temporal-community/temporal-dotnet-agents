using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Temporal activities that perform actual LLM inference.
/// The <see cref="IChatClient"/> is resolved from DI on the worker side,
/// optionally by keyed service key carried in <see cref="DurableChatInput.ClientKey"/>.
/// </summary>
internal sealed class DurableChatActivities(
    IServiceProvider services,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger<DurableChatActivities>();

    /// <summary>
    /// <see cref="ApplicationFailureException.ErrorType"/> stamped on a non-retryable LLM-call
    /// failure (deterministic HTTP 4xx classified by <see cref="Internal.LlmErrorClassifier"/>).
    /// Workflow loops match on this so an LLM step failing fast advances the consecutive-error
    /// counter instead of being mistaken for a transient fault.
    /// </summary>
    internal const string LlmNonRetryableErrorType = Internal.LlmFailurePolicy.NonRetryableErrorType;

    /// <summary>
    /// Per-instance cache of <see cref="IChatClient"/> references that already passed the
    /// mixed-pattern B-check, keyed by object identity. The walk is cheap (small chain of
    /// <see cref="DelegatingChatClient"/> nodes) but cached anyway so the second activity
    /// invocation on the same resolved client is allocation-free.
    /// </summary>
    private readonly System.Collections.Generic.HashSet<IChatClient> _mixedPatternCheckedClients =
        new(Internal.ReferenceComparer<IChatClient>.Instance);

    private readonly object _mixedPatternCheckLock = new();

    /// <summary>
    /// Executes a chat completion by calling the inner <see cref="IChatClient"/>.
    /// </summary>
    [Activity("TemporalCommunity.Extensions.AI.GetResponse")]
    public async Task<ChatResponse> GetResponseAsync(DurableChatInput input)
    {
        var ctx = ActivityExecutionContext.HasCurrent ? ActivityExecutionContext.Current : null;

        _logger.LogChatActivityStarted(input.ConversationId, input.TurnNumber);

        var modelId = input.Options?.ModelId;
        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            $"{DurableChatTelemetry.ChatOperationName} {modelId ?? "unknown"}",
            System.Diagnostics.ActivityKind.Client);

        SetupSpanTags(span, input.ConversationId, modelId, input.Options);

        EnsureNoCallerSuppliedTools(input.Options);
        var resolvedOptions = input.Options;

        var chatClient = ResolveChatClient(input.ClientKey);

        // Per-invocation backstop for the MEAI mixed-pattern conflict. The startup check
        // (DurableMixedPatternValidator) only walks the unkeyed default IChatClient. This
        // backstop catches keyed-only setups, factory-deferred resolutions, and other paths
        // the A-check cannot reach. Per-client cache so the walk runs at most once per
        // resolved instance.
        EnsureMixedPatternCheck(chatClient);
        chatClient = new Internal.ProviderBoundaryChatClient(chatClient);
        Internal.ChatClientActivityTags.Apply(resolvedOptions, _logger);

        var response = await StreamAndCollectAsync(
            chatClient, input.Messages, resolvedOptions, input, span, ctx)
            .ConfigureAwait(false);

        _logger.LogChatActivityCompleted(input.ConversationId, input.TurnNumber);

        return response;
    }

    /// <summary>
    /// Once-per-client backstop for inline function-invocation middleware combined with registered
    /// durable tools. The startup
    /// <see cref="Internal.DurableMixedPatternValidator"/> handles the unkeyed default; this
    /// is the safety net for keyed and factory-deferred clients.
    /// </summary>
    private void EnsureMixedPatternCheck(IChatClient chatClient)
    {
        // Fast path: client already validated. No lock needed for the contains check because
        // HashSet<T>.Contains is safe for single-reader concurrent observation given the
        // surrounding lock on writes — but to keep the code obviously correct we lock once.
        lock (_mixedPatternCheckLock)
        {
            if (_mixedPatternCheckedClients.Contains(chatClient))
            {
                return;
            }

            if (!HasDurableToolsRegistered())
            {
                // No durable tools → no conflict possible. Cache anyway so repeat calls skip.
                _mixedPatternCheckedClients.Add(chatClient);
                return;
            }

            ThrowIfMixedPattern(chatClient);

            _mixedPatternCheckedClients.Add(chatClient);
        }
    }

    private bool HasDurableToolsRegistered()
    {
        var registry = services.GetService<DurableFunctionRegistry>();
        var declarations = services.GetService<Internal.DurableFunctionDeclarationRegistry>();
        return (registry is not null && registry.Count > 0)
            || (declarations is not null && declarations.Count > 0);
    }

    private static void ThrowIfMixedPattern(IChatClient chatClient)
    {
        if (Internal.AgentChainWalker.Contains<FunctionInvokingChatClient>(chatClient))
        {
            throw new DurableMixedPatternException();
        }
    }

    /// <summary>
    /// Executes a single durable LLM step. Unlike <see cref="GetResponseAsync"/> this method
    /// never executes tools inline — the durable workflow is responsible for dispatching each
    /// <see cref="FunctionCallContent"/> as its own <c>InvokeFunction</c> activity. The
    /// <see cref="DurableChatStepResult"/> carries the raw assistant message plus extracted
    /// tool-call requests so the workflow can fan them out.
    /// </summary>
    [Activity("TemporalCommunity.Extensions.AI.GetChatStep")]
    public async Task<DurableChatStepResult> GetChatStepAsync(DurableChatInput input)
    {
        var ctx = ActivityExecutionContext.HasCurrent ? ActivityExecutionContext.Current : null;

        _logger.LogChatStepStarted(input.ConversationId, input.TurnNumber);

        var modelId = input.Options?.ModelId;
        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            $"{DurableChatTelemetry.ChatOperationName} {modelId ?? "unknown"}",
            System.Diagnostics.ActivityKind.Client);

        SetupSpanTags(span, input.ConversationId, modelId, input.Options);

        // Caller-supplied tools cannot cross the durable boundary. The workflow owns the
        // model-facing schema and supplies its recorded declarations to this activity.
        EnsureNoCallerSuppliedTools(input.Options);
        var effectiveOptions = input.Options?.Clone() ?? new ChatOptions();
        if (input.ToolDeclarations is { Count: > 0 })
        {
            effectiveOptions.Tools = [.. input.ToolDeclarations.Select(d => d.ToDeclaration())];
        }
        else
        {
            effectiveOptions.Tools = null;
        }

        var chatClient = ResolveChatClient(input.ClientKey);
        EnsureMixedPatternCheck(chatClient);
        chatClient = new Internal.ProviderBoundaryChatClient(chatClient);
        Internal.ChatClientActivityTags.Apply(effectiveOptions, _logger);

        var response = await StreamAndCollectAsync(
            chatClient, input.Messages, effectiveOptions, input, span, ctx)
            .ConfigureAwait(false);

        // Coalesce all assistant messages from the response into a single ChatMessage
        // carrying every content item. Streaming responses may split content across
        // multiple chunks; the workflow loop just needs one assistant message to
        // append to its accumulated transcript.
        var (assistantMessage, toolCalls) = CollectAssistantContents(response);
        var classification = DurableChatCompletionPolicy.Classify(
            response.FinishReason,
            toolCalls.Count);
        var isFinal = classification.Disposition != DurableChatStepDisposition.ContinueWithTools;
        var completionReason = classification.Disposition ==
            DurableChatStepDisposition.IncompleteResponse
                ? DurableTurnCompletionReason.IncompleteResponse
                : DurableTurnCompletionReason.FinalResponse;

        if (classification.Disposition != DurableChatStepDisposition.ContinueWithTools)
        {
            span?.SetTag(
                DurableChatTelemetry.TurnCompletionReasonAttribute,
                completionReason.ToString());
        }

        if (classification.Disposition == DurableChatStepDisposition.IncompleteResponse)
        {
            _logger.LogChatStepProviderOutputRejected(
                input.ConversationId,
                input.TurnNumber,
                response.FinishReason?.Value ?? "null",
                toolCalls.Count,
                classification.IsProviderOutputContradictory);
        }

        _logger.LogChatStepCompleted(input.ConversationId, input.TurnNumber, isFinal, toolCalls.Count);

        return new DurableChatStepResult
        {
            IsFinal = isFinal,
            AssistantMessage = assistantMessage,
            ToolCalls = classification.Disposition == DurableChatStepDisposition.ContinueWithTools
                ? toolCalls
                : null,
            Usage = response.Usage,
            FinishReason = response.FinishReason,
            CompletionReason = completionReason,
        };
    }

    /// <summary>
    /// Applies a keyed history reducer to the supplied history list and returns the result.
    /// Dispatched by <see cref="DurableChatWorkflow"/> at continue-as-new time when
    /// <see cref="DurableChatWorkflowInput.HistoryReducerKey"/> is set. The reducer delegate is
    /// resolved from DI via <see cref="IServiceProvider.GetKeyedService{T}"/>, applied to the
    /// history, and the trimmed list is returned to the workflow as the new carried history.
    /// </summary>
    /// <remarks>
    /// Running the reducer inside an activity (not on the workflow thread) ensures that:
    /// <list type="bullet">
    ///   <item>the delegate is resolved from DI (workflows have no service provider);</item>
    ///   <item>the activity result is stored in Temporal history, so replay is deterministic.</item>
    /// </list>
    /// </remarks>
    [Activity("TemporalCommunity.Extensions.AI.ReduceHistoryByKey")]
    public Task<List<Session.DurableSessionEntry>> ReduceHistoryByKeyAsync(
        ReduceHistoryByKeyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var reducer = services.GetKeyedService<
            Func<IList<Session.DurableSessionEntry>, IList<Session.DurableSessionEntry>>>(
            input.ReducerKey)
            ?? throw new InvalidOperationException(
                $"No history reducer registered under key '{input.ReducerKey}'. " +
                $"Register a Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> " +
                $"via services.AddKeyedSingleton(\"{input.ReducerKey}\", ...).");

        var result = reducer(input.History);
        return Task.FromResult(result as List<Session.DurableSessionEntry>
            ?? result.ToList());
    }

    /// <summary>
    /// Shared streaming accumulator: streams the LLM response, heartbeats each chunk,
    /// materializes the response via <c>ToChatResponse()</c>, populates span success tags,
    /// and rethrows any exception with span error status and a log entry. The span is passed
    /// in (owned by the caller's <c>using</c> block) so its lifetime spans the entire method.
    /// </summary>
    private async Task<ChatResponse> StreamAndCollectAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions? options,
        DurableChatInput input,
        System.Diagnostics.Activity? span,
        ActivityExecutionContext? ctx)
    {
        var ct = ctx?.CancellationToken ?? CancellationToken.None;
        try
        {
            var collected = new List<ChatResponseUpdate>(32);
            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, ct)
                .WithCancellation(ct)
                .ConfigureAwait(false))
            {
                collected.Add(update);
                ctx?.Heartbeat(update.Text);
            }
            var response = collected.ToChatResponse();

            span?.SetTag(DurableChatTelemetry.InputTokensAttribute, response.Usage?.InputTokenCount);
            span?.SetTag(DurableChatTelemetry.OutputTokensAttribute, response.Usage?.OutputTokenCount);
            span?.SetTag(
                DurableChatTelemetry.ReasoningOutputTokensAttribute,
                response.Usage?.ReasoningTokenCount);
            span?.SetTag(DurableChatTelemetry.ResponseModelAttribute, response.ModelId);
            if (response.FinishReason is { } finishReason)
            {
                span?.SetTag(
                    DurableChatTelemetry.ResponseFinishReasonsAttribute,
                    new[] { finishReason.Value });
            }

            span?.SetTag(
                DurableChatTelemetry.EmptyVisibleTextAttribute,
                !HasVisibleAssistantText(response));

            return response;
        }
        catch (OperationCanceledException)
        {
            // Activity/workflow cancellation — never reclassify; let Temporal handle it.
            throw;
        }
        catch (Exception ex)
        {
            span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            _logger.LogChatActivityFailed(ex, input.ConversationId, input.TurnNumber);

            // Retry-hardening: a deterministic LLM error (HTTP 400/401/403/404/422) will never
            // succeed on retry. Relying on an attempt cap would only delay the inevitable terminal
            // result. Rethrow it as a non-retryable ApplicationFailure so
            // Temporal stops immediately; retryable/transient errors propagate unchanged so the
            // activity's RetryPolicy governs them. ErrorType lets workflow callers recognize it.
            if (Internal.LlmFailurePolicy.CreateNonRetryableFailure(ex) is { } nonRetryableFailure)
            {
                throw nonRetryableFailure;
            }

            throw;
        }
    }

    /// <summary>
    /// Coalesces assistant messages in <paramref name="response"/> into a single
    /// <see cref="ChatMessage"/> and extracts tool-call requests. Used by
    /// <see cref="GetChatStepAsync"/> to build a <see cref="DurableChatStepResult"/>.
    /// </summary>
    private static (ChatMessage AssistantMessage, List<FunctionCallContent> ToolCalls)
        CollectAssistantContents(ChatResponse response)
    {
        List<AIContent> assistantContents = [];
        foreach (var msg in response.Messages)
        {
            if (msg.Role == ChatRole.Assistant)
            {
                foreach (var c in msg.Contents)
                {
                    assistantContents.Add(c);
                }
            }
        }

        var assistantMessage = new ChatMessage(ChatRole.Assistant, assistantContents);
        var toolCalls = assistantContents.OfType<FunctionCallContent>().ToList();

        return (assistantMessage, toolCalls);
    }

    private static bool HasVisibleAssistantText(ChatResponse response) =>
        response.Messages
            .Where(message => message.Role == ChatRole.Assistant)
            .SelectMany(message => message.Contents)
            .OfType<TextContent>()
            .Any(content => !string.IsNullOrWhiteSpace(content.Text));

    private static void EnsureNoCallerSuppliedTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 })
        {
            return;
        }

        throw new ApplicationFailureException(
            "ChatOptions.Tools is not supported by durable execution. Register worker-owned tools " +
            "with AddDurableTools or AddDurableToolset.",
            errorType: nameof(DurableConfigurationException),
            nonRetryable: true);
    }

    /// <summary>
    /// Sets up the shared OTel span tags for chat activities.
    /// </summary>
    private static void SetupSpanTags(
        System.Diagnostics.Activity? span,
        string? conversationId,
        string? modelId,
        ChatOptions? options)
    {
        span?.SetTag(DurableChatTelemetry.OperationNameAttribute, DurableChatTelemetry.ChatOperationName);
        span?.SetTag(DurableChatTelemetry.ConversationIdAttribute, conversationId);
        span?.SetTag(DurableChatTelemetry.RequestModelAttribute, modelId);
        span?.SetTag(DurableChatTelemetry.RequestMaxTokensAttribute, options?.MaxOutputTokens);
    }

    /// <summary>
    /// Resolves the inner <see cref="IChatClient"/> from DI. When
    /// <paramref name="clientKey"/> is non-empty, the keyed registration is used; otherwise
    /// the unkeyed registration is used. Shared by <see cref="GetResponseAsync"/> and
    /// <see cref="GetChatStepAsync"/> to avoid resolution drift.
    /// </summary>
    private IChatClient ResolveChatClient(string? clientKey) =>
        string.IsNullOrEmpty(clientKey)
            ? services.GetRequiredService<IChatClient>()
            : services.GetRequiredKeyedService<IChatClient>(clientKey);

    /// <summary>
    /// Runs the <see cref="IDurableToolInterceptor{TContext}"/> before a durable tool is
    /// dispatched. Resolves the interceptor from DI; if none is registered, logs a warning
    /// and returns <see cref="DurableToolOutcome.Proceed"/> so the tool still runs.
    /// </summary>
    /// <remarks>
    /// When no interceptor is resolved at activity time (for example, after a worker
    /// configuration error), this activity returns <see cref="DurableToolOutcome.Proceed"/>.
    /// The workflow independently applies every registration-time <c>RequireApproval()</c>
    /// floor after it receives this result, so a missing interceptor cannot bypass a tool that
    /// was configured to require approval.
    /// </remarks>
    [Activity("TemporalCommunity.Extensions.AI.RunToolInterceptor")]
    public async Task<DurableToolInterceptorResult> RunToolInterceptorAsync(
        DurableToolInterceptorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var interceptor = services.GetService<IDurableToolInterceptor<DurableToolContext>>();
        if (interceptor is null)
        {
            // Interceptor was removed between workflow dispatch and activity execution
            // (e.g. worker restart without re-registration). Degrade to Proceed so the
            // tool still runs rather than silently blocking the session.
            _logger.LogToolInterceptorNotRegistered(input.ToolName);
            return new DurableToolInterceptorResult { Outcome = DurableToolOutcome.Proceed };
        }

        ctx.Heartbeat($"intercepting tool '{input.ToolName}'");

        var toolContext = new DurableToolContext
        {
            ToolName = input.ToolName,
            Arguments = input.Arguments is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(input.Arguments),
            CallId = input.CallId,
            SessionId = ctx.Info.WorkflowId,
            ConversationId = input.ConversationId,
            CorrelationId = input.CorrelationId,
            TurnNumber = input.TurnNumber,
        };

        DurableToolDecision decision;
        try
        {
            decision = await interceptor
                .BeforeToolCallAsync(toolContext, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogToolInterceptorThrew(ex, input.ToolName);
            return new DurableToolInterceptorResult
            {
                Outcome = DurableToolOutcome.Block,
                Message = $"Interceptor threw an exception: {ex.Message}",
            };
        }

        return DurableToolInterceptorResult.FromDecision(decision);
    }
}
