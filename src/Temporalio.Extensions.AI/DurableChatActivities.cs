using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Exceptions;
using Temporalio.Extensions.AI.Exceptions;

namespace Temporalio.Extensions.AI;

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
    /// Per-instance cache of <see cref="IChatClient"/> references that already passed the
    /// mixed-pattern B-check, keyed by object identity. The walk is cheap (small chain of
    /// <see cref="DelegatingChatClient"/> nodes) but cached anyway so the second activity
    /// invocation on the same resolved client is allocation-free.
    /// </summary>
    private readonly System.Collections.Generic.HashSet<IChatClient> _mixedPatternCheckedClients =
        new(ReferenceEqualityComparer.Instance);

    private readonly object _mixedPatternCheckLock = new();

    /// <summary>
    /// Executes a chat completion by calling the inner <see cref="IChatClient"/>.
    /// </summary>
    [Activity("Temporalio.Extensions.AI.GetResponse")]
    public async Task<ChatResponse> GetResponseAsync(DurableChatInput input)
    {
        var ctx = ActivityExecutionContext.HasCurrent ? ActivityExecutionContext.Current : null;

        _logger.LogDebug(
            "Executing durable chat activity for conversation {ConversationId}, turn {TurnNumber}",
            input.ConversationId, input.TurnNumber);

        var modelId = input.Options?.ModelId;
        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            $"{DurableChatTelemetry.ChatOperationName} {modelId ?? "unknown"}",
            System.Diagnostics.ActivityKind.Client);

        SetupSpanTags(span, input.ConversationId, modelId);

        // Swap any ToolNamePlaceholder instances (left over from wire deserialization) with
        // real AIFunction references resolved from the durable-tool registry. Wire format
        // carries names only — placeholders here mean the caller supplied an explicit
        // ChatOptions.Tools subset that needs activity-side rehydration. Pattern 1
        // (GetResponseAsync) typically relies on FunctionInvokingChatClient inside the
        // chat-client chain to invoke tools, so the rehydrated entries need to be the real
        // AIFunctions or FIC has nothing to call.
        var resolvedOptions = SwapPlaceholderTools(input.Options);

        var chatClient = ResolveChatClient(input.ClientKey);

        // Step 4d: B-check backstop for the MEAI mixed-pattern conflict. The startup A-check
        // (DurableMixedPatternValidator) only walks the unkeyed default IChatClient. This
        // backstop catches keyed-only setups, factory-deferred resolutions, and other paths
        // the A-check cannot reach. Per-client cache so the walk runs at most once per
        // resolved instance.
        EnsureMixedPatternCheck(chatClient);

        // Step 4c: per-call IChatClientDecorator resolution. Per-call WithChatClientFactoryKey
        // wins; worker-level DefaultChatClientFactoryKey is the fallback. Empty-string per-call
        // value is the documented opt-out (overrides the worker default with "no decoration").
        var factoryKey = resolvedOptions.GetChatClientFactoryKey()
            ?? services.GetService<DurableExecutionOptions>()?.DefaultChatClientFactoryKey;

        if (!string.IsNullOrEmpty(factoryKey))
        {
            var decorator = services.GetKeyedService<IChatClientDecorator>(factoryKey)
                ?? throw new DurableChatClientFactoryNotFoundException(factoryKey);
            chatClient = decorator.Decorate(chatClient, resolvedOptions);
        }

        var response = await StreamAndCollectAsync(
            chatClient, input.Messages, resolvedOptions, input, span, ctx)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Durable chat activity completed for conversation {ConversationId}, turn {TurnNumber}",
            input.ConversationId, input.TurnNumber);

        // Safety net for the silent-failure footgun (Pattern 3 design: OD-6).
        // If the user registered durable tools but neither (a) FunctionInvokingChatClient
        // is in the chain to handle them inline, nor (b) the workflow is the Pattern 3
        // dispatch loop (which routes through GetChatStepAsync, not this activity),
        // tool calls would be silently dropped. Throw to surface the misconfiguration.
        EnsureToolDispatchHandlerWired(chatClient, response);

        return response;
    }

    /// Once-per-client backstop check for the silent A+B mixed-pattern misconfiguration:
    /// .UseFunctionInvocation() in the IChatClient chain combined with .AsDurable()-wrapped
    /// tools in the DurableFunctionRegistry. The startup
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

            var registry = services.GetService<DurableFunctionRegistry>();
            if (registry is null || registry.Count == 0)
            {
                // No durable tools → no conflict possible. Cache anyway so repeat calls skip.
                _mixedPatternCheckedClients.Add(chatClient);
                return;
            }

            if (Internal.AgentChainWalker.Contains<FunctionInvokingChatClient>(chatClient))
            {
                throw new DurableMixedPatternException();
            }

            _mixedPatternCheckedClients.Add(chatClient);
        }
    }

    /// <summary>
    /// Executes a single Pattern 3 LLM step. Unlike <see cref="GetResponseAsync"/> this method
    /// never executes tools inline — the durable workflow is responsible for dispatching each
    /// <see cref="FunctionCallContent"/> as its own <c>InvokeFunction</c> activity. The
    /// <see cref="DurableChatStepResult"/> carries the raw assistant message plus extracted
    /// tool-call requests so the workflow can fan them out.
    /// </summary>
    [Activity("Temporalio.Extensions.AI.GetChatStep")]
    public async Task<DurableChatStepResult> GetChatStepAsync(DurableChatInput input)
    {
        var ctx = ActivityExecutionContext.HasCurrent ? ActivityExecutionContext.Current : null;

        _logger.LogDebug(
            "Executing durable chat step activity for conversation {ConversationId}, turn {TurnNumber}",
            input.ConversationId, input.TurnNumber);

        var modelId = input.Options?.ModelId;
        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            $"{DurableChatTelemetry.ChatOperationName} {modelId ?? "unknown"}",
            System.Diagnostics.ActivityKind.Client);

        SetupSpanTags(span, input.ConversationId, modelId);

        // Auto-populate tools from the registry if the caller didn't supply any (OD-1).
        // If ChatOptions.Tools is explicitly provided we respect that subset choice — but
        // any entries that survived the wire are ToolNamePlaceholder instances (the converter
        // only round-trips names) so we must swap them for the real AIFunction references
        // before they reach the LLM.
        var registry = services.GetService<DurableFunctionRegistry>();
        var effectiveOptions = SwapPlaceholderTools(input.Options);
        if (registry is { Count: > 0 } && (effectiveOptions?.Tools is null or { Count: 0 }))
        {
            effectiveOptions = effectiveOptions is null
                ? new ChatOptions()
                : effectiveOptions.Clone();
            // AIFunction : AITool — direct spread, no intermediate iterator needed.
            effectiveOptions.Tools = [..registry.Values];
        }

        var chatClient = ResolveChatClient(input.ClientKey);

        var response = await StreamAndCollectAsync(
            chatClient, input.Messages, effectiveOptions, input, span, ctx)
            .ConfigureAwait(false);

        // Coalesce all assistant messages from the response into a single ChatMessage
        // carrying every content item. Streaming responses may split content across
        // multiple chunks; the workflow loop just needs one assistant message to
        // append to its accumulated transcript.
        var (assistantMessage, toolCalls, isFinal) = CollectAssistantContents(response);

        _logger.LogDebug(
            "Durable chat step activity completed for conversation {ConversationId}, turn {TurnNumber} " +
            "(IsFinal={IsFinal}, ToolCalls={ToolCallCount})",
            input.ConversationId, input.TurnNumber, isFinal, toolCalls.Count);

        return new DurableChatStepResult
        {
            IsFinal = isFinal,
            AssistantMessage = assistantMessage,
            ToolCalls = isFinal ? null : toolCalls,
            Usage = response.Usage,
        };
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
            span?.SetTag(DurableChatTelemetry.ResponseModelAttribute, response.ModelId);

            return response;
        }
        catch (Exception ex)
        {
            span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "Durable chat activity failed for conversation {ConversationId}, turn {TurnNumber}",
                input.ConversationId, input.TurnNumber);
            throw;
        }
    }

    /// <summary>
    /// Coalesces assistant messages in <paramref name="response"/> into a single
    /// <see cref="ChatMessage"/> and extracts tool-call requests. Used by
    /// <see cref="GetChatStepAsync"/> to build a <see cref="DurableChatStepResult"/>.
    /// </summary>
    private static (ChatMessage AssistantMessage, List<FunctionCallContent> ToolCalls, bool IsFinal)
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
        var isFinal = toolCalls.Count == 0;

        return (assistantMessage, toolCalls, isFinal);
    }

    /// <summary>
    /// Replaces any <see cref="ToolNamePlaceholder"/> entries left over from wire
    /// deserialization with the real <see cref="AIFunction"/> instances from
    /// <see cref="DurableFunctionRegistry"/>. <see cref="ChatOptions.Tools"/> serializes
    /// as a list of names only (see <see cref="ChatOptionsToolsJsonConverter"/>); placeholders
    /// reaching the LLM would either throw on invocation (Pattern 1) or be ignored as
    /// non-callable tools. Returns the input unchanged when there are no placeholders to
    /// swap, so allocation is paid only on the explicit-subset path.
    /// </summary>
    private ChatOptions? SwapPlaceholderTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools)
        {
            return options;
        }

        var hasPlaceholder = false;
        foreach (var tool in tools)
        {
            if (tool is ToolNamePlaceholder)
            {
                hasPlaceholder = true;
                break;
            }
        }
        if (!hasPlaceholder)
        {
            return options;
        }

        var registry = services.GetService<DurableFunctionRegistry>();
        var resolved = options.Clone();
        var newTools = new List<AITool>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool is ToolNamePlaceholder placeholder)
            {
                if (registry is not null
                    && registry.TryGetValue(placeholder.Name, out var realTool))
                {
                    newTools.Add(realTool);
                }
                else
                {
                    _logger.LogWarning(
                        "Tool '{ToolName}' not found in DurableFunctionRegistry; dropping from options.",
                        placeholder.Name);
                }
            }
            else
            {
                newTools.Add(tool);
            }
        }
        resolved.Tools = newTools;
        return resolved;
    }

    /// <summary>
    /// Sets up the shared OTel span tags for both Pattern 1 and Pattern 3 activities.
    /// </summary>
    private static void SetupSpanTags(
        System.Diagnostics.Activity? span,
        string? conversationId,
        string? modelId)
    {
        span?.SetTag(DurableChatTelemetry.OperationNameAttribute, DurableChatTelemetry.ChatOperationName);
        span?.SetTag(DurableChatTelemetry.ConversationIdAttribute, conversationId);
        span?.SetTag(DurableChatTelemetry.RequestModelAttribute, modelId);
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
    /// Throws when the LLM returned <see cref="FunctionCallContent"/> items but no
    /// <c>FunctionInvokingChatClient</c> is in the chat-client chain to handle them inline,
    /// AND durable tools are registered (meaning the user expects per-tool dispatch).
    /// Pattern 3 routes through <see cref="GetChatStepAsync"/> rather than this activity,
    /// so a tool call landing here with no FIC and a populated registry means the workflow
    /// is the middleware path (<c>DurableChatClient</c>) — which cannot host a tool-dispatch
    /// loop by contract.
    /// </summary>
    /// <remarks>
    /// Thrown as a non-retryable <see cref="ApplicationFailureException"/> rather than a
    /// plain <see cref="DurableToolsNotWrappedException"/> because the underlying error is a
    /// configuration bug, not a transient failure — retrying the activity will produce the
    /// same result every time. Without <c>nonRetryable: true</c>, Temporal's default retry
    /// policy (unlimited attempts with exponential backoff) would burn ~80 retries before
    /// surfacing the misconfiguration to the workflow caller. <c>ErrorType</c> is set to
    /// <c>nameof(DurableToolsNotWrappedException)</c> so catch blocks can still match on
    /// the typed name via <see cref="ApplicationFailureException.ErrorType"/>.
    /// </remarks>
    private void EnsureToolDispatchHandlerWired(IChatClient chatClient, ChatResponse response)
    {
        var registry = services.GetService<DurableFunctionRegistry>();
        if (registry is null || registry.Count == 0)
        {
            return;
        }

        // Did the LLM ask us to invoke a tool?
        var responseHasToolCalls = false;
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent)
                {
                    responseHasToolCalls = true;
                    break;
                }
            }
            if (responseHasToolCalls) break;
        }

        if (!responseHasToolCalls)
        {
            return;
        }

        // Use AgentChainWalker for consistency with EnsureMixedPatternCheck.
        if (Internal.AgentChainWalker.Contains<FunctionInvokingChatClient>(chatClient))
        {
            return;
        }

        throw new ApplicationFailureException(
            DurableToolsNotWrappedException.DefaultMessage,
            errorType: nameof(DurableToolsNotWrappedException),
            nonRetryable: true);
    }
}
