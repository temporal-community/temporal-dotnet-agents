using System.Text.Json.Serialization;
using Temporalio.Common;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Input for the <see cref="DurableChatWorkflow"/>.
/// </summary>
public record class DurableChatWorkflowInput
{
    [JsonInclude]
    internal IReadOnlyList<Internal.DurableFunctionDeclarationSnapshot>? ToolDeclarations { get; init; }

    [JsonInclude]
    internal Internal.DurableToolsetManifest? ToolsetManifest { get; init; }

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
    /// Retry policy applied to dispatched activities (LLM calls and the durable tool-dispatch
    /// fallback). Resolved at session start from <c>DurableExecutionOptions.RetryPolicy</c>.
    /// </summary>
    /// <remarks>
    /// When a durable tool has no per-tool entry in <see cref="ToolActivityOptions"/>
    /// (defensive fallback in <c>DurableChatWorkflow.ResolveToolActivityOptions</c>), this value
    /// is applied so the tool activity does not fall back to Temporal's default policy
    /// (unlimited retries). A non-idempotent unregistered tool would otherwise retry forever.
    /// May be <see langword="null"/> when no policy was configured, in which case the per-tool
    /// options dictionary already carries the resolved policy for every registered tool.
    /// </remarks>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Maximum time to wait for a human to respond to a tool approval request.
    /// Defaults to 7 days.
    /// </summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Bounded approval decisions retained for idempotent reviewer retries across continue-as-new.
    /// </summary>
    public IReadOnlyList<Approvals.DurableApprovalDecision>? ApprovalResolutionHistory { get; init; }

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
    /// <remarks>
    /// This property is kept for in-process and unit-test use where the delegate can be
    /// supplied directly. For production durable workflows, use
    /// <see cref="HistoryReducerKey"/> instead: the key is serialized and survives the
    /// wire, so the reducer is reliably applied at every continue-as-new boundary
    /// (including after worker restarts and replay).
    /// </remarks>
    [JsonIgnore]
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer { get; init; }

    /// <summary>
    /// Keyed-service key under which
    /// <c>Func&lt;IList&lt;DurableSessionEntry&gt;, IList&lt;DurableSessionEntry&gt;&gt;</c>
    /// is registered in the worker's DI container. Serialized and carried forward through
    /// continue-as-new transitions. When non-null, the workflow dispatches a dedicated
    /// <c>ReduceHistoryByKey</c> activity to apply the reducer at CAN time. Mutually exclusive
    /// with <see cref="HistoryReducer"/>: if both are set, <see cref="HistoryReducerKey"/>
    /// takes precedence.
    /// </summary>
    /// <remarks>
    /// <b>Determinism requirement:</b> the delegate registered under this key must be pure
    /// and deterministic — same inputs must always produce the same output, and the delegate
    /// must not change behaviour between deployments without a key change. The reducer runs
    /// inside a Temporal activity that replays from history; an implementation that changes
    /// between deployments is a nondeterminism hazard for in-flight sessions.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HistoryReducerKey { get; init; }

    /// <summary>
    /// The UTC timestamp at which the session was originally created.
    /// Populated on the first run and carried forward through continue-as-new transitions
    /// so that <c>SessionCreatedAt</c> always reflects the true session start time.
    /// </summary>
    public DateTimeOffset? OriginalCreatedAt { get; init; }

    /// <summary>
    /// Per-tool <see cref="ActivityOptions"/> frozen into workflow input for the advanced
    /// caller-owned declaration mode. Worker-owned toolsets carry the corresponding options in
    /// their resolved manifest instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entries are frozen into workflow history at session start so tool activity behavior is
    /// replay-deterministic regardless of which worker processes a given turn. They are carried
    /// forward verbatim through continue-as-new transitions.
    /// </para>
    /// <para>
    /// <b>Mid-session drift:</b> caller-owned options are frozen at session start. Worker-owned
    /// options are frozen when the resolver activity records the session manifest. Later worker
    /// registration changes do not alter either recorded authority.
    /// </para>
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, ActivityOptions>? ToolActivityOptions { get; init; }

    /// <summary>
    /// Shared <see cref="ActivityOptions"/> used when dispatching a <c>RunToolInterceptor</c>
    /// activity. Non-null only when a <c>DefaultToolInterceptor</c> was registered at session
    /// start. A null value means no interceptor
    /// activities are dispatched.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActivityOptions? InterceptorActivityOptions { get; init; }

    /// <summary>
    /// Per-tool overrides for the <c>RunToolInterceptor</c> activity timeout.
    /// Keys are tool names (ordinal case-insensitive policy lookup). Only entries for tools that
    /// have a non-null <c>InterceptorTimeout</c> are present; all others use
    /// <see cref="InterceptorActivityOptions"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, ActivityOptions>? InterceptorToolActivityOptions { get; init; }

    /// <summary>
    /// Tool names that should be skipped when dispatching the <c>RunToolInterceptor</c>
    /// activity. Populated from tools where <c>SkipInterceptorFlag</c> is set.
    /// Ordinal case-insensitive comparisons apply at runtime. Worker-owned manifest membership
    /// remains an independent exact-ordinal check.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InterceptorSkippedTools { get; init; }

    /// <summary>
    /// Tool names that always require human approval before dispatch, regardless of
    /// what the interceptor returns. Populated unconditionally (no interceptor required).
    /// This is the BLOCK-2 fix: <c>RequireApproval()</c> is an absolute configuration-time
    /// floor independent of whether a <c>DefaultToolInterceptor</c> is registered.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiresApprovalTools { get; init; }

    /// <summary>
    /// Per-tool approval timeout overrides, captured at session start. A timeout applies
    /// whenever that tool enters an approval wait, whether the wait was required by the
    /// tool registration or by an interceptor decision.
    /// </summary>
    /// <remarks>
    /// Entries are carried forward through continue-as-new so an in-flight session is not
    /// changed by later worker configuration updates. Tools without an entry use
    /// <see cref="ApprovalTimeout"/>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, TimeSpan>? ToolApprovalTimeouts { get; init; }

    /// <summary>
    /// Maximum number of LLM iterations the durable tool loop will execute before
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
