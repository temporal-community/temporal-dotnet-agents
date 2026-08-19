using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Abstract base class for durable chat workflows with typed turn output.
/// Provides the shared session loop, conversation history, HITL approval support,
/// continue-as-new handling, search attribute upserts, and serialized turn execution.
/// Concrete subclasses implement the abstract members to dispatch to their own
/// activities and to convert per-turn output into <see cref="DurableSessionResponse"/>
/// entries that get appended to history.
/// </summary>
/// <typeparam name="TOutput">The type returned from each completed chat turn.</typeparam>
public abstract partial class DurableChatWorkflowBase<TOutput>
{
    /// <summary>
    /// The name of the workflow signal used to request graceful shutdown of a session.
    /// Use this constant when signalling shutdown externally rather than hard-coding the
    /// string <c>"Shutdown"</c> — keeping caller and handler in sync if the name ever changes.
    /// </summary>
    /// <seealso cref="IDurableChatSessionClient.ShutdownAsync"/>
    public const string ShutdownSignalName = "Shutdown";
    private List<DurableSessionEntry> _history = new(16);
    private readonly DurableApprovalMixin _approvalMixin = new();
    private bool _isProcessing;
    private bool _shutdownRequested;
    private int _turnCount;

    /// <summary>
    /// The workflow input set by <see cref="InitializeInput"/>.
    /// </summary>
    protected DurableChatWorkflowInput? Input { get; private set; }

    /// <summary>
    /// Non-nullable accessor for <see cref="Input"/>. Throws <see cref="InvalidOperationException"/>
    /// when accessed before <see cref="InitializeInput"/> has set the input. Prefer this over
    /// <c>Input!</c> suppression. Custom workflow run methods must call
    /// <see cref="InitializeInput"/> before their first await.
    /// </summary>
    protected DurableChatWorkflowInput RequiredInput =>
        Input ?? throw new InvalidOperationException(
            "RequiredInput accessed before RunAsync initialized Input.");

    /// <summary>
    /// Initializes start-input state synchronously before a workflow run method first yields.
    /// </summary>
    /// <remarks>
    /// Custom workflow run methods must call this immediately after validating their input and
    /// before their first await. The base run method calls it defensively. A later call in the same
    /// run may replace a provisional input with its resolved copy, but never clears pending or
    /// newly retained approval state. Workflow Update validators are synchronous and must not call
    /// workflow awaitables or depend on state that has not been initialized.
    /// </remarks>
    protected void InitializeInput(DurableChatWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var firstInitialization = Input is null;
        Input = input;
        if (firstInitialization)
        {
            _approvalMixin.RestoreResolvedApprovals(input.ApprovalResolutionHistory);
        }
    }

    /// <summary>
    /// Waits deterministically until the workflow run input has been initialized.
    /// </summary>
    /// <remarks>
    /// Use this only from asynchronous workflow handlers. Workflow Update validators are
    /// synchronous and must never call this method or any other workflow awaitable. A validator
    /// should admit an Update when initialization-dependent state is not ready so the handler can
    /// wait and then perform authoritative validation.
    /// </remarks>
    protected async Task<DurableChatWorkflowInput> WaitForInputAsync()
    {
        if (Input is null)
        {
            await Workflow.WaitConditionAsync(() => Input is not null).ConfigureAwait(true);
        }

        return RequiredInput;
    }

    /// <summary>
    /// Returns <see langword="true"/> once a <c>Shutdown</c> signal has been received.
    /// Subclass update validators can use this to reject new turns after shutdown.
    /// </summary>
    protected bool IsShutdownRequested => _shutdownRequested;

    /// <summary>
    /// The current turn count, available to subclass overrides for telemetry or
    /// activity-input fields. Updated inside <see cref="RunTurnAsync"/> after each turn
    /// completes, and initialized from carried history at the start of each run via
    /// <see cref="InitializeTurnCount"/>.
    /// </summary>
    protected int CurrentTurnNumber => _turnCount;

