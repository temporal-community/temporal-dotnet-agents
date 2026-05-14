using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Activities;
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
        var ct = ctx?.CancellationToken ?? CancellationToken.None;

        _logger.LogDebug(
            "Executing durable chat activity for conversation {ConversationId}, turn {TurnNumber}",
            input.ConversationId, input.TurnNumber);

        var modelId = input.Options?.ModelId;
        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            $"{DurableChatTelemetry.ChatOperationName} {modelId ?? "unknown"}",
            System.Diagnostics.ActivityKind.Client);

        span?.SetTag(DurableChatTelemetry.OperationNameAttribute, DurableChatTelemetry.ChatOperationName);
        span?.SetTag(DurableChatTelemetry.ConversationIdAttribute, input.ConversationId);
        span?.SetTag(DurableChatTelemetry.RequestModelAttribute, modelId);

        var chatClient = string.IsNullOrEmpty(input.ClientKey)
            ? services.GetRequiredService<IChatClient>()
            : services.GetRequiredKeyedService<IChatClient>(input.ClientKey);

        // Step 4d: B-check backstop for the MEAI mixed-pattern conflict. The startup A-check
        // (DurableMixedPatternValidator) only walks the unkeyed default IChatClient. This
        // backstop catches keyed-only setups, factory-deferred resolutions, and other paths
        // the A-check cannot reach. Per-client cache so the walk runs at most once per
        // resolved instance.
        EnsureMixedPatternCheck(chatClient);

        // Step 4c: per-call IChatClientDecorator resolution. Per-call WithChatClientFactoryKey
        // wins; worker-level DefaultChatClientFactoryKey is the fallback. Empty-string per-call
        // value is the documented opt-out (overrides the worker default with "no decoration").
        var factoryKey = input.Options.GetChatClientFactoryKey()
            ?? services.GetService<DurableExecutionOptions>()?.DefaultChatClientFactoryKey;

        if (!string.IsNullOrEmpty(factoryKey))
        {
            var decorator = services.GetKeyedService<IChatClientDecorator>(factoryKey)
                ?? throw new DurableChatClientFactoryNotFoundException(factoryKey);
            chatClient = decorator.Decorate(chatClient, input.Options);
        }

        try
        {
            var collected = new List<ChatResponseUpdate>();
            await foreach (var update in chatClient.GetStreamingResponseAsync(
                    input.Messages, input.Options, ct)
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

            _logger.LogDebug(
                "Durable chat activity completed for conversation {ConversationId}, turn {TurnNumber}",
                input.ConversationId, input.TurnNumber);

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
}
