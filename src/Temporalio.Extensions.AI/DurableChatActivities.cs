using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        var chatClient = ResolveChatClient(input.ClientKey);

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

            // Safety net for the silent-failure footgun (Pattern 3 design: OD-6).
            // If the user registered durable tools but neither (a) FunctionInvokingChatClient
            // is in the chain to handle them inline, nor (b) the workflow is the Pattern 3
            // dispatch loop (which routes through GetChatStepAsync, not this activity),
            // tool calls would be silently dropped. Throw to surface the misconfiguration.
            EnsureToolDispatchHandlerWired(chatClient, response);

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
        var ct = ctx?.CancellationToken ?? CancellationToken.None;

        _logger.LogDebug(
            "Executing durable chat step activity for conversation {ConversationId}, turn {TurnNumber}",
            input.ConversationId, input.TurnNumber);

        var modelId = input.Options?.ModelId;
        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            $"{DurableChatTelemetry.ChatOperationName} {modelId ?? "unknown"}",
            System.Diagnostics.ActivityKind.Client);

        span?.SetTag(DurableChatTelemetry.OperationNameAttribute, DurableChatTelemetry.ChatOperationName);
        span?.SetTag(DurableChatTelemetry.ConversationIdAttribute, input.ConversationId);
        span?.SetTag(DurableChatTelemetry.RequestModelAttribute, modelId);

        // Auto-populate tools from the registry if the caller didn't supply any (OD-1).
        // If ChatOptions.Tools is explicitly provided we respect that subset choice.
        var registry = services.GetService<DurableFunctionRegistry>();
        var effectiveOptions = input.Options;
        if (registry is { Count: > 0 } && (effectiveOptions?.Tools is null or { Count: 0 }))
        {
            effectiveOptions = effectiveOptions is null
                ? new ChatOptions()
                : effectiveOptions.Clone();
            // AIFunction : AITool — direct cast, no custom conversion needed.
            effectiveOptions.Tools = registry.Values.Cast<AITool>().ToList();
        }

        var chatClient = ResolveChatClient(input.ClientKey);

        try
        {
            var collected = new List<ChatResponseUpdate>();
            await foreach (var update in chatClient.GetStreamingResponseAsync(
                    input.Messages, effectiveOptions, ct)
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

            // Coalesce all assistant messages from the response into a single ChatMessage
            // carrying every content item. Streaming responses may split content across
            // multiple chunks; the workflow loop just needs one assistant message to
            // append to its accumulated transcript.
            var assistantContents = new List<AIContent>();
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
        catch (Exception ex)
        {
            span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "Durable chat step activity failed for conversation {ConversationId}, turn {TurnNumber}",
                input.ConversationId, input.TurnNumber);
            throw;
        }
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
    /// Throws <see cref="DurableToolsNotWrappedException"/> when the LLM returned
    /// <see cref="FunctionCallContent"/> items but neither (a) a
    /// <c>FunctionInvokingChatClient</c> is in the chat-client chain to handle them inline,
    /// nor (b) durable tools are registered (the registry being empty means the user is not
    /// trying to use Pattern 2 either). Pattern 3 routes through
    /// <see cref="GetChatStepAsync"/> rather than this activity, so a tool call landing here
    /// with no FIC and a populated registry means the workflow is the middleware path
    /// (<c>DurableChatClient</c>) — which cannot host a tool-dispatch loop by contract.
    /// </summary>
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

        // MEAI's FunctionInvokingChatClient (and any wrapping DelegatingChatClient) exposes
        // itself via GetService(typeof(FunctionInvokingChatClient)). If it's present anywhere
        // in the chain the inline dispatch path is wired up correctly.
        if (chatClient.GetService(typeof(FunctionInvokingChatClient)) is not null)
        {
            return;
        }

        throw new DurableToolsNotWrappedException();
    }
}