    /// <summary>
    /// The current conversation history, available to subclass overrides that need to
    /// pass the full flattened message log to their activity (e.g. so the LLM sees
    /// prior turns). The request entry for the current turn is appended to history
    /// <em>before</em> <see cref="ExecuteTurnAsync"/> is invoked, so the latest entry
    /// in this list is always the request that triggered the activity dispatch.
    /// </summary>
    protected IReadOnlyList<DurableSessionEntry> History => _history;

    // ── Abstract / virtual hooks ────────────────────────────────────────────

    /// <summary>
    /// Builds the response entry that gets appended to history after the activity completes.
    /// Subclasses convert their concrete <typeparamref name="TOutput"/> into a
    /// <see cref="DurableSessionResponse"/> (typically wrapping a <see cref="ChatResponse"/>).
    /// </summary>
    /// <param name="correlationId">Per-turn correlation identifier matching the request entry.</param>
    /// <param name="output">The activity's output for this turn.</param>
    /// <param name="createdAt">Workflow-time creation timestamp.</param>
    protected abstract DurableSessionResponse BuildResponseEntry(
        string correlationId,
        TOutput output,
        DateTimeOffset createdAt);

    /// <summary>
    /// Dispatches the LLM call (or equivalent) as a Temporal activity.
    /// Called by <see cref="RunTurnAsync"/> on each turn. The base no longer constructs
    /// a <see cref="DurableChatInput"/> on the subclass's behalf — subclasses own activity-input
    /// construction so they can include library-specific fields (e.g. MAF's
    /// <c>SerializedStateBag</c> / <c>AgentName</c>).
    /// </summary>
    /// <param name="activityOptions">
    /// Pre-built <see cref="ActivityOptions"/> with timeouts and summary populated from
    /// <see cref="DurableChatWorkflowInput"/> and <paramref name="chatOptions"/>.
    /// </param>
    /// <param name="requestEntry">
    /// The request entry that was just appended to history. Subclasses can extract
    /// <see cref="DurableSessionEntry.Messages"/>, <see cref="DurableSessionEntry.CorrelationId"/>,
    /// or library-specific fields from a derived entry type.
    /// </param>
    /// <param name="chatOptions">
    /// Optional chat options for this turn (e.g. model id, tools list). May be null when
    /// the subclass does not need MEAI-shaped options.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Retry policy.</strong> The <paramref name="activityOptions"/> argument passed by
    /// <see cref="RunTurnAsync"/> uses the workflow input's configured retry policy, or the
    /// library's bounded default of five attempts when none was configured. This prevents an
    /// unknown permanent activity failure from retrying indefinitely.
    /// </para>
    /// <para>
    /// Implementers dispatching <em>non-idempotent</em> activities (mutating state, calling
    /// external APIs without idempotency keys, sending notifications) are responsible for
    /// hardening this — copy <paramref name="activityOptions"/> with an explicit stricter
    /// <see cref="ActivityOptions.RetryPolicy"/> (e.g. <c>new RetryPolicy { MaximumAttempts = 1 }</c>)
    /// before passing it to <c>Workflow.ExecuteActivityAsync</c>. See <see cref="RunTurnAsync"/>
    /// for full context and the relevant Temporal docs link.
    /// </para>
    /// </remarks>
    protected abstract Task<TOutput> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions);

    /// <summary>
    /// Creates the <see cref="ContinueAsNewException"/> typed to the concrete workflow class.
    /// Called by <see cref="RunAsync"/> when the workflow history grows large enough to
    /// trigger a continue-as-new transition.
    /// </summary>
    protected abstract ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input);

    /// <summary>
    /// Computes the initial turn count when restoring carried history at workflow start.
    /// Default implementation re-derives the count by counting <see cref="DurableSessionResponse"/>
    /// entries in <paramref name="carriedHistory"/>, ensuring the <c>TurnCount</c> search
    /// attribute monotonically grows across continue-as-new boundaries instead of resetting.
    /// Subclasses can override for different semantics (e.g., per-CAN reset).
    /// </summary>
    /// <param name="carriedHistory">
    /// History entries carried forward from a prior run. Empty on the first run of a session.
    /// </param>
    protected virtual int InitializeTurnCount(IReadOnlyList<DurableSessionEntry> carriedHistory) =>
        carriedHistory.Count(e => e is DurableSessionResponse);

    /// <summary>
    /// Hook invoked after the base upserts the standard <c>TurnCount</c> and
    /// <c>SessionCreatedAt</c> search attributes. Subclasses override to upsert
    /// additional library-specific attributes (e.g. MAF's <c>AgentName</c>).
    /// Only called when <see cref="DurableChatWorkflowInput.EnableSearchAttributes"/>
    /// is <see langword="true"/>. Default implementation is a no-op.
    /// </summary>
    protected virtual void UpsertCustomSearchAttributes() { }

    /// <summary>
    /// Builds a copy of <paramref name="entry"/> with <see cref="DurableSessionEntry.Messages"/>
    /// replaced by an empty list, preserving correlation ID and creation timestamp. Default
    /// implementation handles the base library's <see cref="DurableSessionRequest"/> and
    /// <see cref="DurableSessionResponse"/> types. Subclasses with additional concrete entry
    /// types (e.g. MAF's <c>AgentSessionRequest</c> / <c>AgentSessionResponse</c>) override
    /// to preserve their library-specific fields.
    /// </summary>
    /// <param name="entry">The entry to strip.</param>
    /// <returns>A new entry of the same runtime type with empty <c>Messages</c>.</returns>
    protected virtual DurableSessionEntry StripMessagesFromEntry(DurableSessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Note: this base implementation only knows about the AI library's concrete types.
        // Library-specific subclasses must override and add their own type branches BEFORE
        // delegating to this base if they need to preserve subclass-only fields.
        return entry switch
        {
            DurableSessionResponse resp => new DurableSessionResponse
            {
                CorrelationId = resp.CorrelationId,
                CreatedAt = resp.CreatedAt,
                Messages = [],
                Usage = resp.Usage,
                AdditionalProperties = resp.AdditionalProperties,
            },
            DurableSessionRequest req => new DurableSessionRequest
            {
                CorrelationId = req.CorrelationId,
                CreatedAt = req.CreatedAt,
                Messages = [],
                AdditionalProperties = req.AdditionalProperties,
            },
            _ => entry,
        };
    }

    /// <summary>
    /// Dispatches a durable activity to apply a keyed history reducer at continue-as-new time.
    /// Implemented by concrete subclasses to invoke the library-specific activity type
    /// (<c>DurableChatActivities.ReduceHistoryByKeyAsync</c> for the MEAI path,
    /// <c>AgentActivities.ReduceHistoryByKeyAsync</c> for the MAF path).
    /// </summary>
    /// <param name="reducerKey">
    /// The keyed-service key that the activity uses to resolve the
    /// <c>Func&lt;IList&lt;DurableSessionEntry&gt;, IList&lt;DurableSessionEntry&gt;&gt;</c>
    /// delegate from DI.
    /// </param>
    /// <param name="history">The current history to be reduced.</param>
    /// <param name="activityOptions">Activity options (timeouts, retry policy) for the dispatch.</param>
    /// <returns>The reduced history list to carry forward into the new run.</returns>
    protected virtual Task<List<DurableSessionEntry>> ApplyKeyedHistoryReducerAsync(
        string reducerKey,
        List<DurableSessionEntry> history,
        ActivityOptions activityOptions) =>
        throw new NotImplementedException(
            $"{GetType().Name} does not override {nameof(ApplyKeyedHistoryReducerAsync)}. " +
            "Set HistoryReducerKey only on workflow types that implement this method.");

    /// <summary>
    /// When <see langword="true"/>, response entries appended to the in-workflow history have
    /// their <see cref="DurableSessionEntry.Messages"/> replaced with an empty collection.
    /// Used by external-history modes (when an agent is configured with an external history store):
    /// the in-workflow history continues to drive turn-counting, search-attribute upserts,
    /// and the <c>MaxEntryCount</c>-triggered continue-as-new check, but the message payloads
    /// — which are the source of PII and Temporal event-log bloat — live only in the external
    /// store. Default implementation returns <see langword="false"/> (full messages retained).
    /// </summary>
    /// <remarks>
    /// Note that the base class only strips the <em>response</em> entry; the request entry is
    /// supplied by the subclass via <see cref="RunTurnAsync"/> and the subclass is responsible
    /// for stripping it before append if appropriate. The
    /// <c>GetHistoryAsync</c> query therefore returns metadata-only entries when this is on.
    /// </remarks>
    protected virtual bool ShouldStripMessagesFromHistoryEntry() => false;

    // ── Session loop ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the durable session loop. Subclasses annotate their own <c>RunAsync</c>
    /// override with <c>[WorkflowRun]</c> and delegate to this method.
    /// </summary>
    protected virtual async Task RunAsync(DurableChatWorkflowInput input)
    {
        InitializeInput(input);

        // Restore history carried forward from a previous run (continue-as-new).
        if (input.CarriedHistory is { Count: > 0 })
        {
            if (_history.Capacity < input.CarriedHistory.Count)
                _history.Capacity = input.CarriedHistory.Count;
            _history.AddRange(input.CarriedHistory);
        }

        // Re-derive the turn count from carried history so search attributes and per-turn
        // diagnostics stay monotonic across continue-as-new transitions. Subclasses override
        // InitializeTurnCount for different semantics.
        _turnCount = InitializeTurnCount(_history);

        // Capture the original creation time on the first run; carry it forward on CAN transitions.
        var sessionCreatedAt = input.OriginalCreatedAt ?? Workflow.UtcNow;

        // Opt-in: upsert search attributes only when explicitly requested.
        // Guards against failure on servers where the attributes are not pre-registered.
        if (input.EnableSearchAttributes)
        {
            Workflow.UpsertTypedSearchAttributes(
                DurableSessionAttributes.SessionCreatedAt.ValueSet(sessionCreatedAt),
                DurableSessionAttributes.TurnCount.ValueSet(_turnCount));
            UpsertCustomSearchAttributes();
        }

        // Wait until shutdown, SDK-suggested CAN, or history has grown to MaxEntryCount.
        bool conditionMet = await Workflow.WaitConditionAsync(
            () => _shutdownRequested
                  || (!_isProcessing && Workflow.ContinueAsNewSuggested)
                  || (!_isProcessing && _history.Count >= input.MaxEntryCount),
            timeout: input.TimeToLive);

        if (!conditionMet)
        {
            // TTL elapsed — session complete. Drain any in-flight handlers (e.g. fire-and-forget
            // turns) before completing so we don't abort them with TMPRL1102.
            await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished).ConfigureAwait(true);
            return;
        }

        if ((Workflow.ContinueAsNewSuggested || _history.Count >= input.MaxEntryCount) && !_shutdownRequested)
        {
            // Reducer selection (priority order):
            //
            // 1. HistoryReducerKey — durable path: dispatch a ReduceHistoryByKey activity so
            //    the reducer delegate is resolved from DI on the worker side. The activity result
            //    is stored in Temporal history and survives replay deterministically. This is the
            //    correct fix for the [JsonIgnore] silent-failure bug: the key is serialized and
            //    travels on the wire; the delegate never does.
            //
            // 2. HistoryReducer (inline delegate) — kept for unit-test and in-process use where a
            //    delegate can be supplied without DI. NOT reliable in production durable workflows
            //    (the [JsonIgnore] strips it on every serialize/deserialize round-trip).
            //
            // 3. DefaultBoundedTrim (C-2 fallback) — no reducer configured. Keeps the most-recent
            //    Max(1, MaxEntryCount/2) entries so the fresh run has headroom before the next CAN.
            //    Pure and order-preserving (TakeLast) — replay-safe. External-store mode (MAF only)
            //    nulls CarriedHistory in its CreateContinueAsNewException override, making this a
            //    harmless no-op there.
            List<DurableSessionEntry> carriedHistory;
            if (input.HistoryReducerKey is not null)
            {
                var reducerActivityOptions = new ActivityOptions
                {
                    StartToCloseTimeout = input.ActivityTimeout,
                    HeartbeatTimeout = input.HeartbeatTimeout,
                };
                carriedHistory = await ApplyKeyedHistoryReducerAsync(
                    input.HistoryReducerKey, _history, reducerActivityOptions).ConfigureAwait(true);
            }
            else if (input.HistoryReducer is not null)
            {
                carriedHistory = input.HistoryReducer(_history).ToList();
            }
            else
            {
                carriedHistory = DefaultBoundedTrim(_history, input.MaxEntryCount);
            }

            var carriedInput = CreateContinueAsNewInput(
                input,
                carriedHistory,
                _approvalMixin.GetResolvedApprovals(),
                sessionCreatedAt);
            // Drain in-flight update/signal handlers before completing-as-new. _isProcessing is a
            // turn-serialization mutex that clears in RunTurnAsync's finally BEFORE the update handler's
            // continuation (logging + result delivery) finishes, so gating CAN on !_isProcessing alone
            // races the handler and aborts it with TMPRL1102 (a lost user turn). AllHandlersFinished is
            // the SDK-sanctioned completion barrier that tracks both update and signal handlers.
            await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished).ConfigureAwait(true);
            throw CreateContinueAsNewException(carriedInput);
        }
    }

    /// <summary>
    /// Clones frozen workflow configuration for continue-as-new while replacing only the
    /// run-scoped values that intentionally change at the boundary.
    /// </summary>
    /// <remarks>
    /// Record cloning preserves every current and future frozen setting and retains the runtime
    /// input type used by derived packages. Keeping this operation centralized prevents a newly
    /// added setting from being silently dropped by field-by-field reconstruction.
    /// </remarks>
    internal static DurableChatWorkflowInput CreateContinueAsNewInput(
        DurableChatWorkflowInput input,
        List<DurableSessionEntry> carriedHistory,
        IReadOnlyList<DurableApprovalDecision> approvalResolutionHistory,
        DateTimeOffset originalCreatedAt)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(carriedHistory);
        ArgumentNullException.ThrowIfNull(approvalResolutionHistory);

        Internal.DurableToolsetAuthority.Resolve(input);

        return input with
        {
            CarriedHistory = carriedHistory,
            ApprovalResolutionHistory = approvalResolutionHistory,
            OriginalCreatedAt = originalCreatedAt,
        };
    }

    /// <summary>
    /// Deterministic default history trim applied at continue-as-new when no
    /// <see cref="DurableChatWorkflowInput.HistoryReducer"/> is configured (C-2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The CAN trigger is <c>history.Count &gt;= maxEntryCount</c>. Without a trim, the no-reducer
    /// path carried the full history into the fresh run, which immediately re-tripped the same
    /// threshold — a back-to-back CAN loop. This keeps only the most-recent entries and guarantees
    /// the carried count is <strong>strictly below</strong> <paramref name="maxEntryCount"/>, so the
    /// new run has headroom before the next CAN.
    /// </para>
    /// <para>
    /// Target = half of <paramref name="maxEntryCount"/> (floored, minimum 1) — a conservative
    /// default that leaves room for several turns and avoids trimming on every turn near the cap.
    /// When the history is already at or below the target it is returned unchanged (so an
    /// SDK-suggested CAN with a small history is not perturbed). Pure and order-preserving
    /// (<see cref="System.Linq.Enumerable.TakeLast{TSource}"/> over the existing entry order) — no
    /// wall-clock, no <see cref="Workflow.NewGuid"/> — hence replay-safe.
    /// </para>
    /// </remarks>
    private static List<DurableSessionEntry> DefaultBoundedTrim(
        List<DurableSessionEntry> history,
        int maxEntryCount)
    {
        // Guard against non-positive MaxEntryCount (validated elsewhere, but stay total here):
        // a target of at least 1 keeps the most-recent entry rather than emptying history.
        var target = Math.Max(1, maxEntryCount / 2);

        if (history.Count <= target)
        {
            // Already below the trim target (e.g. SDK-suggested CAN, not count-driven) — pass
            // through unchanged. The workflow exits after the throw, so no aliasing risk.
            return history;
        }

        return history.TakeLast(target).ToList();
    }

    /// <summary>
    /// Executes a single chat turn: serializes concurrent turns, appends the supplied
    /// request entry, dispatches the LLM call via <see cref="ExecuteTurnAsync"/>, appends
    /// a response entry, and updates the turn count search attribute if opted in.
    /// </summary>
    /// <param name="requestEntry">
    /// The request entry to append to history before the activity is dispatched. Subclass
    /// <c>[WorkflowUpdate]</c> handlers construct this via library-specific factories
    /// (<see cref="DurableSessionRequest.FromMessages"/> for chat workflows;
    /// <c>AgentSessionRequest.FromRunRequest</c> for MAF agent workflows).
    /// </param>
    /// <param name="chatOptions">
    /// Optional chat options for the activity dispatch. May be null.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the workflow update.</param>
    /// <returns>
    /// A tuple containing the activity's raw <typeparamref name="TOutput"/> and the
    /// <see cref="DurableSessionResponse"/> entry that was appended to history.
    /// Subclass update handlers typically return one or the other depending on the
    /// shape they want to expose to callers.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Retry policy for subclassers.</strong> The <see cref="ActivityOptions"/>
    /// constructed inside this method and handed to <see cref="ExecuteTurnAsync"/> uses the
    /// workflow input's configured retry policy. When none is configured, the library applies a
    /// bounded five-attempt default.
    /// </para>
    /// <para>
    /// This default is appropriate for idempotent LLM calls (the canonical use case for
    /// <see cref="DurableChatWorkflowBase{TOutput}"/>): retrying an inference request just
    /// re-asks the model. It is <strong>not sufficient</strong> for subclassers whose
    /// <see cref="ExecuteTurnAsync"/> dispatches non-idempotent activities — mutating state,
    /// calling external APIs without idempotency keys, sending notifications, etc. In those
    /// cases retries can duplicate side effects.
    /// </para>
    /// <para>
    /// Subclassers with non-idempotent activities must override <see cref="ExecuteTurnAsync"/>
    /// and construct a hardened <see cref="ActivityOptions"/> with an explicit
    /// <see cref="ActivityOptions.RetryPolicy"/> (for example,
    /// <c>new RetryPolicy { MaximumAttempts = 1 }</c>) before invoking the activity.
    /// See <see href="https://docs.temporal.io/encyclopedia/retry-policies"/> for the full
    /// server-default behavior reference.
    /// </para>
    /// </remarks>
    protected async Task<(TOutput Output, DurableSessionResponse ResponseEntry)> RunTurnAsync(
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestEntry);

        // Serialize: wait for any in-progress turn to finish.
        // Safety note: after WaitConditionAsync returns, the workflow is in a synchronous
        // execution window. Temporal's single-threaded scheduler cannot interleave another
        // update handler until the next await point. Setting _isProcessing = true immediately
        // after the condition is therefore atomic — no concurrent handler can observe
        // _isProcessing == false and enter this section between these two lines.
        await Workflow.WaitConditionAsync(() => !_isProcessing);
        _isProcessing = true;

        var historyCountBeforeTurn = _history.Count;
        var turnCountBeforeTurn = _turnCount;
        var rollbackParticipant = this as Internal.IDurableTurnRollbackParticipant;
        JsonElement? participantStateBeforeTurn = null;
        var participantStateCaptured = false;

        try
        {
            // Capture derived transactional state only after this turn owns the gate. A queued
            // update may have started while the preceding turn was suspended; capturing outside
            // this critical section would give it a stale snapshot that could erase the preceding
            // turn's committed state if the queued turn later failed.
            if (rollbackParticipant is not null)
            {
                participantStateBeforeTurn = rollbackParticipant.CaptureTurnRollbackState();
                participantStateCaptured = true;
            }

            // Append the request entry for this turn. When external-history mode is on we
            // replace the messages with an empty list so the in-workflow history never holds
            // the raw user prompt — only metadata (CorrelationId, CreatedAt) for turn counting.
            var requestEntryToAppend = ShouldStripMessagesFromHistoryEntry()
                ? (DurableSessionRequest)StripMessagesFromEntry(requestEntry)
                : requestEntry;
            _history.Add(requestEntryToAppend);

            _turnCount++;

            var activityOptions = new ActivityOptions
            {
                StartToCloseTimeout = RequiredInput.ActivityTimeout,
                HeartbeatTimeout = RequiredInput.HeartbeatTimeout,
                RetryPolicy = Internal.DefaultRetryPolicy.ResolveForModel(RequiredInput.RetryPolicy),
                Summary = DurableChatClient.BuildActivitySummary(chatOptions),
            };

            var output = await ExecuteTurnAsync(activityOptions, requestEntry, chatOptions);

            // Build the response entry, then optionally strip its message payload before
            // appending so external-history mode keeps the workflow history metadata-only.
            var responseEntry = BuildResponseEntry(requestEntry.CorrelationId, output, Workflow.UtcNow);
            var responseEntryToAppend = ShouldStripMessagesFromHistoryEntry()
                ? (DurableSessionResponse)StripMessagesFromEntry(responseEntry)
                : responseEntry;
            _history.Add(responseEntryToAppend);

            // Update turn count search attribute if opt-in was requested.
            if (RequiredInput.EnableSearchAttributes)
            {
                Workflow.UpsertTypedSearchAttributes(
                    DurableSessionAttributes.TurnCount.ValueSet(_turnCount));
            }

            return (output, responseEntry);
        }
        catch
        {
            // A turn is a transactional unit from the session's perspective. If model/tool
            // execution, response projection, or search-attribute publication fails, retain
            // neither the request nor any partially-created response and do not count the
            // failed attempt as a completed turn. The workflow gate prevents another turn
            // from mutating these collections while this rollback runs.
            if (_history.Count > historyCountBeforeTurn)
            {
                _history.RemoveRange(
                    historyCountBeforeTurn,
                    _history.Count - historyCountBeforeTurn);
            }

            _turnCount = turnCountBeforeTurn;

            // Restore derived state before releasing the same gate that protected the turn.
            // This keeps the base history and any participant state in one transaction boundary.
            if (participantStateCaptured)
            {
                rollbackParticipant!.RestoreTurnRollbackState(participantStateBeforeTurn);
            }

            throw;
        }
        finally
        {
            _isProcessing = false;
        }
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current conversation history as a list of <see cref="DurableSessionEntry"/>
    /// instances. Each turn appends a request entry followed by a response entry.
    /// </summary>
    [WorkflowQuery("GetHistory")]
    public IReadOnlyList<DurableSessionEntry> GetHistory() => _history;

    // ── Signals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Requests graceful shutdown of this session.
    /// </summary>
    [WorkflowSignal(ShutdownSignalName)]
    public Task RequestShutdownAsync()
    {
        _shutdownRequested = true;
        return Task.CompletedTask;
    }

    // ── HITL: Tool Approval ──────────────────────────────────────────────────

    /// <summary>
    /// Validates a tool approval request before it enters workflow history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RequestApprovalAsync))]
    public void ValidateRequestApproval(DurableApprovalRequest request)
    {
        // An Update-With-Start validator can run before the workflow run method's first user
        // statement. Admit it so the async handler can wait for initialization, then revalidate.
        if (Input is null)
        {
            return;
        }

        _approvalMixin.ValidateRequestApproval(request);
    }

    [WorkflowUpdate("RequestApproval")]
    public async Task<DurableApprovalDecision> RequestApprovalAsync(DurableApprovalRequest request)
    {
        var input = await WaitForInputAsync().ConfigureAwait(true);
        var decision = await _approvalMixin.RequestApprovalAsync(
            request,
            approvalTimeout: input.ApprovalTimeout,
            onRequested: req => Workflow.Logger.LogInformation(
                "[{ConversationId}] Approval requested (RequestId: {RequestId}, Description: {Description})",
                Workflow.Info.WorkflowId, req.RequestId, req.Description ?? req.RequestId),
            onResolved: d => Workflow.Logger.LogInformation(
                "[{ConversationId}] Approval resolved (RequestId: {RequestId}, Approved: {Approved})",
                Workflow.Info.WorkflowId, d.RequestId, d.Approved));

        OnApprovalRequestResolved(decision);
        return decision;
    }

    /// <summary>
    /// Resolves a pending approval request and returns a retry-safe result.
    /// </summary>
    [WorkflowUpdate("ResolveApproval")]
    public async Task<DurableApprovalResolutionResult> ResolveApprovalAsync(DurableApprovalDecision decision)
    {
        // Temporal may admit the first update to a continued-as-new run before the workflow
        // run method has initialized Input. Wait for that deterministic initialization point
        // rather than returning a false NotPending result for a valid reviewer retry.
        if (Input is null)
        {
            await Workflow.WaitConditionAsync(() => Input is not null).ConfigureAwait(true);
        }

        // A continue-as-new input is the durable authority for prior resolutions. Rehydrate
        // defensively if a replay-created workflow instance has no in-memory archive yet.
        // Do not overwrite a non-empty cache: it may contain resolutions accepted since this
        // run began and therefore not present in the start input.
        if (_approvalMixin.GetResolvedApprovals().Count == 0
            && Input?.ApprovalResolutionHistory is { Count: > 0 } carriedResolutions)
        {
            _approvalMixin.RestoreResolvedApprovals(carriedResolutions);
        }

        var result = _approvalMixin.ResolveApproval(decision);
        if (result.Status == DurableApprovalResolutionStatus.Accepted)
        {
            OnApprovalResolutionAccepted(decision);
        }

        return result;
    }

    /// <summary>
    /// Invoked when the generic approval resolution update accepts <paramref name="decision"/>.
    /// Derived workflows can record workflow-specific resolution state. The base workflow has no
    /// additional state to record.
    /// </summary>
    /// <param name="decision">The accepted generic approval decision.</param>
    protected virtual void OnApprovalResolutionAccepted(DurableApprovalDecision decision)
    {
    }

    /// <summary>
    /// Invoked after an approval request completes, including rejection and timeout. Derived
    /// workflows can synchronize workflow-specific state with the generic resolved-decision
    /// archive. The base workflow has no additional state to record.
    /// </summary>
    /// <param name="decision">The final approval decision.</param>
    protected virtual void OnApprovalRequestResolved(DurableApprovalDecision decision)
    {
    }

    /// <summary>
    /// Returns the currently pending approval request, or null if none.
    /// </summary>
    [WorkflowQuery("GetPendingApproval")]
    public DurableApprovalRequest? GetPendingApproval() => _approvalMixin.GetPendingApproval();

    /// <summary>
    /// Invokes the approval state machine directly from the turn loop (Feature A —
    /// compute-free workflow-parked HITL). Unlike the <c>[WorkflowUpdate]</c> path (which
    /// passes through <see cref="RequestApprovalAsync"/>), this method bypasses the update
    /// handler so the workflow can park on the wait-condition inside the turn loop itself
    /// rather than inside an update handler. The mixin's single-pending-approval guard still
    /// runs (it is now enforced inside <c>DurableApprovalMixin.RequestApprovalAsync</c> as
    /// well as inside <c>ValidateRequestApproval</c>).
    /// </summary>
    /// <remarks>
    /// Must only be called from workflow-thread code. The approval unblocks when a matching
    /// decision arrives through <see cref="ResolveApprovalAsync"/>.
    /// </remarks>
    protected async Task<DurableApprovalDecision> RequestApprovalFromTurnLoopAsync(
        DurableApprovalRequest request,
        TimeSpan approvalTimeout,
        Action<DurableApprovalRequest>? onRequested = null,
        Action<DurableApprovalDecision>? onResolved = null)
    {
        var decision = await _approvalMixin.RequestApprovalAsync(
            request,
            approvalTimeout,
            onRequested,
            onResolved);

        OnApprovalRequestResolved(decision);
        return decision;
    }
}
