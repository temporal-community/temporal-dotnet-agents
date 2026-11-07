using Temporalio.Common;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>Temporal execution settings and tool middleware for one registered agent.</summary>
/// <remarks>
/// MAF behavior belongs in <see cref="Microsoft.Agents.AI.ChatClientAgentOptions"/>. This type
/// contains per-agent workflow/activity settings and the durable tool-activity callback.
/// Unset execution settings inherit worker defaults.
/// </remarks>
public sealed class TemporalAgentOptions
{
    private TemporalChatHistoryMemoryBuilder? _chatHistoryMemory;

    /// <summary>
    /// Configures MAF's <see cref="ChatHistoryMemoryProvider"/> through the durable Temporal
    /// memory lifecycle.
    /// </summary>
    /// <param name="configure">Configures the vector store, optional trusted scope initializer, and recall options.</param>
    /// <returns>This options instance.</returns>
    /// <remarks>
    /// The provider's state is initialized in a dedicated activity before the first model call and
    /// is retained in the workflow-owned session StateBag. The initializer has a 30-second
    /// start-to-close timeout and two maximum attempts (one retry after one second), with no
    /// heartbeat timeout. If no initializer is supplied, storage and search are scoped to the
    /// current agent and Temporal session.
    /// </remarks>
    public TemporalAgentOptions UseChatHistoryMemory(
        Action<TemporalChatHistoryMemoryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_chatHistoryMemory is not null)
        {
            throw new InvalidOperationException("Chat-history memory is already configured.");
        }

        var builder = new TemporalChatHistoryMemoryBuilder();
        configure(builder);
        _chatHistoryMemory = builder;
        return this;
    }

    /// <summary>Gets or sets the session workflow time-to-live.</summary>
    public TimeSpan? TimeToLive { get; set; }

    /// <summary>Gets or sets the activity start-to-close timeout for model-step activities.</summary>
    public TimeSpan? ActivityTimeout { get; set; }

    /// <summary>Gets or sets the activity heartbeat timeout for model-step activities.</summary>
    public TimeSpan? HeartbeatTimeout { get; set; }

    /// <summary>Gets or sets the timeout for a pending per-call approval.</summary>
    public TimeSpan? ApprovalTimeout { get; set; }

    /// <summary>Gets or sets the retry policy for model-step activities.</summary>
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>Gets or sets the retained conversation-history entry limit.</summary>
    public int? MaxEntryCount { get; set; }

    /// <summary>Gets or sets the estimated model-context token limit.</summary>
    public int? MaxModelContextTokens { get; set; }

    /// <summary>Gets or sets the reserved model output tokens when a context limit is configured.</summary>
    public int? ModelOutputTokenReserve { get; set; }

    /// <summary>Gets or sets middleware executed inside each scoped tool-activity attempt.</summary>
    /// <remarks>
    /// Required approval precedes the callback. Call <c>next</c> at most once with the supplied
    /// context, without changing its function, call identity, arguments, or scoped services.
    /// Await the returned operation before returning. The function is a non-executable declaration;
    /// invoke it only through <c>next</c>. Messages and iteration metadata describe the initiating
    /// managed-session tool batch and are read-only; <c>Options</c> is null.
    /// Serializable short-circuit results and result transformations are supported.
    /// Set <see cref="FunctionInvocationContext.Terminate"/> to end the turn after this result.
    /// Activity retries can repeat callback and tool effects. The agent parameter exposes
    /// registration metadata, not an executable agent or a client proxy.
    /// </remarks>
    public Func<AIAgent, FunctionInvocationContext,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
        CancellationToken, ValueTask<object?>>? ToolMiddleware { get; set; }

    internal void ApplyTo(DurableAgentBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.TimeToLive = this.TimeToLive;
        builder.ActivityTimeout = this.ActivityTimeout;
        builder.HeartbeatTimeout = this.HeartbeatTimeout;
        builder.ApprovalTimeout = this.ApprovalTimeout;
        builder.RetryPolicy = this.RetryPolicy;
        builder.MaxEntryCount = this.MaxEntryCount;
        builder.MaxModelContextTokens = this.MaxModelContextTokens;
        builder.ModelOutputTokenReserve = this.ModelOutputTokenReserve;
        builder.ToolMiddleware = this.ToolMiddleware;
        if (_chatHistoryMemory is not null)
        {
            var registration = _chatHistoryMemory.CreateRegistration(builder.Name);
            builder.AddTemporalMemorySearchBridge(
                registration,
                "Allows searching for related previous chat history to help answer the user question.");
        }
    }

    internal void Validate(string agentName)
    {
        if (this.MaxEntryCount is < 4)
        {
            throw new InvalidOperationException(
                $"Temporal agent '{agentName}' MaxEntryCount must be at least four.");
        }

        if (this.MaxModelContextTokens is <= 0
            || this.ModelOutputTokenReserve is < 0
            || (this.MaxModelContextTokens is { } limit
                && this.ModelOutputTokenReserve is { } reserve
                && reserve >= limit))
        {
            throw new InvalidOperationException(
                $"Temporal agent '{agentName}' has an invalid model-context token budget.");
        }
    }
}
