using System.Text.Json;
using TemporalCommunity.Extensions.Agents.Internal;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.Agents.Exceptions;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Long-lived Temporal workflow that acts as the durable backing store for an agent session.
/// Drives the durable-agent dispatch loop: each LLM call is a separate <c>RunDurableAgentStep</c>
/// activity, and each tool call is a separate <c>InvokeAgentTool</c> activity dispatched
/// serially in the order returned by the model.
/// </summary>
[Workflow("TemporalCommunity.Extensions.Agents.AgentWorkflow")]
internal class AgentWorkflow :
    DurableChatWorkflowBase<AgentResponse>,
    TemporalCommunity.Extensions.AI.Internal.IDurableTurnRollbackParticipant
{
    internal static readonly SearchAttributeKey<string> AgentNameSearchAttribute =
        SearchAttributeKey.CreateKeyword("AgentName");

    /// <summary>
    /// Default StateBag size threshold (in bytes) for the continue-as-new warning.
    /// Warns when the serialized <c>CarriedStateBag</c> exceeds 64 KB. Warning only —
    /// no hard failure — so sessions keep running even when StateBag grows large.
    /// This constant is <c>internal</c> so tests can reference it without magic numbers.
    /// </summary>
    internal const int StateBagSizeWarnThresholdBytes = 64 * 1024;

    // MAF-specific input (typed view of the base's Input). Set in RunAsync.
    private AgentWorkflowInput? _input;

    // GAP 6: StateBag persisted across turns so AIContextProvider state survives replay.
    private JsonElement? _currentStateBag;
    private long _stateBagRevision;
    private long _stateBagRevisionAtTurnStart;
    private TemporalMemoryScopeSnapshot? _memoryScope;
    private bool _memoryInitializationChecked;

    private int _pendingFireAndForgetTurns;

    protected override bool HasPendingBackgroundTurns => _pendingFireAndForgetTurns > 0;

    [WorkflowRun]
    public async Task RunAsync(AgentWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        InitializeInput(input);
        input = (AgentWorkflowInput)RequiredInput;
        _input = input;
        _currentStateBag = input.CarriedStateBag ?? StateBagMerge.EmptyStateBag;
        _stateBagRevision = input.CarriedStateBagRevision;
        _memoryScope = input.CarriedMemoryScope;
        _memoryInitializationChecked = input.MemoryInitializationChecked;

        Workflow.Logger.LogWorkflowStarted(input.AgentName, Workflow.Info.WorkflowId, input.TimeToLive);
        if (input.InitialFireAndForgetRequest is { } initialRequest)
        {
            ValidateFireAndForgetRequest(initialRequest);
            QueueFireAndForget(initialRequest);
        }

        await base.RunAsync(input).ConfigureAwait(true);
    }

    private AgentSessionRequest CreateAgentRequestEntry(RunRequest request, string? requestId = null)
    {
        if (request.RequestId is not null && string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new ArgumentException("Request ID cannot be empty or whitespace.");
        }

        var requestEntry = AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow);
        requestEntry.RequestId ??= requestId ?? Workflow.NewGuid().ToString("N");
        requestEntry.InputFingerprint =
            TemporalCommunity.Extensions.AI.Internal.DurableRequestFingerprint.Create(
                new
                {
                    Version = 1,
                    requestEntry.Messages,
                    DefaultChatClientKey = RequiredInput.DefaultChatClientKey,
                    Authority = TemporalCommunity.Extensions.AI.Internal.DurableRequestFingerprint
                        .CreateAuthority(RequiredInput),
                    requestEntry.EnableToolCalls,
                    requestEntry.EnableToolNames,
                    requestEntry.ResponseType,
                    requestEntry.ResponseSchema,
                });
        return requestEntry;
    }

    private static string CreateAgentAdmissionFingerprint(RunRequest request)
    {
        var requestEntry = AgentSessionRequest.FromRunRequest(request, DateTimeOffset.UnixEpoch);
        return TemporalCommunity.Extensions.AI.Internal.DurableRequestFingerprint.Create(
            new
            {
                Version = 1,
                requestEntry.Messages,
                requestEntry.EnableToolCalls,
                requestEntry.EnableToolNames,
                requestEntry.ResponseType,
                requestEntry.ResponseSchema,
            });
    }

    /// <summary>
    /// Validates that a <see cref="RunAgentAsync"/> request is well-formed before it enters history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RunAgentAsync))]
    public void ValidateRunAgent(RunRequest request)
    {
        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (IsContinueAsNewPending)
            throw new InvalidOperationException("Session is continuing as new. Retry the turn.");
        if (request?.Messages is null || request.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
        if (request.RequestId is not null && string.IsNullOrWhiteSpace(request.RequestId))
            throw new ArgumentException("Request ID cannot be empty or whitespace.");
        if (request.RequestId is { Length: > MaximumRequestIdLength })
        {
            throw new ApplicationFailureException(
                $"Request IDs cannot exceed {MaximumRequestIdLength} characters.",
                errorType: "DurableTurnInvalidRequest",
                nonRetryable: true);
        }
        ValidateTurnAdmission(
            request.RequestId,
            CreateAgentAdmissionFingerprint(request));
    }

    /// <summary>
    /// Runs the agent with the given request and returns the response.
    /// Updates are serialized — only one runs at a time.
    /// </summary>
    [WorkflowUpdate("Run")]
    public async Task<AgentResponse> RunAgentAsync(RunRequest request)
    {
        var requestId = request.RequestId ?? Workflow.NewGuid().ToString("N");
        var admissionFingerprint = CreateAgentAdmissionFingerprint(request);
        var admission = BeginTurnAdmission(requestId, admissionFingerprint);
        try
        {
            await Workflow.WaitConditionAsync(
                () => _input is not null,
                cancellationToken: CurrentTurnCancellationToken).ConfigureAwait(true);
            admission = RefreshTurnAdmission(admission, requestId, admissionFingerprint);
            ValidateInitializedTurnAdmission(admission);

            var requestEntry = CreateAgentRequestEntry(request, requestId);

            var (output, _) = await RunTurnAsync(requestEntry, admission, chatOptions: null);

            Workflow.Logger.LogWorkflowUpdateCompleted(
                _input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId ?? string.Empty);
            return output;
        }
        catch (Exception exception)
        {
            FailTurnAdmission(admission, exception);
            throw;
        }
    }

    /// <summary>
    /// Validates a queued fire-and-forget run before it enters workflow history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RunAgentFireAndForgetAsync))]
    public void ValidateRunAgentFireAndForget(RunRequest request)
    {
        ValidateFireAndForgetRequest(request);
        ValidateTurnAdmission(
            request.RequestId,
            CreateAgentAdmissionFingerprint(request));
    }

    private void ValidateFireAndForgetRequest(RunRequest request)
    {
        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (IsContinueAsNewPending)
            throw new InvalidOperationException("Session is continuing as new. Retry the turn.");
        if (request?.Messages is null || request.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
        if (request.RequestId is not null && string.IsNullOrWhiteSpace(request.RequestId))
            throw new ArgumentException("Request ID cannot be empty or whitespace.");
    }

    /// <summary>
    /// Queues a fire-and-forget run. The workflow does not wait for this to complete.
    /// </summary>
    [WorkflowUpdate("RunFireAndForget")]
    public Task RunAgentFireAndForgetAsync(RunRequest request)
    {
        QueueFireAndForget(request);
        return Task.CompletedTask;
    }

    private void QueueFireAndForget(RunRequest request)
    {
        _pendingFireAndForgetTurns++;
        _ = ProcessFireAndForgetAsync(request);
    }

    // ── Hooks supplied to the base class ────────────────────────────────────

    protected override long? GetCommittedTurnRevision(AgentResponse? output) => _stateBagRevision;

    /// <inheritdoc/>
    protected override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        string? requestId,
        AgentResponse output,
        DateTimeOffset createdAt)
    {
        _stateBagRevision = checked(_stateBagRevision + 1);
        TemporalAgentResponseMetadata.SetStateBag(output, _currentStateBag, _stateBagRevision);
        return AgentSessionResponse.FromAgentResponse(correlationId, output, createdAt, requestId);
    }

    /// <inheritdoc/>
    protected override Task<List<DurableSessionEntry>> ApplyKeyedHistoryReducerAsync(
        string reducerKey,
        List<DurableSessionEntry> history,
        ActivityOptions activityOptions) =>
        Workflow.ExecuteActivityAsync(
            (AgentActivities a) => a.ReduceHistoryByKeyAsync(
                new TemporalCommunity.Extensions.AI.ReduceHistoryByKeyInput
                {
                    ReducerKey = reducerKey,
                    History = history,
                }),
            activityOptions);

    /// <inheritdoc/>
    protected override Task<AgentResponse> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        // This path builds its step options from the agent's frozen workflow input so per-agent
        // settings are applied consistently across every step in the durable agent loop.
        var agentRequestEntry = (AgentSessionRequest)requestEntry;
        var runRequest = ToRunRequest(agentRequestEntry);

        return ExecuteAgentTurnAsync(runRequest, activityOptions);
    }

    /// <inheritdoc/>
    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(_input);

        // `with` copies all properties from _input and overrides the fields that differ
        // for the new run: the base class CAN fields (carried from the base input parameter)
        // plus the StateBag snapshot. RetryPolicy and ResolvedWorkerConfig carry forward
        // unchanged from _input.
        var carriedInput = _input with
        {
            CarriedStateBag = _currentStateBag,
            CarriedStateBagRevision = _stateBagRevision,
            CarriedMemoryScope = _memoryScope,
            MemoryInitializationChecked = _memoryInitializationChecked,
            InitialFireAndForgetRequest = null,
            // Base class CAN fields — sourced from the base DurableChatWorkflowInput arg.
            TimeToLive = input.TimeToLive,
            SessionIdleDeadline = input.SessionIdleDeadline,
            CarriedHistory = input.CarriedHistory,
            ApprovalTimeout = input.ApprovalTimeout,
            EnableSearchAttributes = input.EnableSearchAttributes,
            MaxEntryCount = input.MaxEntryCount,
            HistoryReducerKey = input.HistoryReducerKey,
            HistoryCompactionStrategy = input.HistoryCompactionStrategy,
            HistoryRecovery = input.HistoryRecovery,
            OriginalCreatedAt = input.OriginalCreatedAt,
            SessionEpoch = input.SessionEpoch,
            CompletedTurnCount = input.CompletedTurnCount,
            CarriedTerminalOutcomes = input.CarriedTerminalOutcomes,
            MaxTerminalOutcomeCount = input.MaxTerminalOutcomeCount,
            MaxTerminalOutcomeBytes = input.MaxTerminalOutcomeBytes,
            ActivityTimeout = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
        };

        // StateBag size guard (Feature D): emit a warning when the serialized StateBag
        // exceeds the internal warning threshold (64 KB). The warning only — no hard
        // failure — so sessions keep running even when StateBag grows large.
        if (_currentStateBag.HasValue)
        {
            var byteCount = AgentSessionStateBagExtensions.GetDurableSerializedUtf8ByteCount(
                _currentStateBag.Value);
            if (byteCount > StateBagSizeWarnThresholdBytes)
            {
                Workflow.Logger.LogWarning(
                    "[{SessionId}] CarriedStateBag is {Bytes:N0} bytes at continue-as-new time " +
                    "(threshold: {Threshold:N0} bytes). Consider reducing AIContextProvider state " +
                    "to avoid bloating the CAN payload.",
                    Workflow.Info.WorkflowId, byteCount, StateBagSizeWarnThresholdBytes);
            }
        }

        Workflow.Logger.LogWorkflowContinueAsNew(
            _input.AgentName, Workflow.Info.WorkflowId,
            input.CarriedHistory?.Count ?? 0);

        return Workflow.CreateContinueAsNewException(
            (AgentWorkflow wf) => wf.RunAsync(carriedInput));
    }

    protected override JsonElement SerializeTurnOutput(AgentResponse output) =>
        JsonSerializer.SerializeToElement(output, TemporalAgentJsonUtilities.DefaultOptions);

    protected override AgentResponse DeserializeTurnOutput(JsonElement output) =>
        output.Deserialize<AgentResponse>(TemporalAgentJsonUtilities.DefaultOptions)
        ?? throw new InvalidOperationException("The retained agent outcome has no response.");

    protected override JsonElement SerializeTurnResponseEntry(DurableSessionResponse responseEntry) =>
        JsonSerializer.SerializeToElement<DurableSessionEntry>(
            responseEntry, TemporalAgentJsonUtilities.DefaultOptions);

    protected override DurableSessionResponse DeserializeTurnResponseEntry(JsonElement responseEntry) =>
        responseEntry.Deserialize<DurableSessionEntry>(TemporalAgentJsonUtilities.DefaultOptions)
            as DurableSessionResponse
        ?? throw new InvalidOperationException("The retained agent outcome has no response entry.");

    protected override JsonSerializerOptions TerminalOutcomeJsonSerializerOptions =>
        TemporalAgentJsonUtilities.DefaultOptions;

    internal override Exception ReportTurnFailure(
        Exception exception,
        TemporalCommunity.Extensions.AI.Internal.DurableTurnFailureDetails details) =>
        new ApplicationFailureException(
            JsonSerializer.Serialize(
                details,
                TemporalCommunity.Extensions.AI.DurableAIJsonUtilities.DefaultOptions.GetTypeInfo(
                    typeof(TemporalCommunity.Extensions.AI.Internal.DurableTurnFailureDetails))),
            exception,
            errorType: TemporalCommunity.Extensions.AI.Internal.DurableTurnFailureMapper.TurnFailedErrorType,
            nonRetryable: true);

    /// <inheritdoc/>
    protected override void UpsertCustomSearchAttributes()
    {
        if (_input is not null)
        {
            Workflow.UpsertTypedSearchAttributes(
                AgentNameSearchAttribute.ValueSet(_input.AgentName));
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reconstructs the original <see cref="RunRequest"/> from a stored
    /// <see cref="AgentSessionRequest"/>.
    /// </summary>
    private static RunRequest ToRunRequest(AgentSessionRequest entry)
    {
        ChatResponseFormat? responseFormat = null;
        if (string.Equals(entry.ResponseType, "json", StringComparison.OrdinalIgnoreCase))
        {
            responseFormat = entry.ResponseSchema is { } schema
                ? ChatResponseFormat.ForJsonSchema(schema)
                : ChatResponseFormat.Json;
        }

        return new RunRequest(
            entry.Messages.ToList(),
            responseFormat: responseFormat,
            enableToolCalls: entry.EnableToolCalls,
            enableToolNames: entry.EnableToolNames?.ToList())
        {
            CorrelationId = entry.CorrelationId,
            OrchestrationId = entry.OrchestrationId,
        };
    }

    private async Task<AgentResponse> ExecuteAgentTurnAsync(
        RunRequest runRequest,
        ActivityOptions turnActivityOptions)
    {
        var stepActivityOptions = ApplyCurrentTurnCancellation(new ActivityOptions
        {
            StartToCloseTimeout = _input!.ActivityTimeout,
            HeartbeatTimeout = _input!.HeartbeatTimeout,
            Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
            RetryPolicy = TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.ResolveForModel(_input!.RetryPolicy),
            CancellationToken = turnActivityOptions.CancellationToken,
            CancellationType = turnActivityOptions.CancellationType,
        });

        return await ExecuteDurableAgentTurnAsync(runRequest, stepActivityOptions).ConfigureAwait(true);
    }

    /// <summary>
    /// Durable-agent dispatch loop. Drives the alternation between
    /// <c>RunDurableAgentStepAsync</c> (one LLM call) and <c>InvokeAgentToolAsync</c> per tool
    /// call. Tool calls within a single LLM response run serially in model order; the loop
    /// terminates when the LLM returns a final assistant message or when
    /// <see cref="DurableAgentBuilder.MaxToolCallsPerTurn"/> iterations are exceeded.
    /// </summary>
    private async Task<AgentResponse> ExecuteDurableAgentTurnAsync(
        RunRequest runRequest,
        ActivityOptions stepActivityOptions)
    {
        Workflow.Logger.LogDurableAgentTurnStarted(_input!.AgentName, Workflow.Info.WorkflowId);

        var initialMessages = FlattenHistoryMessages();
        var accumulated = initialMessages.ToList();
        var allTurnMessages = new List<ChatMessage>();
        UsageDetails? totalUsage = null;
        IReadOnlyList<PreparedMessageAttribution>? preparedMessageAttributions = null;
        var preparedTools = new Dictionary<string, PreparedAgentTool>(
            StringComparer.OrdinalIgnoreCase);

        // Note: do NOT snapshot _input.MaxToolCallsPerTurn here. The resolution handshake
        // mutates _input mid-loop on proxy-started sessions, so we must re-read it each iteration
        // (and again after the loop for the aborted-response log/message).
        try
        {
            if (!_memoryInitializationChecked)
            {
                var memoryInitInput = new InitializeChatHistoryMemoryInput
                {
                    AgentName = _input!.AgentName,
                    SessionId = Workflow.Info.WorkflowId!,
                    SerializedStateBag = GetStateBagForDispatch(),
                    MaximumTransportPayloadSizeBytes = _input.MaximumTransportPayloadSizeBytes,
                    PayloadCodecExpansionAllowanceBytes = _input.PayloadCodecExpansionAllowanceBytes,
                };
                var initializerOptions = ApplyCurrentTurnCancellation(new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromSeconds(30),
                    RetryPolicy = new RetryPolicy
                    {
                        InitialInterval = TimeSpan.FromSeconds(1),
                        MaximumAttempts = 2,
                    },
                    Summary = AgentActivities.BuildActivitySummary(_input.AgentName),
                });
                var initializedMemory = await Workflow.ExecuteActivityAsync(
                    (AgentActivities activities) => activities.InitializeChatHistoryMemoryAsync(memoryInitInput),
                    initializerOptions).ConfigureAwait(true);
                _currentStateBag = StateBagMerge.ApplyTrustedDelta(
                    _currentStateBag,
                    initializedMemory.StateBagDelta);
                _memoryScope = initializedMemory.Scope;
                _memoryInitializationChecked = true;
            }

            var preparationInput = new PrepareAgentTurnInput
            {
                AgentName = _input.AgentName,
                SessionId = Workflow.Info.WorkflowId!,
                Messages = initialMessages,
                SerializedStateBag = GetStateBagForDispatch(),
                MemoryScope = _memoryScope,
                MaximumTransportPayloadSizeBytes = _input.MaximumTransportPayloadSizeBytes,
                PayloadCodecExpansionAllowanceBytes = _input.PayloadCodecExpansionAllowanceBytes,
            };
            TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                preparationInput,
                _input.MaximumTransportPayloadSizeBytes,
                _input.PayloadCodecExpansionAllowanceBytes,
                "agent-turn context preparation input");
            var preparedContext = await Workflow.ExecuteActivityAsync(
                (AgentActivities activities) => activities.PrepareAgentTurnAsync(preparationInput),
                stepActivityOptions).ConfigureAwait(true);
            _currentStateBag = StateBagMerge.ApplyTrustedDelta(_currentStateBag, preparedContext.StateBagDelta);
            accumulated = preparedContext.Messages.ToList();
            preparedMessageAttributions = preparedContext.MessageAttributions;
            preparedTools = preparedContext.Tools.ToDictionary(
                tool => tool.Name,
                StringComparer.OrdinalIgnoreCase);

            for (var iteration = 0; iteration < _input!.MaxToolCallsPerTurn; iteration++)
            {
                ThrowIfCurrentTurnCancellationRequested();

                // Proxy-started sessions have WorkerSettingsResolved=false.
                // On the first step of the first turn, ask the activity to resolve worker-side
                // settings (per-tool activity options) and return them.
                var needsResolution = iteration == 0 && !_input!.WorkerSettingsResolved;

                // The workflow is authoritative; every activity receives an explicit snapshot,
                // including when no StateBag key changed since the preceding dispatch.
                var bagForStep = GetStateBagForDispatch();

                var stepInput = new AgentStepInput
                {
                    AgentName = _input!.AgentName,
                    Request = runRequest,
                    AccumulatedMessages = accumulated,
                    AccumulatedMessageAttributions = preparedMessageAttributions,
                    PreparedInstructions = preparedContext.Instructions,
                    ProvidersPrepared = true,
                    PreparedTools = preparedContext.Tools,
                    CurrentTurnStartIndex = CurrentTurnStartIndex,
                    PreviousTurnRanges = PreviousTurnRanges,
                    MaxModelContextTokens = _input.MaxModelContextTokens,
                    ModelOutputTokenReserve = _input.ModelOutputTokenReserve,
                    SerializedStateBag = bagForStep,
                    SessionId = TemporalAgentSessionId.Parse(Workflow.Info.WorkflowId!),
                    MemoryScope = _memoryScope,
                    NeedsWorkerSettingsResolution = needsResolution,
                    MaximumTransportPayloadSizeBytes = _input.MaximumTransportPayloadSizeBytes,
                    PayloadCodecExpansionAllowanceBytes = _input.PayloadCodecExpansionAllowanceBytes,
                };

                TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                    Workflow.PayloadConverter,
                    stepInput,
                    _input.MaximumTransportPayloadSizeBytes,
                    _input.PayloadCodecExpansionAllowanceBytes,
                    "model-step activity input");
                var stepResult = await Workflow.ExecuteActivityAsync(
                    (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
                    stepActivityOptions).ConfigureAwait(true);
                ThrowIfCurrentTurnCancellationRequested();

            // Apply resolved worker-side settings once and carry the complete bundle forward
            // through continue-as-new.
            if (needsResolution && stepResult.ResolvedWorkerConfig is not null)
            {
                _input = _input! with
                {
                    CarriedStateBag = _currentStateBag,
                    ResolvedWorkerConfig = stepResult.ResolvedWorkerConfig,
                };
            }

            if (stepResult.ApprovalRequiredToolNames is { Count: > 0 } markedTools)
            {
                var resolvedConfig = _input!.ResolvedWorkerConfig
                    ?? throw new ApplicationFailureException(
                        "Worker tool approval metadata was returned without resolved worker settings.",
                        errorType: "DurableConfigurationException",
                        nonRetryable: true);
                var combinedApprovalTools = (resolvedConfig.RequiresApprovalTools ?? [])
                    .Concat(markedTools)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                _input = _input with
                {
                    ResolvedWorkerConfig = resolvedConfig with
                    {
                        RequiresApprovalTools = combinedApprovalTools,
                    },
                };
            }

            _currentStateBag = StateBagMerge.ApplyTrustedDelta(_currentStateBag, stepResult.StateBagDelta);

            if (stepResult.Usage is not null)
            {
                totalUsage ??= new UsageDetails();
                totalUsage.InputTokenCount = (totalUsage.InputTokenCount ?? 0) + (stepResult.Usage.InputTokenCount ?? 0);
                totalUsage.OutputTokenCount = (totalUsage.OutputTokenCount ?? 0) + (stepResult.Usage.OutputTokenCount ?? 0);
                totalUsage.TotalTokenCount = (totalUsage.TotalTokenCount ?? 0) + (stepResult.Usage.TotalTokenCount ?? 0);
            }

            accumulated.AddRange(stepResult.AssistantMessages);
            allTurnMessages.AddRange(stepResult.AssistantMessages);

            if (stepResult.IsFinal || stepResult.ToolCalls is null || stepResult.ToolCalls.Count == 0)
            {
                Workflow.Logger.LogDurableAgentTurnCompleted(_input!.AgentName, iteration + 1);
                var finalResponse = new AgentResponse
                {
                    Messages = allTurnMessages,
                    Usage = totalUsage,
                    CreatedAt = Workflow.UtcNow,
                };

                await CompleteContextProviderTurnAsync(
                    initialMessages,
                    finalResponse.Messages.ToList(),
                    invokeException: null).ConfigureAwait(true);
                return finalResponse;
            }

            var toolCalls = stepResult.ToolCalls;
            foreach (var call in toolCalls)
            {
                if (!preparedTools.ContainsKey(call.Name))
                {
                    throw new ApplicationFailureException(
                        $"The model returned tool '{call.Name}' outside the frozen provider-selected tool set; " +
                        "no tool was dispatched.",
                        errorType: nameof(DurableConfigurationException),
                        nonRetryable: true);
                }
            }

            foreach (var call in toolCalls)
            {
                if (call.CallId is { Length: > 256 })
                {
                    throw new ApplicationFailureException(
                        "Tool call IDs cannot exceed 256 characters; no tool was dispatched.",
                        errorType: "DurableToolCallIdTooLong",
                        nonRetryable: true);
                }
            }

            var interruptedBatchIndex = BeginInterruptedToolBatch(toolCalls);

            var registeredToolNames = _input!.DurableAgentToolActivityOptions?.Keys.ToArray()
                ?? [];
            var enabledToolNames = runRequest.EnableToolNames is { } requestedNames
                ? requestedNames.ToArray()
                : null;
            var enabledToolCalls = new bool[toolCalls.Count];
            for (var i = 0; i < toolCalls.Count; i++)
            {
                enabledToolCalls[i] = AgentRunToolSelectionPolicy.IsCallEnabled(
                    toolCalls[i].Name,
                    registeredToolNames,
                    runRequest.EnableToolCalls,
                    enabledToolNames);
                if (!enabledToolCalls[i])
                {
                    Workflow.Logger.LogRunToolCallBlocked(
                        _input.AgentName,
                        Workflow.Info.WorkflowId,
                        runRequest.CorrelationId ?? string.Empty,
                        iteration + 1,
                        toolCalls[i].Name);
                }
            }

            Workflow.Logger.LogDurableAgentTurnIteration(_input!.AgentName, iteration + 1, toolCalls.Count);

            // ── Feature L — Phase 1: Fan out interceptor activities in parallel ────────────
            // Build interceptor results for all tool calls. Tools opted out (SkipInterceptor)
            // or when no interceptor is configured get a synthetic Proceed.
            DurableToolInterceptorResult[]? interceptorResults = null;
            var interceptorOpts = _input!.InterceptorActivityOptions;
            var interceptorToolOpts = _input!.InterceptorToolActivityOptions;
            var skippedTools = _input!.InterceptorSkippedTools;

            if (interceptorOpts is not null)
            {
                var interceptorTasks = new List<Task<DurableToolInterceptorResult>>(toolCalls.Count);
                var bagForInterceptors = GetStateBagForDispatch();
                for (var i = 0; i < toolCalls.Count; i++)
                {
                    var tc = toolCalls[i];
                    if (!enabledToolCalls[i])
                    {
                        interceptorTasks.Add(Task.FromResult(
                            new DurableToolInterceptorResult { Outcome = DurableToolOutcome.Proceed }));
                        continue;
                    }

                    if (DurableToolDecisionPolicy.IsToolSkipped(tc.Name, skippedTools))
                    {
                        interceptorTasks.Add(Task.FromResult(
                            new DurableToolInterceptorResult { Outcome = DurableToolOutcome.Proceed }));
                    }
                    else
                    {
                        var interceptorInput = new DurableToolInterceptorInput
                        {
                            AgentName = _input!.AgentName,
                            ToolName = tc.Name,
                            Arguments = tc.Arguments is null
                                ? null
                                : new Dictionary<string, object?>(tc.Arguments),
                            CallId = tc.CallId,
                            SerializedStateBag = bagForInterceptors,
                            RequiresApproval = _input!.RequiresApprovalTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true,
                        };

                        // See also: DurableChatWorkflow.ExecutePattern3TurnAsync (MEAI path) — parallel interceptor dispatch
                        interceptorTasks.Add(Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.RunToolInterceptorAsync(interceptorInput),
                            ApplyCurrentTurnCancellation(
                                DurableToolDecisionPolicy.ResolveInterceptorActivityOptions(
                                    tc.Name, interceptorOpts, interceptorToolOpts))));
                    }
                }

                interceptorResults = await Workflow.WhenAllAsync(interceptorTasks).ConfigureAwait(true);

                // X-2: merge any StateBag mutations the interceptors made back into the carried
                // bag, BEFORE tool dispatch, so a tool sees interceptor-driven state changes.
                // Interceptors fan out concurrently; merge in tool-call index order (later index
                // wins) for replay determinism — never by completion order.
                var preInterceptorStateBag = _currentStateBag;
                MergeStateBagWriteBacks(
                    [.. interceptorResults.Select(result =>
                        result?.UpdatedStateBag is { } updated
                            ? StateBagMerge.CreateDelta(preInterceptorStateBag, updated)
                            : null)]);
            }

            // ── Feature L — Phase 2 & Feature A: Process decisions, park for approvals ────
            //
            // Safety invariant (BLOCK-4): NO tool activity is dispatched until ALL approval
            // waits in this turn are fully resolved. Proceed-outcome tool inputs are buffered
            // in pendingToolDispatches and dispatched in Phase 2.5, after the approval loop
            // completes. No tool effect runs while a workflow-parked approval is outstanding.
            //
            var toolResults = new InvokeAgentToolResult?[toolCalls.Count];
            var syntheticResults = new string?[toolCalls.Count]; // null = real tool result

            // Buffered dispatches: populated during Phase 2, dispatched in Phase 2.5.
            var pendingToolDispatches = new List<(int Index, InvokeAgentToolInput Input, ActivityOptions Options)>(toolCalls.Count);

            var requiresApprovalTools = _input!.RequiresApprovalTools;

            // Every consumer receives a complete workflow-owned snapshot. This also ensures
            // serial tool calls see state committed by the preceding call.
            var bagForProceedTools = GetStateBagForDispatch();

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                if (!enabledToolCalls[i])
                {
                    syntheticResults[i] = AgentRunToolSelectionPolicy.CreateBlockedResult(tc.Name);
                    continue;
                }

                var interceptorResult = interceptorResults?[i];

                // Determine effective outcome (Rule 2: RequireApproval floor, Block never overridden).
                var outcome = DurableToolDecisionPolicy.GetEffectiveOutcome(
                    interceptorResult?.Outcome, tc.Name, requiresApprovalTools);

                switch (outcome)
                {
                    case DurableToolOutcome.Proceed:
                        // Buffer for dispatch after all approval waits resolve (BLOCK-4).
                        pendingToolDispatches.Add((i, new InvokeAgentToolInput
                        {
                            AgentName = _input!.AgentName,
                            SessionId = Workflow.Info.WorkflowId,
                            MemoryScope = preparedTools[tc.Name].MemoryScope,
                            ToolName = tc.Name,
                            Arguments = DurableToolDecisionPolicy.GetEffectiveArguments(interceptorResult?.ModifiedArguments, (IReadOnlyDictionary<string, object?>?)tc.Arguments),
                            CallId = tc.CallId,
                            PreparedTool = preparedTools[tc.Name],
                            MaximumTransportPayloadSizeBytes = _input.MaximumTransportPayloadSizeBytes,
                            PayloadCodecExpansionAllowanceBytes = _input.PayloadCodecExpansionAllowanceBytes,
                            // Refreshed at dispatch time after prior serial tool results are applied.
                            SerializedStateBag = bagForProceedTools,
                        }, ResolveDurableToolActivityOptions(tc.Name)));
                        break;

                    case DurableToolOutcome.PauseForApproval:
                        var approvalArguments = DurableToolDecisionPolicy.GetEffectiveArguments(
                            interceptorResult?.ModifiedArguments,
                            (IReadOnlyDictionary<string, object?>?)tc.Arguments);
                        var approvedCallFingerprint = DurableToolMiddleware.Fingerprint(
                            tc.Name, tc.CallId, approvalArguments);
                        // Feature A: park the turn loop; wait for a human decision via
                        // the DurableApprovalMixin (compute-free durable wait).
                        var approvalRequest = new DurableApprovalRequest
                        {
                            RequestId = $"{tc.CallId ?? tc.Name}-{Workflow.NewGuid():N}",
                            FunctionName = tc.Name,
                            CallId = tc.CallId,
                            Description = DurableToolDecisionPolicy.GetApprovalDescription(interceptorResult, tc.Name),
                            // Metadata is deliberately interceptor-authored. Do not expose raw
                            // model function arguments to a reviewer unless an interceptor has
                            // first reduced them to explicit, safe review data.
                            ReviewData = interceptorResult?.Metadata,
                        };

                        // Sequential: the mixin enforces one pending approval at a time.
                        // Feature A: we call the protected turn-loop path, which parks this
                        // workflow fiber (compute-free) without blocking inside an activity.
                        var decision = await RequestApprovalFromTurnLoopAsync(
                            approvalRequest,
                            _input!.ApprovalTimeout,
                            onRequested: req => Workflow.Logger.LogInformation(
                                "[{SessionId}] Approval requested for tool '{ToolName}' (RequestId: {RequestId})",
                                Workflow.Info.WorkflowId, req.FunctionName, req.RequestId),
                            onResolved: dec => Workflow.Logger.LogInformation(
                                "[{SessionId}] Approval resolved for tool '{ToolName}' (RequestId: {RequestId}, Approved: {Approved})",
                                Workflow.Info.WorkflowId, approvalRequest.FunctionName, dec.RequestId, dec.Approved))
                            .ConfigureAwait(true);

                        if (decision.Approved)
                        {
                            // Buffer the approved tool for dispatch in Phase 2.5.
                            pendingToolDispatches.Add((i, new InvokeAgentToolInput
                            {
                                AgentName = _input!.AgentName,
                                SessionId = Workflow.Info.WorkflowId,
                                MemoryScope = preparedTools[tc.Name].MemoryScope,
                                ToolName = tc.Name,
                                Arguments = approvalArguments,
                                CallId = tc.CallId,
                                PreparedTool = preparedTools[tc.Name],
                                ApprovalAccepted = true,
                                ApprovedCallFingerprint = approvedCallFingerprint,
                                MaximumTransportPayloadSizeBytes = _input.MaximumTransportPayloadSizeBytes,
                                PayloadCodecExpansionAllowanceBytes = _input.PayloadCodecExpansionAllowanceBytes,
                                SerializedStateBag = GetStateBagForDispatch(),
                            }, ResolveDurableToolActivityOptions(tc.Name)));
                        }
                        else
                        {
                            // Denied or timed out — inject an error result.
                            var denialReason = string.IsNullOrEmpty(decision.Reason)
                                ? "Tool call was denied or timed out."
                                : decision.Reason;
                            syntheticResults[i] = DurableToolDecisionPolicy.DenialMessage(denialReason);
                        }
                        break;

                    case DurableToolOutcome.Skip:
                        syntheticResults[i] = DurableToolDecisionPolicy.SkipMessage(interceptorResult?.Message);
                        break;

                    case DurableToolOutcome.Block:
                    default:
                        syntheticResults[i] = DurableToolDecisionPolicy.BlockMessage(interceptorResult?.Message);
                        break;
                }
            }

            // ── Phase 2.5: Dispatch buffered tool activities serially ───────────────────
            // All approval waits are resolved before the first tool starts. Serial dispatch makes
            // effect order explicit and lets each later tool observe earlier StateBag updates.
            ThrowIfCurrentTurnCancellationRequested();
            var terminateTurn = false;
            foreach (var (idx, input, opts) in pendingToolDispatches)
            {
                if (terminateTurn)
                {
                    syntheticResults[idx] = "[Tool skipped] Tool middleware terminated this turn.";
                    continue;
                }
                var dispatchInput = new InvokeAgentToolInput
                {
                    AgentName = input.AgentName,
                    SessionId = input.SessionId,
                    MemoryScope = input.MemoryScope,
                    ToolName = input.ToolName,
                    Arguments = input.Arguments,
                    CallId = input.CallId,
                    PreparedTool = input.PreparedTool,
                    ApprovalAccepted = input.ApprovalAccepted,
                    ApprovedCallFingerprint = input.ApprovedCallFingerprint,
                    Messages = preparedContext.HasToolMiddleware ? accumulated.ToArray() : null,
                    Iteration = preparedContext.HasToolMiddleware ? iteration + 1 : 0,
                    FunctionCallIndex = preparedContext.HasToolMiddleware ? idx : 0,
                    FunctionCount = preparedContext.HasToolMiddleware ? toolCalls.Count : 0,
                    MaximumTransportPayloadSizeBytes = input.MaximumTransportPayloadSizeBytes,
                    PayloadCodecExpansionAllowanceBytes = input.PayloadCodecExpansionAllowanceBytes,
                    SerializedStateBag = GetStateBagForDispatch(),
                };
                TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                    Workflow.PayloadConverter,
                    dispatchInput,
                    dispatchInput.MaximumTransportPayloadSizeBytes,
                    dispatchInput.PayloadCodecExpansionAllowanceBytes,
                    "tool-activity input");
                MarkInterruptedToolCallDispatched(interruptedBatchIndex, idx);
                var toolResult = await Workflow.ExecuteActivityAsync(
                    (AgentActivities a) => a.InvokeAgentToolAsync(dispatchInput),
                    ApplyCurrentTurnCancellation(opts)).ConfigureAwait(true);
                toolResults[idx] = toolResult;
                RecordInterruptedToolCallResult(
                    interruptedBatchIndex,
                    idx,
                    toolResult.Result,
                    wasDispatched: true,
                    failed: toolResult.Failed);
                MergeStateBagWriteBacks([toolResult.StateBagDelta]);
                terminateTurn = toolResult.Terminate;
            }

            for (var i = 0; i < toolCalls.Count; i++)
            {
                if (syntheticResults[i] is { } synthetic)
                {
                    RecordInterruptedToolCallResult(
                        interruptedBatchIndex,
                        i,
                        synthetic,
                        wasDispatched: false);
                }
            }

            ThrowIfCurrentTurnCancellationRequested();

            // Assemble final results in original order.
            // Tool results are already applied in call order; preserve that order in the model
            // transcript even when some calls were blocked or denied.
            var functionResultContents = new List<AIContent>(toolCalls.Count);
            for (var i = 0; i < toolCalls.Count; i++)
            {
                if (syntheticResults[i] is { } synthetic)
                {
                    functionResultContents.Add(new FunctionResultContent(
                        callId: toolCalls[i].CallId,
                        result: synthetic));
                    RecordInterruptedToolCallResult(
                        interruptedBatchIndex,
                        i,
                        synthetic,
                        wasDispatched: false);
                }
                else if (toolResults[i] is { } toolResult)
                {
                    // S-X-6: toolResult.Result crosses the activity boundary as a JsonElement
                    // (declared object?), so FunctionResultContent.Result holds a JsonElement here,
                    // not the tool's domain type. Accepted limitation — see InvokeAgentToolResult.Result.
                    functionResultContents.Add(new FunctionResultContent(
                        callId: toolCalls[i].CallId,
                        result: toolResult.Result));
                    RecordInterruptedToolCallResult(
                        interruptedBatchIndex,
                        i,
                        toolResult.Result,
                        wasDispatched: true,
                        failed: toolResult.Failed);
                }
            }

            var toolResultMessage = new ChatMessage(ChatRole.Tool, functionResultContents);
            accumulated.Add(toolResultMessage);
            allTurnMessages.Add(toolResultMessage);
            if (terminateTurn)
            {
                var finalResponse = new AgentResponse
                {
                    Messages = allTurnMessages,
                    Usage = totalUsage,
                    CreatedAt = Workflow.UtcNow,
                };
                await CompleteContextProviderTurnAsync(
                    initialMessages, finalResponse.Messages.ToList(), invokeException: null).ConfigureAwait(true);
                return finalResponse;
            }
        }

        var effectiveMaxIterations = _input!.MaxToolCallsPerTurn;
        Workflow.Logger.LogDurableAgentTurnAborted(_input!.AgentName, effectiveMaxIterations);

        var errorMessage = new ChatMessage(
            ChatRole.Assistant,
            $"Maximum tool-call iterations ({effectiveMaxIterations}) exceeded for agent '{_input!.AgentName}'. " +
            "The agent did not converge on a final answer.");
        allTurnMessages.Add(errorMessage);

        var abortedResponse = new AgentResponse
        {
            Messages = allTurnMessages,
            Usage = totalUsage,
            CreatedAt = Workflow.UtcNow,
        };

        await CompleteContextProviderTurnAsync(
            initialMessages,
            abortedResponse.Messages.ToList(),
            invokeException: null).ConfigureAwait(true);
        return abortedResponse;
        }
        catch (Exception exception)
        {
            await CompleteContextProviderTurnAsync(
                initialMessages,
                allTurnMessages.ToList(),
                exception).ConfigureAwait(true);
            throw;
        }
    }

    private async Task CompleteContextProviderTurnAsync(
        IReadOnlyList<ChatMessage> requestMessages,
        IReadOnlyList<ChatMessage> responseMessages,
        Exception? invokeException)
    {
        var isCancellation = invokeException is OperationCanceledException
            || IsCurrentTurnCancellationRequested();
        var effectiveException = isCancellation && invokeException is not OperationCanceledException
            ? new OperationCanceledException("The agent turn was cancelled.")
            : invokeException;
        var failureMessage = effectiveException is null
            ? null
            : $"{effectiveException.GetType().Name}: {effectiveException.Message}";
        if (failureMessage is { Length: > 2048 })
        {
            failureMessage = failureMessage[..2048];
        }

        var input = new CompleteAgentTurnInput
        {
            AgentName = _input!.AgentName,
            SessionId = Workflow.Info.WorkflowId!,
            RequestMessages = requestMessages,
            ResponseMessages = responseMessages,
            SerializedStateBag = GetStateBagForDispatch(),
            MemoryScope = _memoryScope,
            InvokeExceptionMessage = failureMessage,
            IsCancellation = isCancellation,
            MaximumTransportPayloadSizeBytes = _input.MaximumTransportPayloadSizeBytes,
            PayloadCodecExpansionAllowanceBytes = _input.PayloadCodecExpansionAllowanceBytes,
        };
        try
        {
            TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                input,
                input.MaximumTransportPayloadSizeBytes,
                input.PayloadCodecExpansionAllowanceBytes,
                "agent-turn completion input");
            var completionOptions = new ActivityOptions
            {
                StartToCloseTimeout = isCancellation ? TimeSpan.FromSeconds(10) : _input.ActivityTimeout,
                Summary = AgentActivities.BuildActivitySummary(_input.AgentName),
                RetryPolicy = new RetryPolicy { MaximumAttempts = isCancellation ? 1 : 3 },
                CancellationToken = isCancellation ? CancellationToken.None : CurrentTurnCancellationToken,
                CancellationType = ActivityCancellationType.WaitCancellationCompleted,
            };
            var result = isCancellation
                ? await Workflow.RunTaskAsync(
                    () => Workflow.ExecuteActivityAsync(
                        (AgentActivities activities) => activities.CompleteAgentTurnAsync(input),
                        completionOptions),
                    CancellationToken.None).ConfigureAwait(true)
                : await Workflow.ExecuteActivityAsync(
                    (AgentActivities activities) => activities.CompleteAgentTurnAsync(input),
                    completionOptions).ConfigureAwait(true);
            _currentStateBag = StateBagMerge.ApplyTrustedDelta(_currentStateBag, result.StateBagDelta);
        }
        catch (Exception exception)
        {
            Workflow.Logger.LogWarning(
                "Context-provider completion could not be recorded for agent {AgentName}: {Error}",
                _input.AgentName,
                exception.Message);
        }
    }

    private void MergeStateBagWriteBacks(IReadOnlyList<StateBagDelta?> deltas)
    {
        foreach (var delta in deltas)
        {
            _currentStateBag = StateBagMerge.ApplyDelta(
                _currentStateBag,
                delta);
        }
    }

    private JsonElement? GetStateBagForDispatch() =>
        _currentStateBag ?? StateBagMerge.EmptyStateBag;

    private List<ChatMessage> FlattenHistoryMessages()
    {
        var totalMessageCount = 0;
        foreach (var entry in History)
        {
            totalMessageCount += ProjectHistoryEntryMessages(entry).Count;
        }

        var messages = new List<ChatMessage>(totalMessageCount);
        foreach (var entry in History)
        {
            foreach (var m in ProjectHistoryEntryMessages(entry))
            {
                messages.Add(m);
            }
        }

        return messages;
    }

    private ActivityOptions ResolveDurableToolActivityOptions(string toolName)
    {
        if (DurableToolDecisionPolicy.TryGetToolValue(
            _input!.DurableAgentToolActivityOptions,
            toolName,
            out var perTool))
        {
            return perTool;
        }

        return new ActivityOptions
        {
            StartToCloseTimeout = _input!.ActivityTimeout,
            HeartbeatTimeout = _input!.HeartbeatTimeout,
            Summary = toolName,
            RetryPolicy = _input!.RetryPolicy,
        };
    }

    private async Task ProcessFireAndForgetAsync(RunRequest request)
    {
        TurnAdmission? admission = null;
        try
        {
            var requestId = request.RequestId ?? Workflow.NewGuid().ToString("N");
            var admissionFingerprint = CreateAgentAdmissionFingerprint(request);
            admission = BeginTurnAdmission(
                requestId,
                admissionFingerprint);
            await WaitForAgentInputAsync().ConfigureAwait(true);
            admission = RefreshTurnAdmission(admission, requestId, admissionFingerprint);
            ValidateInitializedTurnAdmission(admission);

            var requestEntry = CreateAgentRequestEntry(request, requestId);
            await RunTurnAsync(requestEntry, admission, chatOptions: null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (admission is not null)
            {
                FailTurnAdmission(admission, ex);
            }
            Workflow.Logger.LogFireAndForgetActivityFailed(
                _input?.AgentName ?? "unknown", Workflow.Info.WorkflowId, ex);
            // Swallow — fire-and-forget errors must not crash the session.
        }
        finally
        {
            _pendingFireAndForgetTurns--;
        }
    }

    /// <summary>
    /// Waits until the workflow run method has initialized the typed agent input.
    /// Update-With-Start handlers can be admitted before the run task
    /// executes its first user statement, so every first-turn handler must cross this barrier.
    /// </summary>
    private async Task WaitForAgentInputAsync()
    {
        if (_input is null)
        {
            await Workflow.WaitConditionAsync(() => _input is not null).ConfigureAwait(true);
        }
    }

    JsonElement? TemporalCommunity.Extensions.AI.Internal.IDurableTurnRollbackParticipant
        .CaptureTurnRollbackState()
    {
        _stateBagRevisionAtTurnStart = _stateBagRevision;
        return _currentStateBag;
    }

    void TemporalCommunity.Extensions.AI.Internal.IDurableTurnRollbackParticipant
        .RestoreTurnRollbackState(JsonElement? state)
    {
        _currentStateBag = state;
        _stateBagRevision = _stateBagRevisionAtTurnStart;
    }
}
