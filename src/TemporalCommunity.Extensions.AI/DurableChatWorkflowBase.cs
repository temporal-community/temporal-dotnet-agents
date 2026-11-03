using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Abstract base class for durable chat workflows with typed turn output.
/// Provides the shared session loop, conversation history, HITL approval support,
/// continue-as-new handling, search attribute upserts, and retry-safe turn admission.
/// Concrete subclasses implement the abstract members to dispatch to their own
/// activities and to convert per-turn output into <see cref="DurableSessionResponse"/>
/// entries that get appended to history.
/// </summary>
/// <typeparam name="TOutput">The type returned from each completed chat turn.</typeparam>
public abstract partial class DurableChatWorkflowBase<TOutput>
{
    private List<DurableSessionEntry> _history = new(16);
    private readonly DurableApprovalMixin _approvalMixin = new();
    private readonly Dictionary<string, TurnExecutionRecord> _terminalTurns =
        new(StringComparer.Ordinal);
    private readonly List<string> _terminalTurnOrder = [];
    private readonly Dictionary<string, MetricCounter<long>> _workflowMetrics =
        new(StringComparer.Ordinal);
    private readonly List<InterruptedToolBatch> _interruptedToolBatches = [];
    private long _terminalTurnLedgerBytes;
    private int _maxTerminalOutcomeCount = 128;
    private int _maxTerminalOutcomeBytes = 1024 * 1024;
    private TurnExecutionRecord? _activeTurn;
    private bool _isProcessing;
    private bool _shutdownRequested;
    private bool _continueAsNewPending;
    private bool _sessionAdmissionClosed;
    private DateTimeOffset? _sessionIdleDeadline;
    private int _turnCount;
    private int _currentTurnStartIndex;
    private IReadOnlyList<DurableChatTurnRange> _previousTurnRanges = [];
    private DurableHistoryRecoveryState _historyRecovery = new();
    private int _historyRecoveryRetriesThisRun;
    private bool _historyRecoveryOperationActive;
    private bool _historyRecoveryCanAttemptedThisRun;
    private List<DurableSessionEntry>? _preparedHistory;

    private const int MaximumHistoryRecoveryRetriesPerRun = 3;
    private const int MaximumHistoryRecoveryRetriesPerIncident = 9;

    private sealed class TurnExecutionRecord
    {
        public TurnExecutionRecord(
            string requestId,
            string admissionFingerprint,
            CancellationToken workflowCancellationToken = default)
        {
            RequestId = requestId;
            AdmissionFingerprint = admissionFingerprint;
            CancellationSource = workflowCancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(workflowCancellationToken)
                : new CancellationTokenSource();
            CancellationToken = CancellationSource.Token;
        }

        public string RequestId { get; }

        public string AdmissionFingerprint { get; }

        public CancellationTokenSource CancellationSource { get; }

        public CancellationToken CancellationToken { get; }

        public int JoinerCount { get; set; }

        public string? TurnInputFingerprint { get; set; }

        public bool IsTerminal { get; set; }

        public bool CancellationRequested { get; set; }

        public TOutput? Output { get; set; }

        public DurableSessionResponse? ResponseEntry { get; set; }

        public JsonElement? SerializedOutput { get; set; }

        public JsonElement? SerializedResponseEntry { get; set; }

        public int SerializedSizeBytes { get; set; }

        public TurnFailure? Failure { get; set; }
    }

    private sealed record TurnFailure(string ErrorType, string Message, bool NonRetryable);

    private sealed class InterruptedToolBatch(IReadOnlyList<FunctionCallContent> calls)
    {
        public IReadOnlyList<InterruptedToolCall> Calls { get; } =
            calls.Select(call => new InterruptedToolCall(call)).ToArray();
    }

    private sealed class InterruptedToolCall(FunctionCallContent call)
    {
        public FunctionCallContent Call { get; } = call;

        public bool Dispatched { get; set; }

        public bool HasResult { get; set; }

        public object? Result { get; set; }
    }

    /// <summary>
    /// Represents ownership of, or a join on, one logical turn admission.
    /// </summary>
    protected sealed class TurnAdmission
    {
        internal object RecordState { get; }

        private TurnAdmission(object recordState, string requestId, bool isOwner, bool isJoiner)
        {
            RecordState = recordState;
            RequestId = requestId;
            IsOwner = isOwner;
            IsJoiner = isJoiner;
        }

        internal static TurnAdmission Create(
            object recordState,
            string requestId,
            bool isOwner,
            bool isJoiner = false) =>
            new(recordState, requestId, isOwner, isJoiner);

        /// <summary>Whether this handler owns execution of the admitted turn.</summary>
        public bool IsOwner { get; }

        /// <summary>Whether this handler occupies a concurrent joiner slot.</summary>
        internal bool IsJoiner { get; }

        /// <summary>The logical request ID associated with this admission.</summary>
        public string RequestId { get; }
    }

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

    /// <summary>The active turn's token, linked to both workflow and turn cancellation.</summary>
    protected CancellationToken CurrentTurnCancellationToken =>
        _activeTurn?.CancellationToken ?? Workflow.CancellationToken;

    /// <summary>
    /// Copies activity options and binds them to the active turn's cancellation token. Waiting
    /// for activity cancellation completion keeps the session from admitting a later turn while
    /// a canceled activity is still running.
    /// </summary>
    protected ActivityOptions ApplyCurrentTurnCancellation(ActivityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var boundOptions = (ActivityOptions)options.Clone();
        boundOptions.CancellationToken = CurrentTurnCancellationToken;
        boundOptions.CancellationType = ActivityCancellationType.WaitCancellationCompleted;
        return boundOptions;
    }

    protected bool IsCurrentTurnCancellationRequested() =>
        _activeTurn?.CancellationRequested == true
        || CurrentTurnCancellationToken.IsCancellationRequested;

    protected void ThrowIfCurrentTurnCancellationRequested()
    {
        if (IsCurrentTurnCancellationRequested())
        {
            throw new OperationCanceledException("The active turn was cancelled.");
        }
    }

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
        ValidateTerminalOutcomeLimits(input);
        ValidateHistoryBudgetLimits(input);
        if (input.MaxModelContextTokens is <= 0
            || input.ModelOutputTokenReserve < 0
            || (input.MaxModelContextTokens is { } modelLimit
                && input.ModelOutputTokenReserve >= modelLimit))
        {
            throw new InvalidOperationException("The configured model-context token budget is invalid.");
        }
        if (input.HistorySummarizerMaxContextTokens <= 0
            || input.HistorySummarizerOutputTokenReserve <= 0
            || input.HistorySummarizerOutputTokenReserve >= input.HistorySummarizerMaxContextTokens)
        {
            throw new InvalidOperationException("The configured history-summarizer token budget is invalid.");
        }
        if (input.HistorySummarizerClientKey is not null
            && string.IsNullOrWhiteSpace(input.HistorySummarizerClientKey))
        {
            throw new InvalidOperationException(
                "The configured history-summarizer client key cannot be empty or whitespace.");
        }
        if (!Enum.IsDefined(typeof(DurableHistoryCompactionStrategy), input.HistoryCompactionStrategy)
            || (input.HistoryReducerKey is not null
                && input.HistoryCompactionStrategy != DurableHistoryCompactionStrategy.None))
        {
            throw new InvalidOperationException("The configured history-compaction strategy is invalid.");
        }
        var sessionEpoch = input.SessionEpoch ?? Input?.SessionEpoch;
        if (sessionEpoch is null)
        {
            input = input with { SessionEpoch = Workflow.NewGuid().ToString("N") };
        }
        else if (input.SessionEpoch is null)
        {
            input = input with { SessionEpoch = sessionEpoch };
        }
        var sessionIdleDeadline = input.SessionIdleDeadline ?? Input?.SessionIdleDeadline;
        if (sessionIdleDeadline is null)
        {
            sessionIdleDeadline = Workflow.UtcNow + input.TimeToLive;
        }

