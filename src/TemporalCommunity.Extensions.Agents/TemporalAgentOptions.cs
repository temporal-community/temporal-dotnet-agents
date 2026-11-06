using Temporalio.Common;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>Temporal execution settings for one registered agent.</summary>
/// <remarks>
/// MAF behavior belongs in <see cref="Microsoft.Agents.AI.ChatClientAgentOptions"/>. This type
/// contains only per-agent workflow and activity settings; unset values inherit worker defaults.
/// </remarks>
public sealed class TemporalAgentOptions
{
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
