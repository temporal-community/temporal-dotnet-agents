using Microsoft.Extensions.AI;
using Temporalio.Common;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Configuration options for durable AI execution via Temporal.
/// </summary>
/// <remarks>
/// Property names on this class are unprefixed (e.g. <c>ActivityTimeout</c>). The MAF
/// counterpart <c>TemporalCommunity.Extensions.Agents.TemporalAgentsOptions</c> uses
/// <c>Default*</c>-prefixed names for worker-level defaults (e.g. <c>DefaultActivityTimeout</c>).
/// This asymmetry is intentional — do not rename properties on either class.
/// </remarks>
/// <seealso cref="global::TemporalCommunity.Extensions.Agents.TemporalAgentsOptions"/>
public sealed class DurableExecutionOptions
{
    /// <summary>
    /// Gets or sets the Temporal task queue for durable AI execution. Managed chat workflows start
    /// on this queue. Direct chat and embedding adapters assign it to each scheduled activity, so
    /// their workflow worker may poll a different queue from the AI activity worker. Must be set
    /// before use. <see cref="AIFunctionExtensions.AsDurable"/> function activities instead use the
    /// calling workflow's task queue and do not read this property.
    /// </summary>
    public string? TaskQueue { get; set; }

    /// <summary>
    /// Gets or sets the activity start-to-close timeout for LLM calls. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan ActivityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the Temporal retry policy for LLM-call (and related) activities. When
    /// <see langword="null"/> (the default), a bounded backstop of
    /// <c>new RetryPolicy { MaximumAttempts = 5 }</c> is applied whenever the library dispatches
    /// a durable model activity rather than the Temporal server default
    /// (<c>MaximumAttempts = 0</c>, i.e. unlimited retries). This prevents an unrecoverable LLM
    /// error from retrying forever and hanging the workflow. Set an explicit policy to override
    /// the bounded default.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the workflow ID prefix for chat sessions. Defaults to "chat-".
    /// </summary>
    public string WorkflowIdPrefix { get; set; } = "chat-";

