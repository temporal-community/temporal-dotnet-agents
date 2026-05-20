using System.Text.Json;
using System.Text.Json.Serialization;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input passed to <see cref="AgentWorkflow"/> when starting a new run.
/// Inherits shared session-loop fields (<see cref="DurableChatWorkflowInput.MaxEntryCount"/>,
/// <see cref="DurableChatWorkflowInput.HistoryReducer"/>, <see cref="DurableChatWorkflowInput.OriginalCreatedAt"/>,
/// <see cref="DurableChatWorkflowInput.EnableSearchAttributes"/>, <see cref="DurableChatWorkflowInput.CarriedHistory"/>)
/// from <see cref="DurableChatWorkflowInput"/> per Layer 3 Decision #1.
/// MAF-specific fields (<see cref="AgentName"/>, <see cref="TaskQueue"/>,
/// <see cref="CarriedStateBag"/>, etc.) live on this subclass.
/// </summary>
/// <remarks>
/// Worker-side resolved settings (<see cref="MaxToolCallsPerTurn"/>,
/// <see cref="UseExternalStoreMode"/>, <see cref="DurableAgentToolActivityOptions"/>) are stored
/// in <see cref="ResolvedWorkerConfig"/> as of the maf-gap Step 3c.1 migration. The legacy
/// flat-field names remain as forwarding computed properties so consumers don't need updating;
/// only construction sites assign to <see cref="ResolvedWorkerConfig"/> directly.
/// </remarks>
internal sealed class AgentWorkflowInput : DurableChatWorkflowInput
{
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

    /// <summary>
    /// Gets the retry policy applied to the agent's <c>RunAgentStep</c> activity (the LLM call).
    /// When <see langword="null"/>, Temporal SDK defaults apply (unbounded retries). Per-tool
    /// retry policies are configured separately via <see cref="DurableAgentToolActivityOptions"/>.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Gets the bundle of worker-side settings resolved either eagerly at workflow start
    /// (when <c>AddDurableAgent</c> registered this worker) or lazily on the first step of the
    /// first turn (proxy-started workflows). <see langword="null"/> means proxy-started and not
    /// yet resolved — the workflow must request resolution via
    /// <see cref="AgentStepInput.NeedsWorkerSettingsResolution"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProxyResolvedWorkerConfig? ResolvedWorkerConfig { get; init; }

    // ── Forwarding properties — preserve consumer call sites across the Step 3c.1 migration ──

    /// <summary>
    /// Maximum number of LLM-step iterations within a single agent turn. Each iteration may
    /// dispatch a parallel batch of tool activities. When the cap is exceeded the workflow
    /// returns a structured error response. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.MaxToolCallsPerTurn"/>;
    /// defaults to <c>20</c> when the config has not yet been resolved (proxy-started, pre-handshake).
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
    /// When <see langword="true"/>, the agent has an <see cref="HistoryStore.IAgentHistoryStore"/>
    /// configured (per-agent or worker-level). Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.UseExternalStoreMode"/>;
    /// defaults to <see langword="false"/> when the config has not yet been resolved.
    /// </summary>
    [JsonIgnore]
    public bool UseExternalStoreMode => ResolvedWorkerConfig?.UseExternalStoreMode ?? false;

    /// <summary>
    /// Pre-computed per-tool <see cref="ActivityOptions"/> indexed by tool name (case-insensitive).
    /// Forwards to <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.ToolActivityOptions"/>;
    /// <see langword="null"/> when the config has not yet been resolved.
    /// </summary>
    /// <remarks>
    /// The dictionary is built at workflow start (not at first activity dispatch) so retry
    /// constraints — especially <c>MaximumAttempts = 1</c> on write tools — are pinned at the
    /// time the workflow began running. Continue-as-new carries the same dictionary forward so
    /// retry semantics survive across CAN transitions.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ActivityOptions>? DurableAgentToolActivityOptions =>
        ResolvedWorkerConfig?.ToolActivityOptions;

    /// <summary>
    /// <see langword="true"/> when worker-side settings are already baked into this input (i.e.,
    /// <see cref="ResolvedWorkerConfig"/> is non-<see langword="null"/>). <see langword="false"/>
    /// for proxy-started workflows that must resolve settings via the first-step handshake.
    /// </summary>
    [JsonIgnore]
    public bool WorkerSettingsResolved => ResolvedWorkerConfig is not null;
}
