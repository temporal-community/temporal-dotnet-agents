using System.Text.Json.Serialization;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Input for the <see cref="DurableChatWorkflow"/>.
/// </summary>
public class DurableChatWorkflowInput
{
    /// <summary>
    /// The session time-to-live. The workflow completes when idle for this duration.
    /// </summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Conversation history carried forward from a previous run (continue-as-new).
    /// </summary>
    public List<DurableSessionEntry>? CarriedHistory { get; init; }

    /// <summary>
    /// Activity timeout for LLM calls.
    /// </summary>
    public TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Heartbeat timeout for LLM call activities.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maximum time to wait for a human to respond to a tool approval request.
    /// Defaults to 7 days.
    /// </summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// When <see langword="true"/>, the workflow upserts <c>TurnCount</c> and
    /// <c>SessionCreatedAt</c> typed search attributes after workflow start and after each
    /// completed turn. Subclasses may upsert additional library-specific attributes via the
    /// <see cref="DurableChatWorkflowBase{TOutput}"/> hooks. Defaults to <see langword="false"/>.
    /// Requires pre-registration of these attributes with the Temporal server.
    /// </summary>
    public bool EnableSearchAttributes { get; init; }

    /// <summary>
    /// Maximum number of <see cref="DurableSessionEntry"/> instances retained in the
    /// conversation history before a continue-as-new transition is triggered. Defaults to 1000.
    /// </summary>
    public int MaxEntryCount { get; init; } = 1000;

    /// <summary>
    /// Optional reducer applied to conversation history before a continue-as-new transition.
    /// Not serialized — the session client re-supplies this on each workflow start.
    /// </summary>
    [JsonIgnore]
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer { get; init; }

    /// <summary>
    /// The UTC timestamp at which the session was originally created.
    /// Populated on the first run and carried forward through continue-as-new transitions
    /// so that <c>SessionCreatedAt</c> always reflects the true session start time.
    /// </summary>
    public DateTimeOffset? OriginalCreatedAt { get; init; }

    /// <summary>
    /// Per-tool <see cref="ActivityOptions"/> resolved at session start by the
    /// <see cref="DurableChatSessionClient"/> for every tool registered via
    /// <see cref="DurableAIServiceCollectionExtensions.AddDurableTools(global::Temporalio.Extensions.Hosting.ITemporalWorkerServiceOptionsBuilder, global::Microsoft.Extensions.AI.AIFunction[])"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non-null with at least one entry indicates Pattern 3 (durable tool dispatch loop) is
    /// active for this workflow. Null or empty indicates Pattern 1 (inline tool execution
    /// inside the single chat activity). The activation decision is frozen into workflow
    /// history at session start so replay is deterministic regardless of which worker
    /// process picks it up. Carried forward verbatim through continue-as-new transitions.
    /// </para>
    /// <para>
    /// <b>Mid-session drift:</b> per-tool options are frozen at session start. A new
    /// <c>AddDurableTools</c> registered after the session begins will NOT affect this
    /// session — its options dict was already captured into workflow history. Newly
    /// registered tools are picked up by sessions started after the registration.
    /// </para>
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, ActivityOptions>? ToolActivityOptions { get; init; }

    /// <summary>
    /// Maximum number of LLM iterations the Pattern 3 dispatch loop will execute before
    /// synthesizing an "iterations exceeded" sentinel response and aborting the turn.
    /// Defaults to 20. Mirrors MAF's <c>DurableAgentBuilder.MaxToolCallsPerTurn</c>.
    /// </summary>
    public int MaxToolCallsPerTurn { get; init; } = 20;

    /// <summary>
    /// Maximum number of consecutive iterations in which one or more tools may fail
    /// before the workflow surfaces a non-retryable <c>ApplicationFailureException</c>.
    /// Defaults to <c>3</c>. Set to <c>0</c> for immediate propagation (MAF-style
    /// behavior where the first tool failure aborts the turn).
    /// </summary>
    /// <remarks>
    /// The counter increments on any iteration that contains at least one tool failure
    /// and resets to zero on the next all-success iteration. When the threshold is
    /// exceeded, the workflow throws a non-retryable failure so the caller is informed.
    /// </remarks>
    public int MaximumConsecutiveErrorsPerRequest { get; init; } = 3;

    /// <summary>
    /// When <see langword="true"/>, synthesized tool-error
    /// <c>FunctionResultContent</c> messages include the underlying exception type and
    /// message. When <see langword="false"/> (default), only a generic
    /// "Tool invocation failed." message is fed back to the LLM.
    /// </summary>
    public bool IncludeDetailedErrors { get; init; }
}