    /// <summary>
    /// Gets or sets the session time-to-live. Defaults to 14 days.
    /// </summary>
    public TimeSpan SessionTimeToLive { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Gets or sets the activity heartbeat timeout. Defaults to 2 minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The heartbeat timeout must be safely longer than the expected time between individual
    /// token chunks (typically a few seconds), not the total call latency. The 2-minute default
    /// is intentionally conservative to accommodate slow or throttled models.
    /// </para>
    /// <para>
    /// Setting a heartbeat timeout that is shorter than the time between successive streaming
    /// chunks will cause the activity to be force-failed mid-execution by the Temporal server,
    /// even if the LLM eventually responds successfully.
    /// </para>
    /// <para>
    /// Per-request overrides are available via
    /// <see cref="TemporalChatOptionsExtensions.WithHeartbeatTimeout"/>.
    /// </para>
    /// </remarks>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the maximum time to wait for a human to respond to a tool approval request.
    /// Defaults to 7 days.
    /// </summary>
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets whether <c>TurnCount</c> and <c>SessionCreatedAt</c> typed search attributes
    /// are upserted on the workflow. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Requires <c>TurnCount</c> (Long) and <c>SessionCreatedAt</c> (Datetime) to be
    /// pre-registered on the Temporal server before the first workflow start.
    /// Use the Temporal CLI: <c>temporal operator search-attribute create</c>.
    /// </remarks>
    public bool EnableSearchAttributes { get; set; }

    /// <summary>
    /// Default keyed DI service key used to resolve <see cref="IChatClient"/>.
    /// When null (default), the unkeyed registration is used.
    /// Per-call overrides via <see cref="TemporalChatOptionsExtensions.WithChatClientKey"/> take precedence.
    /// </summary>
    public string? DefaultChatClientKey { get; set; }

    /// <summary>
    /// Gets or sets a reducer applied to conversation history before a continue-as-new transition.
    /// When <see langword="null"/> (default) and <see cref="DefaultHistoryReducerKey"/> is also
    /// <see langword="null"/>, <c>DefaultBoundedTrim</c> is applied: it keeps the most-recent
    /// <c>Max(1, MaxEntryCount/2)</c> entries when history reaches <c>MaxEntryCount</c>; when
    /// <c>MaxEntryCount</c> is not the trigger (SDK-suggested CAN), history is carried forward
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property is provided for in-process and unit-test scenarios where a delegate can be
    /// supplied directly. For production durable workflows, prefer
    /// <see cref="DefaultHistoryReducerKey"/> — the key is serialized and survives the wire,
    /// so the reducer reliably fires at every continue-as-new boundary including after worker
    /// restarts and replay. If both are set, <see cref="DefaultHistoryReducerKey"/> takes
    /// precedence for the durable path.
    /// </para>
    /// <para>
    /// <b>Workflow determinism:</b> the reducer runs inside a Temporal activity (not on the
    /// workflow thread) and may be async-capable, but the delegate itself must be pure and
    /// deterministic — same inputs must always produce the same output.
    /// </para>
    /// </remarks>
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer { get; set; }

    /// <summary>
    /// Gets or sets the keyed-service key used to resolve the history-reducer delegate from DI.
    /// When non-null, the session client sets this key on the workflow input and the worker
    /// dispatches a <c>ReduceHistoryByKey</c> activity at continue-as-new time to apply the reducer.
    /// The key is serialized and survives continue-as-new transitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Register the reducer in DI before calling <c>AddDurableAI</c>:
    /// <code>
    /// services.AddKeyedSingleton&lt;Func&lt;IList&lt;DurableSessionEntry&gt;, IList&lt;DurableSessionEntry&gt;&gt;&gt;(
    ///     "my-reducer", (sp, key) => history => history.TakeLast(50).ToList());
    /// opts.DefaultHistoryReducerKey = "my-reducer";
    /// </code>
    /// </para>
    /// <para>
    /// <b>Determinism requirement:</b> the registered delegate must be pure and deterministic.
    /// An implementation that changes behaviour between deployments without a key change is a
    /// nondeterminism hazard for in-flight sessions (treat reducer changes like workflow-code
    /// changes: new key or <c>Workflow.Patched</c>).
    /// </para>
    /// </remarks>
    public string? DefaultHistoryReducerKey { get; set; }

    /// <summary>
    /// Gets or sets whether to register the default <see cref="DurableChatWorkflow"/> and
    /// <see cref="DurableChatSessionClient"/>. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set to <see langword="false"/> when using a custom workflow derived from
    /// <see cref="DurableChatWorkflowBase{TOutput}"/>. The workflow and session client are
    /// still required for most applications, so only disable if you are providing your own
    /// workflow implementation and do not need <see cref="DurableChatSessionClient"/>.
    /// </para>
    /// <para>
    /// Disabling this only skips the default workflow and session client registration.
    /// All supporting infrastructure (options, DataConverter, activities, embeddings, and
    /// <see cref="IDurableChatWorkflowInputFactory"/>) is still registered regardless of this
    /// setting.
    /// </para>
    /// </remarks>
    public bool RegisterDefaultWorkflow { get; set; } = true;

    /// <summary>
    /// Gets or sets the ordered named toolsets that form the stock workflow's worker-owned
    /// baseline. A <see langword="null"/> value uses the implicit toolset populated by
    /// <c>AddDurableTools</c>; an empty list creates a no-tools baseline.
    /// </summary>
    /// <remarks>
    /// Do not combine this property with <c>AddDurableTools</c>. Use
    /// <c>AddDurableToolset</c> for every selected ID. IDs use exact ordinal comparison and are
    /// resolved once for each new session.
    /// </remarks>
    public IReadOnlyList<string>? DefaultToolsetIds { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of LLM iterations the durable tool loop will
    /// execute before synthesizing an "iterations exceeded" sentinel response and aborting
    /// the turn. Defaults to <c>20</c>.
    /// </summary>
    /// <remarks>
    /// Relevant when durable tools are registered via
    /// <see cref="DurableAIServiceCollectionExtensions.AddDurableTools(global::Temporalio.Extensions.Hosting.ITemporalWorkerServiceOptionsBuilder, global::Microsoft.Extensions.AI.AIFunction[])"/>).
    /// </remarks>
    public int MaxToolCallsPerTurn { get; set; } = 20;

    /// <summary>
    /// Gets or sets the maximum number of consecutive failed model steps or iterations in which
    /// one or more tools fail before the durable tool loop surfaces a non-retryable
    /// <c>ApplicationFailureException</c>. Defaults to <c>3</c>. Set to <c>0</c> for
    /// immediate propagation (MAF-style behavior where the first tool failure aborts the turn).
    /// </summary>
    public int MaximumConsecutiveErrorsPerRequest { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether synthesized tool-error <c>FunctionResultContent</c> messages
    /// include the underlying exception type and message. When <see langword="false"/>
    /// (default), only a generic "Tool invocation failed." message is fed back to the LLM.
    /// </summary>
    public bool IncludeDetailedErrors { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of <see cref="DurableSessionEntry"/> instances retained
    /// in the conversation history before a continue-as-new transition is triggered.
    /// Defaults to 1000.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The workflow also continues-as-new when the Temporal SDK's internal event history
    /// threshold is reached (<c>Workflow.ContinueAsNewSuggested</c>), whichever comes first.
    /// Reduce this value to limit payload size on long-running sessions.
    /// </para>
    /// <para>
    /// <b>Note:</b> this property previously counted individual <see cref="ChatMessage"/>s.
    /// It now counts <see cref="DurableSessionEntry"/> instances (a request entry + a response
    /// entry per turn). The same numeric value retains roughly 2× the conversation depth.
    /// </para>
    /// </remarks>
    public int MaxEntryCount { get; set; } = 1000;

    /// <summary>
    /// Gets or sets a factory that creates a worker-level
    /// <see cref="IDurableToolInterceptor{TContext}"/> for intercepting tool calls before
    /// dispatch in the durable tool loop. When <see langword="null"/> (default), no
    /// interceptor activity is dispatched and tools are invoked directly.
    /// </summary>
    /// <remarks>
    /// The factory receives the worker-side <see cref="IServiceProvider"/>. Register
    /// the interceptor implementation in DI and resolve it via the factory:
    /// <code>
    /// opts.DefaultToolInterceptor = sp => sp.GetRequiredService&lt;MyInterceptor&gt;();
    /// </code>
    /// For worker-owned toolsets, the resolver records whether the interceptor is enabled and
    /// freezes its activity policy into the session manifest. The advanced caller-owned mode
    /// freezes the equivalent policy directly into <c>DurableChatWorkflowInput</c>.
    /// </remarks>
    public Func<IServiceProvider, IDurableToolInterceptor<DurableToolContext>>? DefaultToolInterceptor
    {
        get; set;
    }

    internal void Validate()
    {
        if (string.IsNullOrEmpty(TaskQueue))
        {
            throw new InvalidOperationException(
                $"{nameof(TaskQueue)} must be set in {nameof(DurableExecutionOptions)}.");
        }

        if (ActivityTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ActivityTimeout must be a positive duration.");
        }

        if (HeartbeatTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("HeartbeatTimeout must be a positive duration.");
        }

        if (SessionTimeToLive <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("SessionTimeToLive must be a positive duration.");
        }

        if (ApprovalTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ApprovalTimeout must be a positive duration.");
        }

        if (MaxEntryCount <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxEntryCount)} must be greater than zero in {nameof(DurableExecutionOptions)}.");
        }

        if (MaxToolCallsPerTurn <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxToolCallsPerTurn)} must be greater than zero in {nameof(DurableExecutionOptions)}.");
        }

        if (MaximumConsecutiveErrorsPerRequest < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaximumConsecutiveErrorsPerRequest)} cannot be negative in {nameof(DurableExecutionOptions)}.");
        }

        if (DefaultToolsetIds is not null)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in DefaultToolsetIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException(
                        $"{nameof(DefaultToolsetIds)} cannot contain an empty identifier.");
                }

                if (!ids.Add(id))
                {
                    throw new InvalidOperationException(
                        $"{nameof(DefaultToolsetIds)} contains duplicate identifier '{id}'.");
                }
            }
        }
    }
}