        if (input.SessionIdleDeadline != sessionIdleDeadline)
        {
            input = input with { SessionIdleDeadline = sessionIdleDeadline };
        }
        if (input.EnableTransportPayloadGuard)
        {
            ValidateTransportConfiguration(input);
        }
        _maxTerminalOutcomeCount = input.MaxTerminalOutcomeCount;
        _maxTerminalOutcomeBytes = input.MaxTerminalOutcomeBytes;
        _sessionIdleDeadline = sessionIdleDeadline;
        Input = input;
        if (input.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                input,
                input.MaximumTransportPayloadSizeBytes,
                input.PayloadCodecExpansionAllowanceBytes,
                "workflow input");
        }
        if (firstInitialization)
        {
            _approvalMixin.RestoreResolvedApprovals(input.ApprovalResolutionHistory);
            RestoreTerminalTurnOutcomes(input.CarriedTerminalOutcomes);
            _historyRecovery = input.HistoryRecovery ?? new DurableHistoryRecoveryState();
            if (_historyRecovery.RetryCountThisIncident < 0
                || _historyRecovery.RetryCountThisIncident > MaximumHistoryRecoveryRetriesPerIncident
                || _historyRecovery.AcceptedOperationIds is null
                || _historyRecovery.AcceptedOperationIds.Count > MaximumHistoryRecoveryRetriesPerIncident + 1
                || _historyRecovery.AcceptedOperationIds.Any(string.IsNullOrWhiteSpace)
                || _historyRecovery.AcceptedOperationIds.Distinct(StringComparer.Ordinal).Count()
                != _historyRecovery.AcceptedOperationIds.Count)
            {
                throw new InvalidOperationException("The carried history-recovery state is invalid.");
            }
            _sessionAdmissionClosed = _historyRecovery.IsBlocked;
        }
    }

    private static void ValidateTransportConfiguration(DurableChatWorkflowInput input)
    {
        if (input.MaximumTransportPayloadSizeBytes <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(input.MaximumTransportPayloadSizeBytes)} must be positive.");
        }

        if (input.PayloadCodecExpansionAllowanceBytes < 0
            || input.PayloadCodecExpansionAllowanceBytes >= input.MaximumTransportPayloadSizeBytes)
        {
            throw new InvalidOperationException(
                $"{nameof(input.PayloadCodecExpansionAllowanceBytes)} must be non-negative and smaller " +
                $"than {nameof(input.MaximumTransportPayloadSizeBytes)}.");
        }

        Internal.DurableTransportPayloadGuard.EnsureBoundedRetryPolicy(
            input.RetryPolicy,
            nameof(input.RetryPolicy));

        ValidateActivityRetryPolicies(input.ToolActivityOptions);
        ValidateActivityRetryPolicies(input.InterceptorToolActivityOptions);
        ValidateActivityRetryPolicy(input.InterceptorActivityOptions);

        if (input.ToolsetManifest is { } manifest)
        {
            foreach (var member in manifest.Members)
            {
                ValidateActivityRetryPolicy(member.ToolActivityOptions);
                ValidateActivityRetryPolicy(member.InterceptorActivityOptions);
            }
        }
    }

    private static void ValidateActivityRetryPolicies(
        IReadOnlyDictionary<string, ActivityOptions>? activityOptions)
    {
        if (activityOptions is null)
        {
            return;
        }

        foreach (var (toolName, options) in activityOptions)
        {
            ValidateActivityRetryPolicy(options, $"RetryPolicy for tool '{toolName}'");
        }
    }

    private static void ValidateActivityRetryPolicy(
        ActivityOptions? activityOptions,
        string settingName = "ActivityOptions.RetryPolicy")
    {
        if (activityOptions is not null)
        {
            Internal.DurableTransportPayloadGuard.EnsureBoundedRetryPolicy(
                activityOptions.RetryPolicy,
                settingName);
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

    protected void RenewSessionIdleDeadline()
    {
        var input = RequiredInput;
        var deadline = Workflow.UtcNow + input.TimeToLive;
        _sessionIdleDeadline = deadline;
        Input = input with { SessionIdleDeadline = deadline };
    }

    /// <summary>
    /// Returns <see langword="true"/> once a shutdown update has been accepted.
    /// Subclass update validators can use this to reject new turns after shutdown.
    /// </summary>
    protected bool IsShutdownRequested => _shutdownRequested;

    /// <summary>Whether this run has stopped admitting new turns before continue-as-new.</summary>
    protected bool IsContinueAsNewPending => _continueAsNewPending;

    /// <summary>Whether idle expiry has closed new-turn admission while accepted work drains.</summary>
    protected bool IsSessionAdmissionClosed => _sessionAdmissionClosed;

    /// <summary>
    /// Validates a caller's optional session-lineage precondition after workflow input is restored.
    /// </summary>
    protected void ValidateExpectedSessionEpoch(string? expectedSessionEpoch)
    {
        if (expectedSessionEpoch is not null && string.IsNullOrWhiteSpace(expectedSessionEpoch))
        {
            throw new ArgumentException(
                "Expected session epoch cannot be empty or whitespace.",
                nameof(expectedSessionEpoch));
        }

        if (expectedSessionEpoch is not null
            && !string.Equals(expectedSessionEpoch, CurrentSessionEpoch, StringComparison.Ordinal))
        {
            RecordWorkflowMetric(
                DurableChatTelemetry.TurnAdmissionRejectionsMetricName,
                "reason",
                "epoch_mismatch");
            throw new ApplicationFailureException(
                $"Expected session epoch '{expectedSessionEpoch}' does not match the active session epoch.",
                errorType: Exceptions.DurableChatSessionEpochMismatchException.ErrorType,
                nonRetryable: true);
        }
    }

    /// <summary>Whether a subclass has started turns outside tracked Update handlers.</summary>
    protected virtual bool HasPendingBackgroundTurns => false;

    /// <summary>
    /// The current turn count, available to subclass overrides for telemetry or
    /// activity-input fields. Updated inside <see cref="RunTurnAsync"/> after each turn
    /// completes, and initialized from carried history at the start of each run via
    /// <see cref="InitializeTurnCount"/>.
    /// </summary>
    protected int CurrentTurnNumber => _turnCount;

    /// <summary>The opaque epoch identifying the current session lineage.</summary>
    protected string CurrentSessionEpoch =>
        RequiredInput.SessionEpoch
        ?? throw new InvalidOperationException("The session epoch has not been initialized.");

    /// <summary>
    /// The current conversation history, available to subclass overrides that need to
    /// pass the full flattened message log to their activity (e.g. so the LLM sees
    /// prior turns). The request entry for the current turn is appended to history
    /// <em>before</em> <see cref="ExecuteTurnAsync"/> is invoked, so the latest entry
    /// in this list is always the request that triggered the activity dispatch.
    /// </summary>
    protected IReadOnlyList<DurableSessionEntry> History => _history;

    /// <summary>Start of the active turn in the flattened model-message list.</summary>
    protected int CurrentTurnStartIndex => _currentTurnStartIndex;

    /// <summary>Prior complete turns, in message order, that may be trimmed from model context.</summary>
    protected IReadOnlyList<DurableChatTurnRange> PreviousTurnRanges => _previousTurnRanges;

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
        string? requestId,
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
    /// Serializes a terminal turn output for bounded deduplication across continue-as-new.
    /// Override when a derived workflow requires a specialized serializer.
    /// </summary>
    protected virtual JsonElement SerializeTurnOutput(TOutput output) =>
        JsonSerializer.SerializeToElement(output, DurableAIJsonUtilities.DefaultOptions);

    /// <summary>Restores an output serialized by <see cref="SerializeTurnOutput"/>.</summary>
    protected virtual TOutput DeserializeTurnOutput(JsonElement output) =>
        output.Deserialize<TOutput>(DurableAIJsonUtilities.DefaultOptions)!;

    /// <summary>
    /// Serializes a response entry using its polymorphic durable-entry contract.
    /// </summary>
    protected virtual JsonElement SerializeTurnResponseEntry(DurableSessionResponse responseEntry) =>
        JsonSerializer.SerializeToElement<DurableSessionEntry>(
            responseEntry, DurableAIJsonUtilities.DefaultOptions);

    /// <summary>Restores a response entry serialized by <see cref="SerializeTurnResponseEntry"/>.</summary>
    protected virtual DurableSessionResponse DeserializeTurnResponseEntry(JsonElement responseEntry) =>
        responseEntry.Deserialize<DurableSessionEntry>(DurableAIJsonUtilities.DefaultOptions)
            as DurableSessionResponse
        ?? throw new InvalidOperationException("The retained turn outcome has no response entry.");

    /// <summary>JSON options used to measure the serialized terminal outcome ledger.</summary>
    protected virtual JsonSerializerOptions TerminalOutcomeJsonSerializerOptions =>
        DurableAIJsonUtilities.DefaultOptions;

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
                EntryId = resp.EntryId,
                TurnId = resp.TurnId,
                CorrelationId = resp.CorrelationId,
                RequestId = resp.RequestId,
                CreatedAt = resp.CreatedAt,
                Messages = [],
                Usage = resp.Usage,
                ResponseId = resp.ResponseId,
                ModelId = resp.ModelId,
                ResponseAdditionalProperties = resp.ResponseAdditionalProperties,
                FinishReason = resp.FinishReason,
                CompletionReason = resp.CompletionReason,
                AdditionalProperties = resp.AdditionalProperties,
            },
            DurableSessionRequest req => new DurableSessionRequest
            {
                EntryId = req.EntryId,
                TurnId = req.TurnId,
                CorrelationId = req.CorrelationId,
                RequestId = req.RequestId,
                InputFingerprint = req.InputFingerprint,
                CreatedAt = req.CreatedAt,
                Messages = [],
                AdditionalProperties = req.AdditionalProperties,
            },
            _ => entry,
        };
    }

    private (int CurrentTurnStartIndex, IReadOnlyList<DurableChatTurnRange> PreviousTurnRanges)
        BuildModelContextTurnRanges(DurableSessionRequest currentRequest)
    {
        var ranges = new List<DurableChatTurnRange>();
        var messageIndex = 0;
        var currentStart = 0;

        for (var index = 0; index < _history.Count; index++)
        {
            var entry = _history[index];
            var start = messageIndex;
            messageIndex += ProjectHistoryEntryMessages(entry).Count;

            if (ReferenceEquals(entry, currentRequest)
                || string.Equals(entry.EntryId, currentRequest.EntryId, StringComparison.Ordinal))
            {
                currentStart = start;
                break;
            }

            if (entry is DurableSessionRequest request
                && index + 1 < _history.Count
                && _history[index + 1] is DurableSessionResponse response
                && SameLogicalTurn(request, response))
            {
                var end = messageIndex + response.Messages.Count;
                var isProtected = response.CompletionReason == DurableTurnCompletionReason.Interrupted
                    || ContainsApprovalContent(request.Messages)
                    || ContainsApprovalContent(response.Messages)
                    || ContainsInstructionContent(request.Messages)
                    || ContainsInstructionContent(response.Messages);
                if (end > start)
                {
                    ranges.Add(new DurableChatTurnRange(start, end, isProtected));
                }
                messageIndex = end;
                index++;
                continue;
            }

            if (messageIndex > start)
            {
                ranges.Add(new DurableChatTurnRange(start, messageIndex, IsProtected: true));
            }
        }

        return (currentStart, ranges);
    }

    private static bool SameLogicalTurn(DurableSessionRequest request, DurableSessionResponse response) =>
        request.TurnId is not null && response.TurnId is not null
            ? string.Equals(request.TurnId, response.TurnId, StringComparison.Ordinal)
            : string.Equals(request.CorrelationId, response.CorrelationId, StringComparison.Ordinal);

    private static bool ContainsApprovalContent(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents)
            .Any(content => content is ToolApprovalRequestContent or ToolApprovalResponseContent);

    private static bool ContainsInstructionContent(IReadOnlyList<ChatMessage> messages) =>
        messages.Any(message =>
            message.Role == ChatRole.System || message.Role == new ChatRole("developer"));

    /// <summary>Projects a retained entry to the messages used for future model context.</summary>
    protected static IReadOnlyList<ChatMessage> ProjectHistoryEntryMessages(DurableSessionEntry entry) =>
        entry is CompactionMarkerEntry marker
            ? [new ChatMessage(ChatRole.Assistant, marker.Summary.Text)]
            : entry.Messages;

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

    /// <summary>Dispatches the library-owned summarizer activity from a workflow implementation.</summary>
    protected virtual Task<DurableHistorySummarizationResult> SummarizeHistoryPrefixAsync(
        DurableHistorySummarizationInput input,
        ActivityOptions activityOptions) =>
        Workflow.ExecuteActivityAsync(
            (DurableChatActivities activities) => activities.SummarizeHistoryPrefixAsync(input),
            activityOptions);

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
        input = RequiredInput;

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
        _turnCount = input.CompletedTurnCount ?? InitializeTurnCount(_history);

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

        bool conditionMet;
        while (true)
        {
            if (_historyRecovery.IsBlocked)
            {
                _sessionAdmissionClosed = true;
                await WaitForHistoryRecoveryAsync(input, sessionCreatedAt).ConfigureAwait(true);
                if (_shutdownRequested)
                {
                    return;
                }
            }

            var waitingDeadline = _sessionIdleDeadline!.Value;
            var remaining = waitingDeadline - Workflow.UtcNow;
            if (_continueAsNewPending)
            {
                conditionMet = true;
            }
            else if (remaining <= TimeSpan.Zero)
            {
                conditionMet = false;
            }
            else
            {
                conditionMet = await Workflow.WaitConditionAsync(
                    () => _shutdownRequested
                          || _continueAsNewPending
                          || IsHistoryAdmissionRestricted
                          || (!_isProcessing && Workflow.ContinueAsNewSuggested)
                          || (!_isProcessing && _history.Count >= input.MaxEntryCount)
                          || _sessionIdleDeadline != waitingDeadline,
                    timeout: remaining).ConfigureAwait(true);
            }

            var canContinueAsNew = !_isProcessing
                                   && (_continueAsNewPending
                                       || Workflow.ContinueAsNewSuggested
                                       || _history.Count >= input.MaxEntryCount
                                       || IsHistoryAdmissionRestricted);
            if (conditionMet
                && _sessionIdleDeadline != waitingDeadline
                && !_shutdownRequested
                && !canContinueAsNew)
            {
                continue;
            }

            if (conditionMet)
            {
                break;
            }

            _sessionAdmissionClosed = true;
            await Workflow.WaitConditionAsync(
                () => Workflow.AllHandlersFinished
                      && !_isProcessing
                      && !HasPendingBackgroundTurns).ConfigureAwait(true);
            if (_shutdownRequested || _sessionIdleDeadline <= Workflow.UtcNow)
            {
                return;
            }

            _sessionAdmissionClosed = false;
        }

        if ((Workflow.ContinueAsNewSuggested
             || _history.Count >= input.MaxEntryCount
             || IsHistoryAdmissionRestricted
             || _continueAsNewPending)
            && !_shutdownRequested)
        {
            var historySnapshot = _history.ToList();
            var historyCountAtReduction = historySnapshot.Count;
            var explicitHistoryRecovery = Workflow.Patched("meai-history-recovery-v1");
            var hasConfiguredReducer = input.HistoryReducerKey is not null;
            var needsReduction = historySnapshot.Count >= input.MaxEntryCount;
            var noStrategyReductionRequired = _preparedHistory is null
                && !hasConfiguredReducer
                && input.HistoryCompactionStrategy == DurableHistoryCompactionStrategy.None
                && needsReduction;
            if (noStrategyReductionRequired && explicitHistoryRecovery)
            {
                RecordHistoryRecoveryFailure(
                    new Exceptions.DurableConfigurationException(
                        "History reduction is required, but no retained-history compaction strategy is configured."),
                    "compaction-blocked");
                _continueAsNewPending = false;
                await WaitForHistoryRecoveryAsync(input, sessionCreatedAt).ConfigureAwait(true);
                if (_shutdownRequested || _historyRecovery.IsBlocked)
                {
                    return;
                }

                var recoveredInput = CreateContinueAsNewInput(
                    input,
                    _preparedHistory ?? _history.ToList(),
                    _approvalMixin.GetResolvedApprovals(),
                    sessionCreatedAt,
                    SnapshotTerminalTurnOutcomes(),
                    _turnCount,
                    _sessionIdleDeadline) with { HistoryRecovery = _historyRecovery };
                if (input.EnableTransportPayloadGuard)
                {
                    Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                        Workflow.PayloadConverter,
                        recoveredInput,
                        input.MaximumTransportPayloadSizeBytes,
                        input.PayloadCodecExpansionAllowanceBytes,
                        "continue-as-new workflow input");
                }
                throw CreateContinueAsNewException(recoveredInput);
            }
            var reducerActivityOptions = hasConfiguredReducer
                ? new ActivityOptions
                {
                    StartToCloseTimeout = input.ActivityTimeout,
                    HeartbeatTimeout = input.HeartbeatTimeout,
                    RetryPolicy = explicitHistoryRecovery
                        ? new Temporalio.Common.RetryPolicy
                        {
                            MaximumAttempts = 3,
                            MaximumInterval = TimeSpan.FromSeconds(2),
                        }
                        : null,
                }
                : null;
            List<DurableSessionEntry> carriedHistory;
            try
            {
                if (_preparedHistory is not null)
                {
                    carriedHistory = _preparedHistory;
                }
                else if (hasConfiguredReducer)
                {
                    carriedHistory = await ApplyKeyedHistoryReducerAsync(
                        input.HistoryReducerKey!,
                        historySnapshot,
                        reducerActivityOptions!).ConfigureAwait(true);
                }
                else if (needsReduction && noStrategyReductionRequired)
                {
                    carriedHistory = DefaultBoundedTrim(historySnapshot, input.MaxEntryCount);
                }
                else if (needsReduction)
                {
                    carriedHistory = await ApplyConfiguredHistoryCompactionAsync(historySnapshot)
                        .ConfigureAwait(true);
                }
                else
                {
                    carriedHistory = historySnapshot;
                }

                _sessionAdmissionClosed = true;
                _continueAsNewPending = true;
                var drainBeforeSnapshot = Workflow.Patched("meai-can-drain-before-snapshot");
                if (_preparedHistory is null && (hasConfiguredReducer || noStrategyReductionRequired))
                {
                    ValidateHistoryProjection(historySnapshot, carriedHistory);
                }
                ValidateHistoryCompactionProgress(input, historySnapshot, carriedHistory);
                DurableChatWorkflowInput? carriedInput = null;
                if (!drainBeforeSnapshot)
                {
                    carriedInput = CreateContinueAsNewInput(
                        input,
                        carriedHistory,
                        _approvalMixin.GetResolvedApprovals(),
                        sessionCreatedAt,
                        SnapshotTerminalTurnOutcomes(),
                        _turnCount,
                        _sessionIdleDeadline) with { HistoryRecovery = _historyRecovery };
                    await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished)
                        .ConfigureAwait(true);
                }
                else
                {
                    while (true)
                    {
                        await Workflow.WaitConditionAsync(
                            () => Workflow.AllHandlersFinished
                                  && !_isProcessing
                                  && !HasPendingBackgroundTurns).ConfigureAwait(true);
                        if (_shutdownRequested)
                        {
                            return;
                        }

                        if (_history.Count == historyCountAtReduction)
                        {
                            break;
                        }

                        historySnapshot = _history.ToList();
                        historyCountAtReduction = historySnapshot.Count;
                        if (hasConfiguredReducer)
                        {
                            carriedHistory = await ApplyKeyedHistoryReducerAsync(
                                input.HistoryReducerKey!,
                                historySnapshot,
                                reducerActivityOptions!).ConfigureAwait(true);
                            ValidateHistoryProjection(historySnapshot, carriedHistory);
                        }
                        else if (_preparedHistory is not null && _history.Count == _preparedHistory.Count)
                        {
                            carriedHistory = _preparedHistory;
                        }
                        else if (historySnapshot.Count >= input.MaxEntryCount)
                        {
                            carriedHistory = await ApplyConfiguredHistoryCompactionAsync(historySnapshot)
                                .ConfigureAwait(true);
                        }
                        else
                        {
                            carriedHistory = historySnapshot;
                        }

                        ValidateHistoryCompactionProgress(input, historySnapshot, carriedHistory);
                    }

                    carriedInput = CreateContinueAsNewInput(
                        input,
                        carriedHistory,
                        _approvalMixin.GetResolvedApprovals(),
                        sessionCreatedAt,
                        SnapshotTerminalTurnOutcomes(),
                        _turnCount,
                        _sessionIdleDeadline) with { HistoryRecovery = _historyRecovery };
                }

                if (input.EnableTransportPayloadGuard)
                {
                    Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                        Workflow.PayloadConverter,
                        carriedInput!,
                        input.MaximumTransportPayloadSizeBytes,
                        input.PayloadCodecExpansionAllowanceBytes,
                        "continue-as-new workflow input");
                }
                throw CreateContinueAsNewException(carriedInput!);
            }
            catch (ContinueAsNewException)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordHistoryRecoveryFailure(exception, "compaction-blocked");
                _continueAsNewPending = false;
                await WaitForHistoryRecoveryAsync(input, sessionCreatedAt).ConfigureAwait(true);
                if (_shutdownRequested)
                {
                    return;
                }
                if (_historyRecovery.IsBlocked)
                {
                    return;
                }

                var recoveredInput = CreateContinueAsNewInput(
                    input,
                    _preparedHistory ?? _history.ToList(),
                    _approvalMixin.GetResolvedApprovals(),
                    sessionCreatedAt,
                    SnapshotTerminalTurnOutcomes(),
                    _turnCount,
                    _sessionIdleDeadline) with { HistoryRecovery = _historyRecovery };
                if (input.EnableTransportPayloadGuard)
                {
                    Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                        Workflow.PayloadConverter,
                        recoveredInput,
                        input.MaximumTransportPayloadSizeBytes,
                        input.PayloadCodecExpansionAllowanceBytes,
                        "continue-as-new workflow input");
                }
                throw CreateContinueAsNewException(recoveredInput);
            }
        }

        // Shutdown closes new-turn admission, but admitted handlers must finish before the
        // workflow completes. Preserve completed histories' prior command sequence on replay.
        if (Workflow.Patched("meai-shutdown-drain-handlers"))
        {
            await Workflow.WaitConditionAsync(
                () => Workflow.AllHandlersFinished
                      && !_isProcessing
                      && !HasPendingBackgroundTurns).ConfigureAwait(true);
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
        DateTimeOffset originalCreatedAt,
        IReadOnlyList<Internal.DurableTerminalTurnOutcome> terminalTurnOutcomes,
        int completedTurnCount,
        DateTimeOffset? sessionIdleDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(carriedHistory);
        ArgumentNullException.ThrowIfNull(approvalResolutionHistory);
        ArgumentNullException.ThrowIfNull(terminalTurnOutcomes);

        Internal.DurableToolsetAuthority.Resolve(input);

        return input with
        {
            CarriedHistory = carriedHistory,
            ApprovalResolutionHistory = approvalResolutionHistory,
            OriginalCreatedAt = originalCreatedAt,
            CompletedTurnCount = completedTurnCount,
            CarriedTerminalOutcomes = terminalTurnOutcomes,
            SessionIdleDeadline = sessionIdleDeadline ?? input.SessionIdleDeadline,
        };
    }

    /// <summary>
    /// Deterministic complete-turn trim used only by the explicitly selected trim strategy or
    /// the one-time blocked-history recovery update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The CAN trigger is <c>history.Count &gt;= maxEntryCount</c>. Keeping the latest complete
    /// turns and recording a truncation marker is an explicit lossy choice, never an implicit
    /// fallback for an unconfigured strategy. When the count trigger fires, the carried count is
    /// strictly below <paramref name="maxEntryCount"/> so the new run has headroom before the
    /// next count-triggered CAN.
    /// </para>
    /// <para>
    /// The target is half of <paramref name="maxEntryCount"/> (floored, minimum 1). The helper
    /// retains complete request/response turns and avoids starting the carried suffix with an
    /// orphaned response. A small history already below the target is returned unchanged. The
    /// projection is pure and order-preserving, so it is replay-safe.
    /// </para>
    /// </remarks>
    private static List<DurableSessionEntry> DefaultBoundedTrim(
        List<DurableSessionEntry> history,
        int maxEntryCount)
    {
        // The half-window policy must retain a complete request/response turn. Values below four
        // cannot do both, so configuration validation rejects them; retain the guard here for
        // callers that reach this private method through a future code path.
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntryCount, 4);
        var target = maxEntryCount / 2;

        if (history.Count <= target)
        {
            // Already below the trim target (e.g. SDK-suggested CAN, not count-driven) — pass
            // through unchanged. The workflow exits after the throw, so no aliasing risk.
            return history;
        }

        var firstRetainedIndex = history.Count - target;
        var trimmed = history.TakeLast(target).ToList();

        // RunTurnAsync appends a completed turn as an adjacent request/response pair sharing a
        // CorrelationId. When an odd target lands between that pair, retaining the response alone
        // gives the next run context that it cannot attribute to a request. Do not infer pairing
        // for unrelated or legacy entries: only remove the response when the omitted immediate
        // predecessor proves the boundary split a known pair.
        if (firstRetainedIndex > 0 &&
            history[firstRetainedIndex - 1] is DurableSessionRequest request &&
            trimmed[0] is DurableSessionResponse response &&
            SameLogicalTurn(request, response))
        {
            trimmed.RemoveAt(0);
        }

        return trimmed;
    }

    private async Task<List<DurableSessionEntry>> ApplyConfiguredHistoryCompactionAsync(
        List<DurableSessionEntry> history)
    {
        try
        {
            var projection = await ApplyConfiguredHistoryCompactionCoreAsync(history)
                .ConfigureAwait(true);
            RecordWorkflowMetric(
                DurableChatTelemetry.HistoryCompactionOutcomesMetricName,
                "outcome",
                "success");
            return projection;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordWorkflowMetric(
                DurableChatTelemetry.HistoryCompactionOutcomesMetricName,
                "outcome",
                "failure");
            throw;
        }
    }

    private async Task<List<DurableSessionEntry>> ApplyConfiguredHistoryCompactionCoreAsync(
        List<DurableSessionEntry> history)
    {
        var input = RequiredInput;
        var activityOptions = new ActivityOptions
        {
            StartToCloseTimeout = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
            RetryPolicy = new Temporalio.Common.RetryPolicy
            {
                MaximumAttempts = 3,
                MaximumInterval = TimeSpan.FromSeconds(2),
            },
        };

        List<DurableSessionEntry> projection;
        if (input.HistoryReducerKey is { } reducerKey)
        {
            projection = await ApplyKeyedHistoryReducerAsync(reducerKey, history, activityOptions)
                .ConfigureAwait(true);
            ValidateHistoryProjection(history, projection);
        }
        else
        {
            projection = input.HistoryCompactionStrategy switch
            {
                DurableHistoryCompactionStrategy.TrimRecentTurns =>
                    CreateExplicitTrimProjection(history),
                DurableHistoryCompactionStrategy.SummarizeOldestTurns =>
                    await CreateSummaryCompactionProjectionAsync(history, activityOptions).ConfigureAwait(true),
                _ => throw new Exceptions.DurableConfigurationException(
                    "History reduction is required, but no retained-history compaction strategy is configured."),
            };
        }

        if (projection.Count >= history.Count
            || (history.Count >= input.MaxEntryCount && projection.Count >= input.MaxEntryCount))
        {
            throw new ApplicationFailureException(
                "The configured history strategy did not reduce retained history below its active trigger.",
                errorType: "DurableHistoryCompactionNoProgress",
                nonRetryable: true);
        }

        if (input.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                projection,
                input.MaximumTransportPayloadSizeBytes,
                input.PayloadCodecExpansionAllowanceBytes,
                "compacted retained history");
        }

        return projection;
    }

    private List<DurableSessionEntry> CreateExplicitTrimProjection(
        IReadOnlyList<DurableSessionEntry> history)
    {
        var input = RequiredInput;
        var source = history.ToList();
        var retained = DefaultBoundedTrim(source, input.MaxEntryCount);
        if (retained.Count == 0)
        {
            throw new ApplicationFailureException(
                "The explicit trim could not retain a complete request/response turn.",
                errorType: "DurableHistoryCompactionProtectedContent",
                nonRetryable: true);
        }

        var firstRetainedIndex = source.FindIndex(entry => ReferenceEquals(entry, retained[0]));
        if (firstRetainedIndex <= 0)
        {
            throw new ApplicationFailureException(
                "The explicit trim would not make progress.",
                errorType: "DurableHistoryCompactionNoProgress",
                nonRetryable: true);
        }

        var removed = source.Take(firstRetainedIndex).ToList();
        ValidateHistoryProjection(source, retained);
        var markerId = Workflow.NewGuid().ToString("N");
        var marker = new TruncationMarkerEntry
        {
            EntryId = markerId,
            TurnId = markerId,
            CorrelationId = markerId,
            CreatedAt = Workflow.UtcNow,
            FirstRemovedEntryId = GetFirstRemovedEntryId(removed[0]),
            LastRemovedEntryId = GetLastRemovedEntryId(removed[^1]),
            RemovedEntryCount = removed.Sum(GetRepresentedEntryCount),
            FirstRemovedTurnId = GetFirstRemovedTurnId(removed[0]),
            LastRemovedTurnId = GetLastRemovedTurnId(removed[^1]),
            RemovedTurnCount = removed.Sum(GetRepresentedTurnCount),
        };
        return [marker, .. retained];
    }

    private async Task<List<DurableSessionEntry>> CreateSummaryCompactionProjectionAsync(
        IReadOnlyList<DurableSessionEntry> history,
        ActivityOptions activityOptions)
    {
        var source = history.ToList();
        var turns = new List<(DurableHistorySummaryTurn Turn, int Start, int End, int Entries, int Turns,
            string FirstEntryId, string LastEntryId, string FirstTurnId, string LastTurnId)>();
        var index = 0;
        if (source.FirstOrDefault() is TruncationMarkerEntry)
        {
            index = 1;
        }

        var latestTurnStart = source.FindLastIndex(entry => entry is DurableSessionRequest);
        while (index < source.Count)
        {
            if (source[index] is CompactionMarkerEntry priorMarker)
            {
                turns.Add((
                    new DurableHistorySummaryTurn
                    {
                        TurnId = priorMarker.TurnId ?? priorMarker.EntryId ?? priorMarker.FirstReplacedTurnId,
                        Messages =
                        [
                            new ChatMessage(ChatRole.User, "Previously summarized conversation:"),
                            new ChatMessage(ChatRole.Assistant, priorMarker.Summary.Text),
                        ],
                    },
                    index,
                    index,
                    priorMarker.ReplacedEntryCount,
                    priorMarker.ReplacedTurnCount,
                    priorMarker.FirstReplacedEntryId,
                    priorMarker.LastReplacedEntryId,
                    priorMarker.FirstReplacedTurnId,
                    priorMarker.LastReplacedTurnId));
                index++;
                continue;
            }

            if (source[index] is not DurableSessionRequest request
                || index + 1 >= source.Count
                || source[index + 1] is not DurableSessionResponse response
                || !SameLogicalTurn(request, response))
            {
                break;
            }

            var turnId = request.TurnId ?? request.CorrelationId;
            var protectedTurn = index == latestTurnStart
                                || response.CompletionReason == DurableTurnCompletionReason.Interrupted
                                || ContainsApprovalContent(request.Messages)
                                || ContainsApprovalContent(response.Messages)
                                || ContainsInstructionContent(request.Messages)
                                || ContainsInstructionContent(response.Messages);
            if (protectedTurn)
            {
                break;
            }

            turns.Add((
                new DurableHistorySummaryTurn
                {
                    TurnId = turnId,
                    Messages = [.. request.Messages, .. response.Messages],
                },
                index,
                index + 1,
                2,
                1,
                request.EntryId ?? request.CorrelationId,
                response.EntryId ?? response.CorrelationId,
                turnId,
                response.TurnId ?? turnId));
            index += 2;
        }

        if (turns.Count == 0)
        {
            throw new ApplicationFailureException(
                "No oldest complete, unprotected turn is available to summarize.",
                errorType: "DurableHistoryCompactionProtectedContent",
                nonRetryable: true);
        }

        var summarizationInput = new DurableHistorySummarizationInput
        {
            Turns = turns.Select(item => item.Turn).ToArray(),
            ClientKey = RequiredInput.HistorySummarizerClientKey,
            MaxContextTokens = RequiredInput.HistorySummarizerMaxContextTokens,
            OutputTokenReserve = RequiredInput.HistorySummarizerOutputTokenReserve,
            EnableTransportPayloadGuard = RequiredInput.EnableTransportPayloadGuard,
            MaximumTransportPayloadSizeBytes = RequiredInput.MaximumTransportPayloadSizeBytes,
            PayloadCodecExpansionAllowanceBytes = RequiredInput.PayloadCodecExpansionAllowanceBytes,
        };
        if (RequiredInput.EnableTransportPayloadGuard)
        {
            try
            {
                Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                    Workflow.PayloadConverter,
                    summarizationInput,
                    RequiredInput.MaximumTransportPayloadSizeBytes,
                    RequiredInput.PayloadCodecExpansionAllowanceBytes,
                    "history summarizer activity input");
            }
            catch (ApplicationFailureException exception)
                when (exception.ErrorType == "DurableTransportPayloadTooLarge")
            {
                throw new ApplicationFailureException(
                    "The history summarizer input exceeded the configured transport payload budget.",
                    exception,
                    errorType: "DurableHistorySummaryInputTooLarge",
                    nonRetryable: true);
            }
        }

        var result = await SummarizeHistoryPrefixAsync(summarizationInput, activityOptions)
            .ConfigureAwait(true);
        if (result.SummarizedTurnCount <= 0 || result.SummarizedTurnCount > turns.Count)
        {
            throw new ApplicationFailureException(
                "The history summarizer returned an invalid replaced-turn count.",
                errorType: "DurableHistorySummaryInvalidOutput",
                nonRetryable: true);
        }

        var selected = turns.Take(result.SummarizedTurnCount).ToArray();
        var lastSelected = selected[^1];
        var suffix = source.Skip(lastSelected.End + 1).ToList();
        ValidateHistoryProjection(source, suffix);
        var markerId = Workflow.NewGuid().ToString("N");
        var marker = new CompactionMarkerEntry
        {
            EntryId = markerId,
            TurnId = markerId,
            CorrelationId = markerId,
            CreatedAt = Workflow.UtcNow,
            FirstReplacedEntryId = selected[0].FirstEntryId,
            LastReplacedEntryId = lastSelected.LastEntryId,
            ReplacedEntryCount = selected.Sum(item => item.Entries),
            FirstReplacedTurnId = selected[0].FirstTurnId,
            LastReplacedTurnId = lastSelected.LastTurnId,
            ReplacedTurnCount = selected.Sum(item => item.Turns),
            Strategy = "summarize-oldest-turns",
            ModelId = result.Summary.ModelId ?? string.Empty,
            Summary = result.Summary,
        };
        return [.. source.Take(selected[0].Start), marker, .. suffix];
    }

    private static string GetFirstRemovedEntryId(DurableSessionEntry entry) => entry switch
    {
        CompactionMarkerEntry marker => marker.FirstReplacedEntryId,
        TruncationMarkerEntry marker => marker.FirstRemovedEntryId,
        _ => entry.EntryId ?? entry.CorrelationId,
    };

    private static string GetLastRemovedEntryId(DurableSessionEntry entry) => entry switch
    {
        CompactionMarkerEntry marker => marker.LastReplacedEntryId,
        TruncationMarkerEntry marker => marker.LastRemovedEntryId,
        _ => entry.EntryId ?? entry.CorrelationId,
    };

    private static string GetFirstRemovedTurnId(DurableSessionEntry entry) => entry switch
    {
        CompactionMarkerEntry marker => marker.FirstReplacedTurnId,
        TruncationMarkerEntry marker => marker.FirstRemovedTurnId,
        _ => entry.TurnId ?? entry.CorrelationId,
    };

    private static string GetLastRemovedTurnId(DurableSessionEntry entry) => entry switch
    {
        CompactionMarkerEntry marker => marker.LastReplacedTurnId,
        TruncationMarkerEntry marker => marker.LastRemovedTurnId,
        _ => entry.TurnId ?? entry.CorrelationId,
    };

    private static int GetRepresentedEntryCount(DurableSessionEntry entry) => entry switch
    {
        CompactionMarkerEntry marker => marker.ReplacedEntryCount,
        TruncationMarkerEntry marker => marker.RemovedEntryCount,
        _ => 1,
    };

    private static int GetRepresentedTurnCount(DurableSessionEntry entry) => entry switch
    {
        CompactionMarkerEntry marker => marker.ReplacedTurnCount,
        TruncationMarkerEntry marker => marker.RemovedTurnCount,
        DurableSessionRequest => 1,
        _ => 0,
    };

    private static void ValidateHistoryProjection(
        IReadOnlyList<DurableSessionEntry> source,
        IReadOnlyList<DurableSessionEntry> projection)
    {
        if (projection is null)
        {
            throw new ApplicationFailureException(
                "The configured history strategy returned no projection.",
                errorType: "DurableHistoryProjectionInvalid",
                nonRetryable: true);
        }

        try
        {
            Internal.DurableHistoryProjectionValidator.Validate(source, projection);
        }
        catch (Exceptions.DurableHistoryProjectionException exception)
        {
            throw new ApplicationFailureException(
                exception.Message,
                exception,
                errorType: "DurableHistoryProjectionInvalid",
                nonRetryable: true);
        }
    }

    private static void ValidateHistoryCompactionProgress(
        DurableChatWorkflowInput input,
        IReadOnlyList<DurableSessionEntry> source,
        IReadOnlyList<DurableSessionEntry> projection)
    {
        if (source.Count >= input.MaxEntryCount
            && (projection.Count >= source.Count || projection.Count >= input.MaxEntryCount))
        {
            throw new ApplicationFailureException(
                "The configured history strategy did not reduce retained history below its active trigger.",
                errorType: "DurableHistoryCompactionNoProgress",
                nonRetryable: true);
        }
    }

    /// <summary>
    /// Rejects a request that conflicts with the active or retained terminal turn.
    /// Call from a synchronous <c>[WorkflowUpdateValidator]</c> before accepting an update.
    /// </summary>
    /// <param name="requestId">Caller-known logical request ID, when supplied.</param>
    /// <param name="admissionFingerprint">Fingerprint of caller-controlled effect-bearing input.</param>
    protected void ValidateTurnAdmission(string? requestId, string admissionFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(admissionFingerprint);

        if (requestId is not null
            && _terminalTurns.TryGetValue(requestId, out var terminal))
        {
            EnsureAdmissionFingerprint(terminal, requestId, admissionFingerprint);
            return;
        }

        var matchesActiveTurn = _activeTurn is not null
                                && string.Equals(requestId, _activeTurn.RequestId, StringComparison.Ordinal);
        if (_sessionAdmissionClosed && !matchesActiveTurn)
        {
            throw TurnAdmissionFailure(
                "The session is closing admission and no longer accepts new turns.",
                "DurableSessionAdmissionClosed");
        }

        if (_historyRecovery.IsBlocked && !matchesActiveTurn)
        {
            RecordWorkflowMetric(
                DurableChatTelemetry.TurnAdmissionRejectionsMetricName,
                "reason",
                "history_blocked");
            throw new ApplicationFailureException(
                "The session is blocked until retained history is compacted or explicitly trimmed.",
                errorType: "DurableHistoryBlocked",
                nonRetryable: true);
        }

        if (IsHistoryAdmissionRestricted && !matchesActiveTurn)
        {
            throw HistoryBudgetFailure();
        }

        if (_activeTurn is null)
        {
            return;
        }

        if (!string.Equals(requestId, _activeTurn.RequestId, StringComparison.Ordinal))
        {
            throw TurnAdmissionFailure(
                "The session already has an active turn.",
                "DurableTurnBusy");
        }

        EnsureAdmissionFingerprint(_activeTurn, requestId, admissionFingerprint);
        if (_activeTurn.JoinerCount >= (Input?.MaxConcurrentTurnJoiners ?? 32))
        {
            throw PendingJoinerFailure();
        }
    }

    /// <summary>
    /// Validates a session-style request using the same stable input fingerprint as
    /// <see cref="BeginTurnAdmission(DurableChatInput)"/>.
    /// </summary>
    protected void ValidateTurnAdmission(DurableChatInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Messages is null or { Count: 0 })
        {
            throw TurnAdmissionFailure(
                "At least one message is required.",
                "DurableTurnInvalidRequest");
        }

        if (input.RequestId is not null && string.IsNullOrWhiteSpace(input.RequestId))
        {
            throw TurnAdmissionFailure(
                "Request ID cannot be empty or whitespace.",
                "DurableTurnInvalidRequest");
        }

        if (IsShutdownRequested)
        {
            throw TurnAdmissionFailure("Session has been shut down.", "DurableSessionShutdown");
        }

        if (IsContinueAsNewPending)
        {
            throw TurnAdmissionFailure(
                "The session is continuing as new. Retry the turn against the current run.",
                "DurableTurnContinueAsNewPending");
        }

        ValidateTurnAdmission(
            input.RequestId,
            Internal.DurableRequestFingerprint.CreateChatAdmission(input));
    }

    /// <summary>
    /// Reserves a logical turn before the handler's first await. A matching request joins its
    /// pending execution or retained terminal outcome; any other request is rejected.
    /// </summary>
    protected TurnAdmission BeginTurnAdmission(
        string requestId,
        string admissionFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(admissionFingerprint);
        ValidateTurnAdmission(requestId, admissionFingerprint);
        if (Input is not null)
        {
            RenewSessionIdleDeadline();
        }

        if (_terminalTurns.TryGetValue(requestId, out var terminal))
        {
            return TurnAdmission.Create(terminal, requestId, isOwner: false);
        }

        if (_activeTurn is { } active)
        {
            active.JoinerCount++;
            return TurnAdmission.Create(active, requestId, isOwner: false, isJoiner: true);
        }

        var record = new TurnExecutionRecord(
            requestId,
            admissionFingerprint,
            Workflow.CancellationToken);
        _activeTurn = record;
        _isProcessing = true;
        return TurnAdmission.Create(record, requestId, isOwner: true);
    }

    /// <summary>
    /// Reconciles an early update admission after workflow input has initialized. An update-with-
    /// start may reserve a request before a Continue-as-New ledger is restored.
    /// </summary>
    protected TurnAdmission RefreshTurnAdmission(
        TurnAdmission admission,
        string requestId,
        string admissionFingerprint)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!_terminalTurns.TryGetValue(requestId, out var terminal))
        {
            return admission;
        }

        var current = GetTurnRecord(admission);
        if (!string.Equals(
            terminal.AdmissionFingerprint,
            admissionFingerprint,
            StringComparison.Ordinal))
        {
            RejectTurnAdmission(
                admission,
                TurnAdmissionFailure(
                    $"Request ID '{requestId}' was already used with different effect-bearing input.",
                    "DurableTurnConflict"));
            throw TurnAdmissionFailure(
                $"Request ID '{requestId}' was already used with different effect-bearing input.",
                "DurableTurnConflict");
        }

        if (admission.IsOwner && ReferenceEquals(_activeTurn, current))
        {
            _activeTurn = null;
            _isProcessing = false;
            DisposeTurnCancellationSourceIfSafe(current);
        }
        else if (admission.IsJoiner && current.JoinerCount > 0)
        {
            current.JoinerCount--;
        }

        return TurnAdmission.Create(terminal, requestId, isOwner: false);
    }

    /// <summary>
    /// Applies initialization-dependent restrictions to a turn admitted by update-with-start
    /// before the workflow input was available to its validator.
    /// </summary>
    protected void ValidateInitializedTurnAdmission(TurnAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.IsOwner && (_historyRecovery.IsBlocked || IsHistoryAdmissionRestricted))
        {
            var failure = _historyRecovery.IsBlocked
                ? new ApplicationFailureException(
                    "The session is blocked until retained history is compacted or explicitly trimmed.",
                    errorType: "DurableHistoryBlocked",
                    nonRetryable: true)
                : HistoryBudgetFailure();
            RejectTurnAdmission(admission, failure);
            throw failure;
        }
    }

    /// <summary>
    /// Reserves a session-style request before the handler's first await. Use the same input in
    /// the paired synchronous Update validator via <see cref="ValidateTurnAdmission(DurableChatInput)"/>.
    /// </summary>
    protected TurnAdmission BeginTurnAdmission(DurableChatInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateTurnAdmission(input);
        var requestId = input.RequestId ?? Workflow.NewGuid().ToString("N");
        return BeginTurnAdmission(
            requestId,
            Internal.DurableRequestFingerprint.CreateChatAdmission(input));
    }

    /// <summary>
    /// Verifies the worker-authoritative fingerprint once initialization-dependent policy is
    /// available. The caller-controlled fingerprint has already protected admission.
    /// </summary>
    protected void SetTurnInputFingerprint(TurnAdmission admission, string inputFingerprint)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFingerprint);

        var record = GetTurnRecord(admission);
        if (record.TurnInputFingerprint is { } known
            && !string.Equals(known, inputFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The admitted turn's durable input fingerprint changed after admission.");
        }

        record.TurnInputFingerprint = inputFingerprint;
    }

    /// <summary>
    /// Waits for the owning handler and returns its terminal output, or rethrows its recorded
    /// failure. Only matching duplicate handlers should call this.
    /// </summary>
    protected async Task<(TOutput Output, DurableSessionResponse ResponseEntry)> WaitForTurnOutcomeAsync(
        TurnAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        RenewSessionIdleDeadline();
        try
        {
            await Workflow.WaitConditionAsync(() => GetTurnRecord(admission).IsTerminal).ConfigureAwait(true);

            var record = GetTurnRecord(admission);
            if (record.Failure is { } failure)
            {
                throw new ApplicationFailureException(
                    failure.Message,
                    errorType: failure.ErrorType,
                    nonRetryable: failure.NonRetryable);
            }

            return (record.Output!, record.ResponseEntry!);
        }
        finally
        {
            if (admission.IsJoiner && GetTurnRecord(admission).JoinerCount > 0)
            {
                GetTurnRecord(admission).JoinerCount--;
            }
        }
    }

    /// <summary>
    /// Completes an admission that failed before <see cref="RunTurnAsync"/> took ownership.
    /// </summary>
    protected void FailTurnAdmission(TurnAdmission admission, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(exception);
        if (!admission.IsOwner || GetTurnRecord(admission).IsTerminal)
        {
            return;
        }

        var record = GetTurnRecord(admission);
        if (exception is ApplicationFailureException
            {
                ErrorType: Exceptions.DurableChatSessionEpochMismatchException.ErrorType,
            })
        {
            RejectTurnAdmission(admission, exception);
            return;
        }

        if (record.CancellationToken.IsCancellationRequested)
        {
            var cancellationFailure = CreateTurnCancellationFailure(exception);
            CompleteTurnFailure(record, cancellationFailure);
            throw cancellationFailure;
        }

        CompleteTurnFailure(record, exception);
    }

    private static ApplicationFailureException CreateTurnCancellationFailure(Exception exception) =>
        new(
            "The durable turn was cancelled before it completed. External effects may have occurred and are not rolled back.",
            exception,
            errorType: "DurableTurnCancelled",
            nonRetryable: true);

    private void RejectTurnAdmission(TurnAdmission admission, Exception exception)
    {
        var record = GetTurnRecord(admission);
        var errorType = exception is ApplicationFailureException failure
            ? failure.ErrorType ?? "DurableTurnRejected"
            : exception.GetType().FullName ?? exception.GetType().Name;
        var nonRetryable = exception is ApplicationFailureException { NonRetryable: true };
        record.Failure = new TurnFailure(errorType, exception.Message, nonRetryable);
        record.IsTerminal = true;
        if (ReferenceEquals(_activeTurn, record))
        {
            _activeTurn = null;
        }
        _isProcessing = false;
        DisposeTurnCancellationSourceIfSafe(record);
    }

    private static TurnExecutionRecord GetTurnRecord(TurnAdmission admission) =>
        (TurnExecutionRecord)admission.RecordState;

    private void EnsureAdmissionFingerprint(
        TurnExecutionRecord record,
        string? requestId,
        string admissionFingerprint)
    {
        if (!string.Equals(record.AdmissionFingerprint, admissionFingerprint, StringComparison.Ordinal))
        {
            throw TurnAdmissionFailure(
                $"Request ID '{requestId}' was already used with different effect-bearing input.",
                "DurableTurnConflict");
        }
    }

    private ApplicationFailureException TurnAdmissionFailure(string message, string errorType)
    {
        var reason = errorType switch
        {
            "DurableTurnConflict" => "conflict",
            "DurableTurnBusy" => "busy",
            "DurableSessionAdmissionClosed" or "DurableSessionShutdown" => "admission_closed",
            "DurableHistoryBlocked" => "history_blocked",
            "DurableHistoryBudgetBlocked" => "history_budget",
            "DurableTurnPending" => "pending_limit",
            _ => null,
        };
        if (reason is not null)
        {
            RecordWorkflowMetric(
                DurableChatTelemetry.TurnAdmissionRejectionsMetricName,
                "reason",
                reason);
        }

        return new ApplicationFailureException(message, errorType: errorType, nonRetryable: true);
    }

    private void RecordApprovalOutcome(DurableApprovalDecision decision)
    {
        var outcome = decision.ExpirationReason.HasValue
            ? "expired"
            : decision.Approved ? "approved" : "rejected";
        RecordWorkflowMetric(
            DurableChatTelemetry.ApprovalOutcomesMetricName,
            "outcome",
            outcome);
        if (decision.ExpirationReason is { } expirationReason)
        {
            RecordWorkflowMetric(
                DurableChatTelemetry.ApprovalExpiriesMetricName,
                "reason",
                expirationReason == DurableApprovalExpirationReason.HistoryBudget
                    ? "history_budget"
                    : "timeout");
        }
    }

    private void RecordWorkflowMetric(string name, string tagName, string tagValue)
    {
        var key = $"{name}\0{tagName}\0{tagValue}";
        if (!_workflowMetrics.TryGetValue(key, out var counter))
        {
            counter = Workflow.MetricMeter
                .CreateCounter<long>(name)
                .WithTags([new KeyValuePair<string, object>(tagName, tagValue)]);
            _workflowMetrics.Add(key, counter);
        }

        counter.Add(1);
    }

    private static void ValidateTerminalOutcomeLimits(DurableChatWorkflowInput input)
    {
        if (input.MaxTerminalOutcomeCount <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(input.MaxTerminalOutcomeCount)} must be positive.");
        }

        if (input.MaxTerminalOutcomeBytes <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(input.MaxTerminalOutcomeBytes)} must be positive.");
        }
    }

    private static void ValidateHistoryBudgetLimits(DurableChatWorkflowInput input)
    {
        if (input.HistoryEventSoftLimit <= 0
            || input.HistoryEventReserveLimit <= input.HistoryEventSoftLimit)
        {
            throw new InvalidOperationException(
                $"{nameof(input.HistoryEventSoftLimit)} must be positive and smaller than " +
                $"{nameof(input.HistoryEventReserveLimit)}.");
        }

        if (input.HistorySizeSoftLimitBytes <= 0
            || input.HistorySizeReserveLimitBytes <= input.HistorySizeSoftLimitBytes)
        {
            throw new InvalidOperationException(
                $"{nameof(input.HistorySizeSoftLimitBytes)} must be positive and smaller than " +
                $"{nameof(input.HistorySizeReserveLimitBytes)}.");
        }

        if (input.MaxConcurrentTurnJoiners <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(input.MaxConcurrentTurnJoiners)} must be positive.");
        }
    }

    private bool IsHistoryAdmissionRestricted =>
        Input is { } input
        && (Workflow.CurrentHistoryLength >= input.HistoryEventSoftLimit
            || Workflow.CurrentHistorySize >= input.HistorySizeSoftLimitBytes);

    private bool IsHistoryReserveReached =>
        Input is { } input
        && (Workflow.CurrentHistoryLength >= input.HistoryEventReserveLimit
            || Workflow.CurrentHistorySize >= input.HistorySizeReserveLimitBytes);

    private ApplicationFailureException HistoryBudgetFailure()
    {
        RecordWorkflowMetric(
            DurableChatTelemetry.TurnAdmissionRejectionsMetricName,
            "reason",
            "history_budget");
        return new(
            "The workflow history budget is restricting admission. Retry after the session continues as new.",
            errorType: "DurableHistoryBudgetBlocked",
            nonRetryable: true);
    }

    private ApplicationFailureException PendingJoinerFailure()
    {
        RecordWorkflowMetric(
            DurableChatTelemetry.TurnAdmissionRejectionsMetricName,
            "reason",
            "pending_limit");
        return new(
            "The active turn has reached its concurrent joiner limit. Observe the request status and retry later.",
            errorType: "DurableTurnPending",
            nonRetryable: true);
    }

    private void RestoreTerminalTurnOutcomes(
        IReadOnlyList<Internal.DurableTerminalTurnOutcome>? outcomes)
    {
        if (outcomes is null)
        {
            return;
        }

        foreach (var outcome in outcomes)
        {
            var record = new TurnExecutionRecord(outcome.RequestId, outcome.AdmissionFingerprint)
            {
                TurnInputFingerprint = outcome.TurnInputFingerprint,
                IsTerminal = true,
                Failure = outcome.Failure is { } failure
                    ? new TurnFailure(failure.ErrorType, failure.Message, failure.NonRetryable)
                    : null,
            };
            if (outcome.Output is { } serializedOutput)
            {
                record.SerializedOutput = serializedOutput;
                record.Output = DeserializeTurnOutput(serializedOutput);
            }

            if (outcome.ResponseEntry is { } serializedResponse)
            {
                record.SerializedResponseEntry = serializedResponse;
                record.ResponseEntry = DeserializeTurnResponseEntry(serializedResponse);
            }

            if (record.Failure is null
                && (record.SerializedOutput is null || record.SerializedResponseEntry is null))
            {
                throw new InvalidOperationException(
                    $"Carried terminal outcome '{outcome.RequestId}' is incomplete.");
            }

            if (_terminalTurns.ContainsKey(record.RequestId))
            {
                throw new InvalidOperationException(
                    $"Carried terminal outcomes contain duplicate request ID '{record.RequestId}'.");
            }

            AddTerminalTurnRecord(record, enforceBounds: false);
            DisposeTurnCancellationSourceIfSafe(record);
        }

        EnforceTerminalTurnLedgerBounds();
    }

    private IReadOnlyList<Internal.DurableTerminalTurnOutcome> SnapshotTerminalTurnOutcomes()
    {
        var outcomes = new List<Internal.DurableTerminalTurnOutcome>(_terminalTurnOrder.Count);
        foreach (var requestId in _terminalTurnOrder)
        {
            if (!_terminalTurns.TryGetValue(requestId, out var record))
            {
                continue;
            }

            outcomes.Add(new Internal.DurableTerminalTurnOutcome
            {
                RequestId = record.RequestId,
                AdmissionFingerprint = record.AdmissionFingerprint,
                TurnInputFingerprint = record.TurnInputFingerprint,
                Output = record.SerializedOutput,
                ResponseEntry = record.SerializedResponseEntry,
                Failure = record.Failure is { } failure
                    ? new Internal.DurableTerminalTurnFailure
                    {
                        ErrorType = failure.ErrorType,
                        Message = failure.Message,
                        NonRetryable = failure.NonRetryable,
                    }
                    : null,
            });
        }

        return outcomes;
    }

    private void AddTerminalTurnRecord(
        TurnExecutionRecord record,
        bool enforceBounds = true)
    {
        var outcome = new Internal.DurableTerminalTurnOutcome
        {
            RequestId = record.RequestId,
            AdmissionFingerprint = record.AdmissionFingerprint,
            TurnInputFingerprint = record.TurnInputFingerprint,
            Output = record.SerializedOutput,
            ResponseEntry = record.SerializedResponseEntry,
            Failure = record.Failure is { } failure
                ? new Internal.DurableTerminalTurnFailure
                {
                    ErrorType = failure.ErrorType,
                    Message = failure.Message,
                    NonRetryable = failure.NonRetryable,
                }
                : null,
        };
        record.SerializedSizeBytes =
            JsonSerializer.SerializeToUtf8Bytes(outcome, TerminalOutcomeJsonSerializerOptions).Length;

        if (record.SerializedSizeBytes > _maxTerminalOutcomeBytes)
        {
            return;
        }

        _terminalTurns[record.RequestId] = record;
        _terminalTurnOrder.Add(record.RequestId);
        _terminalTurnLedgerBytes += record.SerializedSizeBytes;
        if (enforceBounds)
        {
            EnforceTerminalTurnLedgerBounds();
        }
    }

    private void EnforceTerminalTurnLedgerBounds()
    {
        while (_terminalTurnOrder.Count > _maxTerminalOutcomeCount
               || _terminalTurnLedgerBytes > _maxTerminalOutcomeBytes)
        {
            var oldestRequestId = _terminalTurnOrder[0];
            _terminalTurnOrder.RemoveAt(0);
            if (_terminalTurns.Remove(oldestRequestId, out var evicted))
            {
                _terminalTurnLedgerBytes -= evicted.SerializedSizeBytes;
            }
        }
    }

    private void CompleteTurnSuccess(
        TurnExecutionRecord record,
        TOutput output,
        DurableSessionResponse responseEntry)
    {
        RecordWorkflowMetric(
            DurableChatTelemetry.TurnOutcomesMetricName,
            "outcome",
            "completed");
        record.SerializedOutput = SerializeTurnOutput(output);
        record.SerializedResponseEntry = SerializeTurnResponseEntry(responseEntry);
        record.Output = output;
        record.ResponseEntry = responseEntry;
        record.IsTerminal = true;
        AddTerminalTurnRecord(record);
        if (ReferenceEquals(_activeTurn, record))
        {
            _activeTurn = null;
        }
        _isProcessing = false;
        DisposeTurnCancellationSourceIfSafe(record);
    }

    private void CompleteTurnFailure(TurnExecutionRecord record, Exception exception)
    {
        RecordWorkflowMetric(
            DurableChatTelemetry.TurnOutcomesMetricName,
            "outcome",
            exception is ApplicationFailureException { ErrorType: "DurableTurnCancelled" }
                ? "cancelled"
                : "failed");
        var (errorType, message, nonRetryable) = exception is ApplicationFailureException failure
            ? (failure.ErrorType ?? "DurableTurnFailure", failure.Message, failure.NonRetryable)
            : (exception.GetType().FullName ?? exception.GetType().Name, exception.Message, false);
        record.Failure = new TurnFailure(errorType, message, nonRetryable);
        record.IsTerminal = true;
        AddTerminalTurnRecord(record);
        if (ReferenceEquals(_activeTurn, record))
        {
            _activeTurn = null;
        }
        _isProcessing = false;
        DisposeTurnCancellationSourceIfSafe(record);
    }

    private void DisposeTurnCancellationSourceIfSafe(TurnExecutionRecord record)
    {
        record.CancellationSource.Dispose();
    }

    /// <summary>
    /// Executes a previously admitted turn, appending its request and response to history.
    /// </summary>
    /// <param name="requestEntry">The durable request entry for this turn.</param>
    /// <param name="admission">The previously reserved logical turn admission.</param>
    /// <param name="chatOptions">Optional chat options for the activity dispatch.</param>
    /// <param name="cancellationToken">Cancellation token for the workflow update.</param>
    protected async Task<(TOutput Output, DurableSessionResponse ResponseEntry)> RunTurnAsync(
        DurableSessionRequest requestEntry,
        TurnAdmission admission,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestEntry);
        ArgumentNullException.ThrowIfNull(admission);

        requestEntry.RequestId ??= Workflow.NewGuid().ToString("N");
        requestEntry.InputFingerprint ??= Internal.DurableRequestFingerprint.CreateSessionTurn(
            requestEntry,
            chatOptions,
            RequiredInput);

        if (!admission.IsOwner)
        {
            return await WaitForTurnOutcomeAsync(admission).ConfigureAwait(true);
        }

        if (!string.Equals(
            requestEntry.RequestId,
            GetTurnRecord(admission).RequestId,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The turn request does not match its admission.");
        }

        SetTurnInputFingerprint(admission, requestEntry.InputFingerprint);
        RenewSessionIdleDeadline();

        var turnRecord = GetTurnRecord(admission);
        var historyCountBeforeTurn = _history.Count;
        var turnCountBeforeTurn = _turnCount;
        var rollbackParticipant = this as Internal.IDurableTurnRollbackParticipant;
        JsonElement? participantStateBeforeTurn = null;
        var participantStateCaptured = false;
        DurableSessionRequest? requestEntryToAppend = null;
        _interruptedToolBatches.Clear();

        try
        {
            ThrowIfCurrentTurnCancellationRequested();

            // Capture derived transactional state only after this turn owns the gate. A queued
            // update may have started while the preceding turn was suspended; capturing outside
            // this critical section would give it a stale snapshot that could erase the preceding
            // turn's committed state if the queued turn later failed.
            if (rollbackParticipant is not null)
            {
                participantStateBeforeTurn = rollbackParticipant.CaptureTurnRollbackState();
                participantStateCaptured = true;
            }

            requestEntry.EntryId ??= Workflow.NewGuid().ToString("N");
            requestEntry.TurnId ??= Workflow.NewGuid().ToString("N");

            // Append the request entry for this turn. When external-history mode is on we
            // replace the messages with an empty list so the in-workflow history never holds
            // the raw user prompt — only request identity and metadata for turn counting.
            requestEntryToAppend = ShouldStripMessagesFromHistoryEntry()
                ? (DurableSessionRequest)StripMessagesFromEntry(requestEntry)
                : requestEntry;
            _history.Add(requestEntryToAppend);
            (_currentTurnStartIndex, _previousTurnRanges) = BuildModelContextTurnRanges(requestEntryToAppend);

            _turnCount++;

            var activityOptions = new ActivityOptions
            {
                StartToCloseTimeout = RequiredInput.ActivityTimeout,
                HeartbeatTimeout = RequiredInput.HeartbeatTimeout,
                RetryPolicy = Internal.DefaultRetryPolicy.ResolveForModel(RequiredInput.RetryPolicy),
                Summary = DurableChatClient.BuildActivitySummary(chatOptions),
                CancellationToken = turnRecord.CancellationToken,
                CancellationType = ActivityCancellationType.WaitCancellationCompleted,
            };

            var output = await ExecuteTurnAsync(activityOptions, requestEntry, chatOptions);
            ThrowIfCurrentTurnCancellationRequested();

            // Build the response entry, then optionally strip its message payload before
            // appending so external-history mode keeps the workflow history metadata-only.
            var responseEntry = BuildResponseEntry(
                requestEntry.CorrelationId,
                requestEntry.RequestId,
                output,
                Workflow.UtcNow);
            responseEntry.EntryId ??= Workflow.NewGuid().ToString("N");
            responseEntry.TurnId = requestEntry.TurnId;
            if (RequiredInput.EnableTransportPayloadGuard)
            {
                Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                    Workflow.PayloadConverter,
                    output,
                    RequiredInput.MaximumTransportPayloadSizeBytes,
                    RequiredInput.PayloadCodecExpansionAllowanceBytes,
                    "workflow update result");
                Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                    Workflow.PayloadConverter,
                    responseEntry,
                    RequiredInput.MaximumTransportPayloadSizeBytes,
                    RequiredInput.PayloadCodecExpansionAllowanceBytes,
                    "workflow update result");
            }
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

            CompleteTurnSuccess(GetTurnRecord(admission), output, responseEntry);
            RenewSessionIdleDeadline();
            return (output, responseEntry);
        }
        catch (Exception exception)
        {
            // A turn is a transactional unit from the session's perspective. If model/tool
            // execution, response projection, or search-attribute publication fails, retain
            // neither the request nor any partially-created response and do not count the
            // failed attempt as a completed turn. Only the admitted owner can mutate these
            // collections while this rollback runs.
            if (_history.Count > historyCountBeforeTurn)
            {
                _history.RemoveRange(
                    historyCountBeforeTurn,
                    _history.Count - historyCountBeforeTurn);
            }

            _turnCount = turnCountBeforeTurn;

            // Restore derived state before releasing the active-turn admission.
            // This keeps the base history and any participant state in one transaction boundary.
            if (participantStateCaptured)
            {
                rollbackParticipant!.RestoreTurnRollbackState(participantStateBeforeTurn);
            }

            var terminalException = IsCurrentTurnCancellationRequested()
                ? CreateTurnCancellationFailure(exception)
                : exception;

            if (requestEntryToAppend is not null && HasDispatchedInterruptedToolCall())
            {
                var interruptedResponse = CreateInterruptedResponseEntry(
                    requestEntryToAppend.CorrelationId,
                    requestEntry.RequestId,
                    IsCurrentTurnCancellationRequested());
                interruptedResponse.EntryId ??= Workflow.NewGuid().ToString("N");
                interruptedResponse.TurnId = requestEntryToAppend.TurnId;
                _history.Add(requestEntryToAppend);
                _history.Add(ShouldStripMessagesFromHistoryEntry()
                    ? (DurableSessionResponse)StripMessagesFromEntry(interruptedResponse)
                    : interruptedResponse);
            }

            CompleteTurnFailure(turnRecord, terminalException);
            if (!IsCurrentTurnCancellationRequested())
            {
                RenewSessionIdleDeadline();
            }
            if (!ReferenceEquals(terminalException, exception))
            {
                throw terminalException;
            }

            throw;
        }
        finally
        {
            _interruptedToolBatches.Clear();
            _isProcessing = false;
        }
    }

    /// <summary>Starts tracking the tool calls in one model response for interruption recovery.</summary>
    protected int BeginInterruptedToolBatch(IReadOnlyList<FunctionCallContent> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        _interruptedToolBatches.Add(new InterruptedToolBatch(calls));
        return _interruptedToolBatches.Count - 1;
    }

    /// <summary>Marks a tool call as dispatched, conservatively treating its effect as uncertain.</summary>
    protected void MarkInterruptedToolCallDispatched(int batchIndex, int callIndex)
    {
        GetInterruptedToolCall(batchIndex, callIndex).Dispatched = true;
    }

    /// <summary>Records a known tool result, including a synthetic non-dispatch result.</summary>
    protected void RecordInterruptedToolCallResult(
        int batchIndex,
        int callIndex,
        object? result,
        bool wasDispatched)
    {
        var call = GetInterruptedToolCall(batchIndex, callIndex);
        call.Dispatched |= wasDispatched;
        call.HasResult = true;
        call.Result = result;
    }

    private InterruptedToolCall GetInterruptedToolCall(int batchIndex, int callIndex)
    {
        if ((uint)batchIndex >= (uint)_interruptedToolBatches.Count)
        {
            throw new InvalidOperationException("The interrupted tool batch is not active.");
        }

        var calls = _interruptedToolBatches[batchIndex].Calls;
        if ((uint)callIndex >= (uint)calls.Count)
        {
            throw new InvalidOperationException("The interrupted tool call index is invalid.");
        }

        return calls[callIndex];
    }

    private bool HasDispatchedInterruptedToolCall() =>
        _interruptedToolBatches.Any(batch => batch.Calls.Any(call => call.Dispatched));

    private DurableSessionResponse CreateInterruptedResponseEntry(
        string correlationId,
        string? requestId,
        bool wasCancelled)
    {
        const int maximumInterruptedResponseBytes = 64 * 1024;
        var maximumPayloadBytes = Math.Min(
            maximumInterruptedResponseBytes,
            RequiredInput.MaximumTransportPayloadSizeBytes);
        var expansionAllowanceBytes = Math.Min(
            RequiredInput.PayloadCodecExpansionAllowanceBytes,
            maximumPayloadBytes - 1);
        var response = CreateInterruptedResponseEntryCore(
            correlationId,
            requestId,
            wasCancelled,
            compact: false);

        try
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                response,
                maximumPayloadBytes,
                expansionAllowanceBytes,
                "interrupted-turn history entry");
            return response;
        }
        catch (ApplicationFailureException exception)
            when (exception.ErrorType == "DurableTransportPayloadTooLarge")
        {
            var compactResponse = CreateInterruptedResponseEntryCore(
                correlationId,
                requestId,
                wasCancelled,
                compact: true);
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                compactResponse,
                maximumPayloadBytes,
                expansionAllowanceBytes,
                "compact interrupted-turn history entry");
            return compactResponse;
        }
    }

    private DurableSessionResponse CreateInterruptedResponseEntryCore(
        string correlationId,
        string? requestId,
        bool wasCancelled,
        bool compact)
    {
        var messages = new List<ChatMessage>();
        foreach (var batch in _interruptedToolBatches)
        {
            var callContents = compact
                ? batch.Calls
                    .Select(call => (AIContent)new FunctionCallContent(call.Call.CallId, call.Call.Name))
                    .ToList()
                : batch.Calls.Select(call => (AIContent)call.Call).ToList();
            messages.Add(new ChatMessage(ChatRole.Assistant, callContents));

            var results = new List<AIContent>(batch.Calls.Count);
            foreach (var call in batch.Calls)
            {
                var result = compact
                    ? call.Dispatched && call.HasResult
                        ? "Tool activity completed; result omitted from the bounded interruption record."
                        : call.Dispatched
                            ? "Tool effect may have completed; result unknown and omitted from the bounded interruption record."
                            : "Tool call was not executed."
                    : call.HasResult
                        ? call.Result
                        : call.Dispatched
                            ? "Tool effect may have completed, but its result is unknown."
                            : "Tool call was not executed.";
                results.Add(new FunctionResultContent(call.Call.CallId, result));
            }
            messages.Add(new ChatMessage(ChatRole.Tool, results));
        }

        messages.Add(new ChatMessage(
            ChatRole.Assistant,
            wasCancelled
                ? "Turn interrupted by cancellation. Tool effects may have occurred; reconcile before retrying."
                : "Turn interrupted after a tool was dispatched. Tool effects may have occurred; reconcile before retrying."));

        return new DurableSessionResponse
        {
            CorrelationId = correlationId,
            CreatedAt = Workflow.UtcNow,
            RequestId = requestId,
            Messages = messages,
            CompletionReason = DurableTurnCompletionReason.Interrupted,
        };
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current conversation history as a list of <see cref="DurableSessionEntry"/>
    /// instances. Each turn appends a request entry followed by a response entry.
    /// </summary>
    [WorkflowQuery("GetHistory")]
    public IReadOnlyList<DurableSessionEntry> GetHistory()
    {
        if (RequiredInput.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                _history,
                RequiredInput.MaximumTransportPayloadSizeBytes,
                RequiredInput.PayloadCodecExpansionAllowanceBytes,
                "workflow query result");
        }
        return _history;
    }

    /// <summary>Returns the opaque epoch identifying this session lineage.</summary>
    [WorkflowQuery("GetSessionEpoch")]
    public string GetSessionEpoch() => CurrentSessionEpoch;

    /// <summary>Returns the cumulative completed-turn count across continue-as-new runs.</summary>
    [WorkflowQuery("GetCompletedTurnCount")]
    public int GetCompletedTurnCount() => _turnCount;

    /// <summary>Returns current Temporal event-history usage and admission thresholds.</summary>
    [WorkflowQuery("GetHistoryBudget")]
    public DurableChatHistoryBudgetStatus GetHistoryBudget()
    {
        var input = RequiredInput;
        var eventCount = Workflow.CurrentHistoryLength;
        var sizeBytes = Workflow.CurrentHistorySize;
        return new DurableChatHistoryBudgetStatus
        {
            HistoryEventCount = eventCount,
            HistorySizeBytes = sizeBytes,
            IsAdmissionRestricted = eventCount >= input.HistoryEventSoftLimit
                                   || sizeBytes >= input.HistorySizeSoftLimitBytes,
            IsReserveReached = eventCount >= input.HistoryEventReserveLimit
                               || sizeBytes >= input.HistorySizeReserveLimitBytes,
            HistoryEventSoftLimit = input.HistoryEventSoftLimit,
            HistoryEventReserveLimit = input.HistoryEventReserveLimit,
            HistorySizeSoftLimitBytes = input.HistorySizeSoftLimitBytes,
            HistorySizeReserveLimitBytes = input.HistorySizeReserveLimitBytes,
            MaxConcurrentTurnJoiners = input.MaxConcurrentTurnJoiners,
            ConcurrentTurnJoiners = _activeTurn?.JoinerCount ?? 0,
            IsHistoryBlocked = _historyRecovery.IsBlocked,
            HistoryBlockReason = _historyRecovery.BlockReason,
            HistoryRecoveryRetriesThisRun = _historyRecoveryRetriesThisRun,
            HistoryRecoveryRetriesThisIncident = _historyRecovery.RetryCountThisIncident,
            HistoryRecoveryTrimUsed = _historyRecovery.TrimUsedThisIncident,
            HistoryRecoveryLastOutcome = _historyRecovery.LastOutcome,
            RetainedHistoryEntryCount = _history.Count,
        };
    }

    // ── Updates ─────────────────────────────────────────────────────────────

    /// <summary>Validates an idempotent recovery retry before Temporal accepts it.</summary>
    [WorkflowUpdateValidator(nameof(RetryHistoryCompactionAsync))]
    public void ValidateRetryHistoryCompaction(string operationId) =>
        ValidateHistoryRecoveryOperation(operationId, isTrim: false);

    /// <summary>
    /// Runs one explicitly requested, bounded compaction retry while the session is blocked.
    /// </summary>
    [WorkflowUpdate("RetryHistoryCompaction")]
    public async Task<DurableHistoryRecoveryResult> RetryHistoryCompactionAsync(string operationId)
    {
        AcceptHistoryRecoveryOperation(operationId, isTrim: false);
        try
        {
            var projection = await ApplyConfiguredHistoryCompactionAsync(_history.ToList())
                .ConfigureAwait(true);
            CompleteHistoryRecovery(projection, "retry-succeeded");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordHistoryRecoveryFailure(exception, "retry-failed");
        }
        finally
        {
            _historyRecoveryOperationActive = false;
        }

        return BuildHistoryRecoveryResult(operationId);
    }

    /// <summary>Validates the incident's single explicitly authorized lossy trim.</summary>
    [WorkflowUpdateValidator(nameof(TrimBlockedHistoryAsync))]
    public void ValidateTrimBlockedHistory(string operationId) =>
        ValidateHistoryRecoveryOperation(operationId, isTrim: true);

    /// <summary>
    /// Removes an oldest complete-turn prefix once for the current blocked incident and records
    /// a truncation marker for the removed range.
    /// </summary>
    [WorkflowUpdate("TrimBlockedHistory")]
    public Task<DurableHistoryRecoveryResult> TrimBlockedHistoryAsync(string operationId)
    {
        AcceptHistoryRecoveryOperation(operationId, isTrim: true);
        try
        {
            var projection = CreateExplicitTrimProjection(_history);
            CompleteHistoryRecovery(projection, "trim-succeeded");
        }
        catch (Exception exception)
        {
            RecordHistoryRecoveryFailure(exception, "trim-failed");
        }
        finally
        {
            _historyRecoveryOperationActive = false;
        }

        return Task.FromResult(BuildHistoryRecoveryResult(operationId));
    }

    private void ValidateHistoryRecoveryOperation(string operationId, bool isTrim)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ApplicationFailureException(
                "A non-empty history-recovery operation ID is required.",
                errorType: "DurableHistoryRecoveryInvalidRequest",
                nonRetryable: true);
        }
        if (!_historyRecovery.IsBlocked)
        {
            throw new ApplicationFailureException(
                "History recovery is only accepted while history is blocked.",
                errorType: "DurableHistoryRecoveryNotBlocked",
                nonRetryable: true);
        }
        if (IsHistoryReserveReached)
        {
            throw new ApplicationFailureException(
                "History recovery cannot be admitted after the configured history reserve is reached.",
                errorType: "DurableHistoryRecoveryReserveReached",
                nonRetryable: true);
        }
        if (_historyRecoveryOperationActive)
        {
            throw new ApplicationFailureException(
                "Another history-recovery operation is already active.",
                errorType: "DurableHistoryRecoveryBusy",
                nonRetryable: true);
        }
        if (_historyRecovery.AcceptedOperationIds.Contains(operationId, StringComparer.Ordinal))
        {
            throw new ApplicationFailureException(
                "This history-recovery operation ID was already accepted.",
                errorType: "DurableHistoryRecoveryDuplicate",
                nonRetryable: true);
        }

        if (isTrim)
        {
            if (_historyRecovery.TrimUsedThisIncident)
            {
                throw new ApplicationFailureException(
                    "The explicitly authorized trim was already used in this blocked incident.",
                    errorType: "DurableHistoryRecoveryTrimLimitReached",
                    nonRetryable: true);
            }
        }
        else
        {
            if (RequiredInput.HistoryCompactionStrategy == DurableHistoryCompactionStrategy.None
                && RequiredInput.HistoryReducerKey is null)
            {
                throw new ApplicationFailureException(
                    "No history-compaction strategy is configured for retry.",
                    errorType: "DurableHistoryRecoveryConfigurationMissing",
                    nonRetryable: true);
            }
            if (_historyRecoveryRetriesThisRun >= MaximumHistoryRecoveryRetriesPerRun
                || _historyRecovery.RetryCountThisIncident >= MaximumHistoryRecoveryRetriesPerIncident)
            {
                throw new ApplicationFailureException(
                    "The bounded history-compaction retry limit has been reached.",
                    errorType: "DurableHistoryRecoveryRetryLimitReached",
                    nonRetryable: true);
            }
        }
    }

    private void AcceptHistoryRecoveryOperation(string operationId, bool isTrim)
    {
        ValidateHistoryRecoveryOperation(operationId, isTrim);
        _historyRecoveryOperationActive = true;
        var acceptedOperationIds = _historyRecovery.AcceptedOperationIds.Append(operationId).ToArray();
        _historyRecovery = _historyRecovery with
        {
            RetryCountThisIncident = _historyRecovery.RetryCountThisIncident + (isTrim ? 0 : 1),
            TrimUsedThisIncident = _historyRecovery.TrimUsedThisIncident || isTrim,
            AcceptedOperationIds = acceptedOperationIds,
            LastOutcome = isTrim ? "trim-started" : "retry-started",
        };
        if (Input is { } input)
        {
            Input = input with { HistoryRecovery = _historyRecovery };
        }
        if (!isTrim)
        {
            _historyRecoveryRetriesThisRun++;
        }
    }

    private void CompleteHistoryRecovery(List<DurableSessionEntry> projection, string outcome)
    {
        _preparedHistory = projection;
        _historyRecovery = new DurableHistoryRecoveryState { LastOutcome = outcome };
        if (Input is { } input)
        {
            Input = input with { HistoryRecovery = _historyRecovery };
        }
        _historyRecoveryRetriesThisRun = 0;
        _sessionAdmissionClosed = true;
        _continueAsNewPending = true;
    }

    private void RecordHistoryRecoveryFailure(Exception exception, string outcome)
    {
        var applicationFailure = FindApplicationFailure(exception);
        var reason = exception switch
        {
            Exceptions.DurableContextBudgetExceededException => "context-budget-exceeded",
            _ when applicationFailure?.ErrorType?.StartsWith("DurableHistory", StringComparison.Ordinal) == true =>
                applicationFailure.ErrorType ?? "compaction-failed",
            _ when applicationFailure?.ErrorType == "DurableTransportPayloadTooLarge" =>
                "transport-payload-too-large",
            Exceptions.DurableConfigurationException => "configuration-invalid",
            _ => "compaction-failed",
        };
        if (reason.Length > 96)
        {
            reason = reason[..96];
        }
        _historyRecovery = _historyRecovery with
        {
            IsBlocked = true,
            BlockReason = string.IsNullOrWhiteSpace(reason) ? "compaction-failed" : reason,
            LastOutcome = outcome,
        };
        if (Input is { } input)
        {
            Input = input with { HistoryRecovery = _historyRecovery };
        }
        _sessionAdmissionClosed = true;
    }

    private static ApplicationFailureException? FindApplicationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ApplicationFailureException applicationFailure)
            {
                return applicationFailure;
            }
        }

        return null;
    }

    private DurableHistoryRecoveryResult BuildHistoryRecoveryResult(string operationId) =>
        new()
        {
            OperationId = operationId,
            IsBlocked = _historyRecovery.IsBlocked,
            Outcome = _historyRecovery.LastOutcome ?? "unknown",
        };

    private bool CanContinueAsNewForHistoryRecovery =>
        !_historyRecoveryCanAttemptedThisRun
        && _historyRecoveryRetriesThisRun >= MaximumHistoryRecoveryRetriesPerRun
        && _historyRecovery.RetryCountThisIncident < MaximumHistoryRecoveryRetriesPerIncident;

    private async Task WaitForHistoryRecoveryAsync(
        DurableChatWorkflowInput input,
        DateTimeOffset originalCreatedAt)
    {
        while (_historyRecovery.IsBlocked && !_shutdownRequested)
        {
            await Workflow.WaitConditionAsync(
                () => !_historyRecovery.IsBlocked
                      || _shutdownRequested
                      || CanContinueAsNewForHistoryRecovery).ConfigureAwait(true);
            if (_shutdownRequested || !_historyRecovery.IsBlocked)
            {
                return;
            }

            _historyRecoveryCanAttemptedThisRun = true;
            if (IsHistoryAdmissionRestricted || IsHistoryReserveReached)
            {
                RecordHistoryRecoveryFailure(
                    new ApplicationFailureException(
                        "There is insufficient event-history headroom for continue-as-new.",
                        errorType: "DurableHistoryRecoveryHeadroomInsufficient",
                        nonRetryable: true),
                    "continue-as-new-unavailable");
                continue;
            }

            _sessionAdmissionClosed = true;
            await Workflow.WaitConditionAsync(
                () => Workflow.AllHandlersFinished
                      && !_isProcessing
                      && !HasPendingBackgroundTurns).ConfigureAwait(true);
            if (_shutdownRequested)
            {
                return;
            }

            try
            {
                var carriedInput = CreateContinueAsNewInput(
                    input,
                    _history.ToList(),
                    _approvalMixin.GetResolvedApprovals(),
                    originalCreatedAt,
                    SnapshotTerminalTurnOutcomes(),
                    _turnCount,
                    _sessionIdleDeadline) with { HistoryRecovery = _historyRecovery };
                if (input.EnableTransportPayloadGuard)
                {
                    Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                        Workflow.PayloadConverter,
                        carriedInput,
                        input.MaximumTransportPayloadSizeBytes,
                        input.PayloadCodecExpansionAllowanceBytes,
                        "blocked continue-as-new workflow input");
                }
                throw CreateContinueAsNewException(carriedInput);
            }
            catch (ContinueAsNewException)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordHistoryRecoveryFailure(exception, "continue-as-new-unavailable");
            }
        }
    }

    /// <summary>Validates that the requested logical turn is active and not already cancelled.</summary>
    [WorkflowUpdateValidator(nameof(CancelTurnAsync))]
    public void ValidateCancelTurn(string requestId)
    {
        ValidateCancelTurnRequest(requestId);
    }

    private void ValidateCancelTurnRequest(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw TurnCancellationFailure(
                "A non-empty request ID is required to cancel a turn.",
                "DurableTurnCancellationInvalidRequest");
        }

        if (_activeTurn is null)
        {
            throw CompletedOrInactiveTurnFailure(requestId);
        }

        if (!string.Equals(_activeTurn.RequestId, requestId, StringComparison.Ordinal))
        {
            if (_terminalTurns.ContainsKey(requestId))
            {
                throw CompletedOrInactiveTurnFailure(requestId);
            }

            throw TurnCancellationFailure(
                "The request ID does not match the active turn.",
                "DurableTurnCancellationRequestMismatch");
        }

        if (_activeTurn.IsTerminal)
        {
            throw CompletedOrInactiveTurnFailure(requestId);
        }

        if (_activeTurn.CancellationToken.IsCancellationRequested)
        {
            throw TurnCancellationFailure(
                "Cancellation has already been requested for this turn.",
                "DurableTurnCancellationAlreadyRequested");
        }
    }

    /// <summary>
    /// Requests cancellation of a specific active logical turn, including its current activity or
    /// approval wait. The workflow waits for active activity cancellation to complete before ending
    /// the turn. A caller cancellation token cancels only this update RPC wait; it does not cancel
    /// the accepted turn.
    /// </summary>
    [WorkflowUpdate("CancelTurn")]
    public Task<DurableTurnCancellationResult> CancelTurnAsync(string requestId)
    {
        // Accepted updates replay without validator-only state; handle races using workflow state.
        var record = _activeTurn;
        if (record is null
            || record.IsTerminal
            || !string.Equals(record.RequestId, requestId, StringComparison.Ordinal))
        {
            return Task.FromResult(new DurableTurnCancellationResult
            {
                RequestId = requestId,
                Status = DurableTurnCancellationStatus.AlreadyCompleted,
            });
        }

        if (!record.CancellationToken.IsCancellationRequested)
        {
            record.CancellationRequested = true;
            record.CancellationSource.Cancel();
        }
        return Task.FromResult(new DurableTurnCancellationResult
        {
            RequestId = requestId,
            Status = DurableTurnCancellationStatus.CancellationRequested,
        });
    }

    private ApplicationFailureException CompletedOrInactiveTurnFailure(string requestId) =>
        _terminalTurns.ContainsKey(requestId)
            ? TurnCancellationFailure(
                "The requested turn has already completed.",
                "DurableTurnCancellationAlreadyCompleted")
            : TurnCancellationFailure(
                "No active turn matches the request ID.",
                "DurableTurnCancellationNotActive");

    private static ApplicationFailureException TurnCancellationFailure(
        string message,
        string errorType) =>
        new(message, errorType: errorType, nonRetryable: true);

    /// <summary>
    /// Validates and requests graceful shutdown of this session.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RequestShutdownAsync))]
    public void ValidateRequestShutdown()
    {
        if (_shutdownRequested)
        {
            throw new ApplicationFailureException(
                "Shutdown has already been requested.",
                errorType: "DurableSessionShutdownAlreadyRequested",
                nonRetryable: true);
        }
    }

    [WorkflowUpdate("RequestShutdown")]
    public Task RequestShutdownAsync()
    {
        _shutdownRequested = true;
        _sessionAdmissionClosed = true;
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

        if (Input.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                request,
                Input.MaximumTransportPayloadSizeBytes,
                Input.PayloadCodecExpansionAllowanceBytes,
                "approval update input");
        }
        if (IsHistoryAdmissionRestricted && _activeTurn is null)
        {
            throw HistoryBudgetFailure();
        }
        _approvalMixin.ValidateRequestApproval(request);
    }

    [WorkflowUpdate("RequestApproval")]
    public async Task<DurableApprovalDecision> RequestApprovalAsync(DurableApprovalRequest request)
    {
        var input = await WaitForInputAsync().ConfigureAwait(true);
        ValidateRequestApproval(request);
        if (input.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                request,
                input.MaximumTransportPayloadSizeBytes,
                input.PayloadCodecExpansionAllowanceBytes,
                "approval update input");
        }
        var decision = await _approvalMixin.RequestApprovalAsync(
            request,
            approvalTimeout: input.ApprovalTimeout,
            onRequested: req => Workflow.Logger.LogInformation(
                "[{ConversationId}] Approval requested (RequestId: {RequestId}, Description: {Description})",
                Workflow.Info.WorkflowId, req.RequestId, req.Description ?? req.RequestId),
            onResolved: d => Workflow.Logger.LogInformation(
                "[{ConversationId}] Approval resolved (RequestId: {RequestId}, Approved: {Approved})",
                Workflow.Info.WorkflowId, d.RequestId, d.Approved),
            historyBudgetReserveReached: () => IsHistoryReserveReached,
            cancellationToken: CurrentTurnCancellationToken,
            turnCancellationRequested: IsCurrentTurnCancellationRequested);
        RecordApprovalOutcome(decision);

        if (input.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                decision,
                input.MaximumTransportPayloadSizeBytes,
                input.PayloadCodecExpansionAllowanceBytes,
                "workflow update result");
        }
        OnApprovalRequestResolved(decision);
        return decision;
    }

    /// <summary>
    /// Resolves a pending approval request and returns a retry-safe result.
    /// </summary>
    [WorkflowUpdateValidator(nameof(ResolveApprovalAsync))]
    public void ValidateResolveApproval(DurableApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (Input is null)
        {
            return;
        }

        if (Input.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                decision,
                Input.MaximumTransportPayloadSizeBytes,
                Input.PayloadCodecExpansionAllowanceBytes,
                "approval-resolution update input");
        }
        try
        {
            _approvalMixin.ValidateResolveApproval(decision);
        }
        catch (ApplicationFailureException)
        {
            RecordWorkflowMetric(
                DurableChatTelemetry.ApprovalResolutionsMetricName,
                "outcome",
                "rejected");
            throw;
        }
    }

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
        var input = RequiredInput;
        ValidateResolveApproval(decision);
        if (input.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                decision,
                input.MaximumTransportPayloadSizeBytes,
                input.PayloadCodecExpansionAllowanceBytes,
                "approval-resolution update input");
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
            RecordWorkflowMetric(
                DurableChatTelemetry.ApprovalResolutionsMetricName,
                "outcome",
                "accepted");
            OnApprovalResolutionAccepted(decision);
            RenewSessionIdleDeadline();
        }

        if (RequiredInput.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                result,
                RequiredInput.MaximumTransportPayloadSizeBytes,
                RequiredInput.PayloadCodecExpansionAllowanceBytes,
                "workflow update result");
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
    public DurableApprovalRequest? GetPendingApproval()
    {
        var request = _approvalMixin.GetPendingApproval();
        if (RequiredInput.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                request,
                RequiredInput.MaximumTransportPayloadSizeBytes,
                RequiredInput.PayloadCodecExpansionAllowanceBytes,
                "workflow query result");
        }
        return request;
    }

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
        if (RequiredInput.EnableTransportPayloadGuard)
        {
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                request,
                RequiredInput.MaximumTransportPayloadSizeBytes,
                RequiredInput.PayloadCodecExpansionAllowanceBytes,
                "approval request");
        }
        var decision = await _approvalMixin.RequestApprovalAsync(
            request,
            approvalTimeout,
            onRequested,
            onResolved,
            historyBudgetReserveReached: () => IsHistoryReserveReached,
            cancellationToken: CurrentTurnCancellationToken,
            turnCancellationRequested: IsCurrentTurnCancellationRequested);

        RecordApprovalOutcome(decision);
        OnApprovalRequestResolved(decision);
        return decision;
    }
}
