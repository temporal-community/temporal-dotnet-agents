using System.Text.Json;
using System.Text.Json.Serialization;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.AI;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Input passed to <see cref="AgentWorkflow"/> when starting a new run.
/// Inherits shared session-loop fields (<see cref="DurableChatWorkflowInput.MaxEntryCount"/>,
/// <see cref="DurableChatWorkflowInput.HistoryReducerKey"/>, <see cref="DurableChatWorkflowInput.OriginalCreatedAt"/>,
/// <see cref="DurableChatWorkflowInput.EnableSearchAttributes"/>, <see cref="DurableChatWorkflowInput.CarriedHistory"/>)
/// from <see cref="DurableChatWorkflowInput"/> per Layer 3 Decision #1.
/// MAF-specific fields (<see cref="AgentName"/>, <see cref="TaskQueue"/>,
/// <see cref="CarriedStateBag"/>, etc.) live on this subclass.
/// </summary>
internal sealed record class AgentWorkflowInput : DurableChatWorkflowInput
{
    /// <summary>
    /// Optional first fire-and-forget request carried by a delayed workflow start.
    /// This is cleared when creating the next run.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunRequest? InitialFireAndForgetRequest { get; init; }

    /// <summary>Gets the name of the agent that this workflow manages.</summary>
    public required string AgentName { get; init; }

    /// <summary>Gets the task queue on which <see cref="AgentActivities"/> are registered.</summary>
    public required string TaskQueue { get; init; }

    /// <summary>
    /// Gets the serialized <see cref="AgentSessionStateBag"/> carried forward from a
    /// previous run (for continue-as-new scenarios). Allows AIContextProvider state
    /// (e.g. Mem0 thread IDs) to survive workflow continuation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? CarriedStateBag { get; init; }

    /// <summary>Revision of the workflow-owned StateBag carried into this run.</summary>
    public long CarriedStateBagRevision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalMemoryScopeSnapshot? CarriedMemoryScope { get; init; }

    public bool MemoryInitializationChecked { get; init; }

    // RetryPolicy is inherited from DurableChatWorkflowInput and applies to the model step;
    // tools use one attempt unless an explicit per-agent policy opts all tools into retries.

    /// <summary>
    /// Gets the worker-owned execution settings supplied by the workflow instance factory and
    /// carried in continue-as-new input.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentWorkerToolConfig? ResolvedWorkerConfig { get; init; }

    // ── Workflow accessors for the worker-owned execution settings ───────────

    /// <summary>
    /// Maximum number of LLM-step iterations within a single agent turn. Each iteration may
    /// dispatch a serial batch of tool activities. When the cap is exceeded the workflow
    /// returns a structured error response. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="AgentWorkerToolConfig.MaxToolCallsPerTurn"/>.
    /// </summary>
    /// <remarks>
    /// Shadows <see cref="DurableChatWorkflowInput.MaxToolCallsPerTurn"/>. The base property
    /// belongs to MEAI's Pattern 3 dispatch loop and is irrelevant for the MAF agent workflow
    /// (which forwards through <see cref="ResolvedWorkerConfig"/>). The <c>new</c> keyword
    /// preserves the existing MAF forwarding semantics.
    /// </remarks>
    [JsonIgnore]
    public new int MaxToolCallsPerTurn => ResolvedWorkerConfig?.MaxToolCallsPerTurn ?? 20;

    /// <summary>
    /// Pre-computed per-tool <see cref="ActivityOptions"/> indexed by tool name (case-insensitive).
    /// Forwards to <see cref="ResolvedWorkerConfig"/>.<see cref="AgentWorkerToolConfig.ToolActivityOptions"/>;
    /// <see langword="null"/> when no durable tools are registered.
    /// </summary>
    /// <remarks>
    /// The worker instance factory builds this map before workflow initialization. Continue-as-new
    /// carries the same values forward so retry semantics survive across transitions.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ActivityOptions>? DurableAgentToolActivityOptions =>
        ResolvedWorkerConfig?.ToolActivityOptions;

    // ── Feature L forwarding properties ──

    /// <summary>
    /// Pre-computed <see cref="ActivityOptions"/> for <c>RunToolInterceptor</c> dispatches.
    /// Forwards to <see cref="ResolvedWorkerConfig"/>.<see cref="AgentWorkerToolConfig.InterceptorActivityOptions"/>;
    /// <see langword="null"/> when no interceptor is configured or config is not yet resolved.
    /// </summary>
    [JsonIgnore]
    public new ActivityOptions? InterceptorActivityOptions =>
        ResolvedWorkerConfig?.InterceptorActivityOptions;

    /// <summary>
    /// Per-tool <see cref="ActivityOptions"/> for interceptor dispatches with custom timeouts.
    /// Forwards to <see cref="ResolvedWorkerConfig"/>.<see cref="AgentWorkerToolConfig.InterceptorToolActivityOptions"/>.
    /// </summary>
    [JsonIgnore]
    public new IReadOnlyDictionary<string, ActivityOptions>? InterceptorToolActivityOptions =>
        ResolvedWorkerConfig?.InterceptorToolActivityOptions;

    /// <summary>
    /// Names of tools that skip the interceptor. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="AgentWorkerToolConfig.InterceptorSkippedTools"/>.
    /// </summary>
    [JsonIgnore]
    public new IReadOnlyList<string>? InterceptorSkippedTools =>
        ResolvedWorkerConfig?.InterceptorSkippedTools;

    /// <summary>
    /// Names of tools that always require human approval (Rule 2). Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="AgentWorkerToolConfig.RequiresApprovalTools"/>.
    /// </summary>
    [JsonIgnore]
    public new IReadOnlyList<string>? RequiresApprovalTools =>
        ResolvedWorkerConfig?.RequiresApprovalTools;


}
