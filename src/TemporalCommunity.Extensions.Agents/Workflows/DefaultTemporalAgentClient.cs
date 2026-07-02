using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Default implementation of <see cref="ITemporalAgentClient"/> that communicates with
/// <see cref="AgentWorkflow"/> via Temporal workflow updates (no polling).
/// </summary>
internal sealed class DefaultTemporalAgentClient(
    ITemporalClient client,
    TemporalAgentsOptions options,
    string taskQueue,
    ILogger<DefaultTemporalAgentClient>? logger = null) : ITemporalAgentClient
{
    private readonly ILogger<DefaultTemporalAgentClient> _logger =
        logger ?? NullLogger<DefaultTemporalAgentClient>.Instance;

    /// <inheritdoc/>
    public async Task<AgentResponse> SendAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentClientSendSpanName,
            ActivityKind.Client);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, sessionId.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, sessionId.WorkflowId);

        var workflowOptions = new WorkflowOptions(sessionId.WorkflowId, taskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate
        };

        _logger.LogClientSendingUpdate(sessionId.AgentName, sessionId.WorkflowId);

        workflowOptions.Rpc = new RpcOptions { CancellationToken = cancellationToken };
        await client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(BuildAgentWorkflowInput(sessionId.AgentName)),
            workflowOptions).ConfigureAwait(false);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);

        var response = await handle.ExecuteUpdateAsync<AgentWorkflow, AgentResponse>(
            wf => wf.RunAgentAsync(request),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);

        _logger.LogClientUpdateCompleted(sessionId.AgentName, sessionId.WorkflowId);
        return response;
    }

    /// <inheritdoc/>
    public async Task RunAgentFireAndForgetAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workflowOptions = new WorkflowOptions(sessionId.WorkflowId, taskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate
        };

        _logger.LogClientFireAndForget(sessionId.AgentName, sessionId.WorkflowId);

        workflowOptions.Rpc = new RpcOptions { CancellationToken = cancellationToken };
        await client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(BuildAgentWorkflowInput(sessionId.AgentName)),
            workflowOptions).ConfigureAwait(false);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        await handle.SignalAsync<AgentWorkflow>(
            wf => wf.RunAgentFireAndForgetAsync(request),
            new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DurableApprovalRequest?> GetPendingApprovalAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
            wf => wf.GetPendingApproval(),
            new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SubmitApprovalAsync(
        TemporalAgentSessionId sessionId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        await handle.ExecuteUpdateAsync(
            wf => wf.SubmitApprovalAsync(decision),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RunAgentDelayedAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentScheduleDelayedSpanName,
            ActivityKind.Client);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, sessionId.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, sessionId.WorkflowId);
        span?.SetTag(TemporalAgentTelemetry.ScheduleDelayAttribute, delay.ToString());

        _logger.LogClientDelayedStart(sessionId.AgentName, sessionId.WorkflowId, delay);

        var workflowOptions = new WorkflowOptions(sessionId.WorkflowId, taskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
            StartDelay = delay,
        };

        workflowOptions.Rpc = new RpcOptions { CancellationToken = cancellationToken };

        // Attach the request signal atomically with the workflow start (signal-with-start).
        // A separate SignalAsync after StartWorkflowAsync would create a crash window between the
        // two RPCs where the workflow exists without its request. Signal-with-start is a single
        // server round-trip: the workflow is created AND the signal is queued in one operation.
        // The signal is buffered and delivered when execution begins after the StartDelay elapses.
        workflowOptions.SignalWithStart((AgentWorkflow wf) => wf.RunAgentFireAndForgetAsync(request));

        try
        {
            await client.StartWorkflowAsync(
                (AgentWorkflow wf) => wf.RunAsync(BuildAgentWorkflowInput(sessionId.AgentName)),
                workflowOptions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ScheduleHandle> ScheduleAgentAsync(
        string agentName,
        string scheduleId,
        RunRequest request,
        ScheduleSpec spec,
        SchedulePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);

        var workflowId = $"ta-{agentName.ToLowerInvariant()}-scheduled-{scheduleId}";

        var action = ScheduleActionStartWorkflow.Create(
            (AgentJobWorkflow wf) => wf.RunAsync(BuildAgentJobInput(agentName, request, options, taskQueue)),
            new WorkflowOptions(workflowId, taskQueue));

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentScheduleCreateSpanName,
            ActivityKind.Client);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, agentName);
        span?.SetTag(TemporalAgentTelemetry.ScheduleIdAttribute, scheduleId);

        _logger.LogScheduleAgentCreating(scheduleId, agentName);

        try
        {
            return await client.CreateScheduleAsync(
                scheduleId,
                new Schedule(action, spec) { Policy = policy ?? new SchedulePolicy() },
                new ScheduleOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc/>
    public ScheduleHandle GetAgentScheduleHandle(string scheduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        return client.GetScheduleHandle(scheduleId);
    }

    /// <inheritdoc/>
    public async Task ShutdownAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var handle = client.GetWorkflowHandle(sessionId.WorkflowId);
        await handle.SignalAsync(
            DurableChatWorkflowBase<AgentResponse>.ShutdownSignalName,
            Array.Empty<object>(),
            new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    // ── IDurableSessionControl — explicit implementations ───────────────────
    // ITemporalAgentClient.GetPendingApprovalAsync / SubmitApprovalAsync / ShutdownAsync take
    // TemporalAgentSessionId. IDurableSessionControl uses string workflowId so approval
    // dashboards can address any session directly without constructing a session-ID value.

    async Task<DurableApprovalRequest?> IDurableSessionControl.GetPendingApprovalAsync(
        string workflowId, CancellationToken ct)
    {
        var handle = client.GetWorkflowHandle<AgentWorkflow>(workflowId);
        return await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
            wf => wf.GetPendingApproval(),
            new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = ct } })
            .ConfigureAwait(false);
    }

    async Task IDurableSessionControl.SubmitApprovalAsync(
        string workflowId, DurableApprovalDecision decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var handle = client.GetWorkflowHandle<AgentWorkflow>(workflowId);
        await handle.ExecuteUpdateAsync(
            wf => wf.SubmitApprovalAsync(decision),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = ct } })
            .ConfigureAwait(false);
    }

    async Task IDurableSessionControl.ShutdownAsync(string workflowId, CancellationToken ct)
    {
        var handle = client.GetWorkflowHandle(workflowId);
        await handle.SignalAsync(
            DurableChatWorkflowBase<AgentResponse>.ShutdownSignalName,
            Array.Empty<object>(),
            new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = ct } })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Constructs the <see cref="AgentWorkflowInput"/> for a session by resolving every per-agent
    /// scalar via the inheritance rule: <c>effective = registration.X ?? options.DefaultX</c>.
    /// When this process only declared the agent via <see cref="TemporalAgentsOptions.AddAgentProxy"/>
    /// (split worker/client deployment), a minimal input is built from worker-level defaults plus
    /// the proxy declaration's optional TTL — the actual workflow execution happens on the worker.
    /// </summary>
    /// <exception cref="AgentNotRegisteredException">
    /// Thrown when no durable-agent registration and no proxy declaration exist for
    /// <paramref name="agentName"/>.
    /// </exception>
    internal AgentWorkflowInput BuildAgentWorkflowInput(string agentName) =>
        BuildAgentWorkflowInputCore(agentName, options, taskQueue);

    /// <summary>
    /// Pure builder used by <see cref="BuildAgentWorkflowInput(string)"/> and unit tests.
    /// </summary>
    internal static AgentWorkflowInput BuildAgentWorkflowInputCore(
        string agentName,
        TemporalAgentsOptions options,
        string taskQueue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(taskQueue);

        if (!options.DurableAgentRegistrations.TryGetValue(agentName, out var registration))
        {
            // Proxy-only path: this process declared the agent via AddTemporalAgentProxies +
            // AddAgentProxy("name", ttl) but does not host the durable agent itself (typical
            // split worker/client deployment). The proxy still constructs an AgentWorkflowInput
            // locally to start the workflow on the server; the workflow then runs in the worker
            // process which has the full DurableAgentRegistration. Build a minimal input from
            // worker-level defaults plus the proxy declaration's optional TTL.
            if (options.ProxyDeclarations.TryGetValue(agentName, out var proxyTtl))
            {
                return BuildProxyOnlyAgentWorkflowInput(agentName, options, taskQueue, proxyTtl);
            }

            throw new AgentNotRegisteredException(agentName);
        }

        var perAgentTimeToLive = registration.TimeToLive ?? options.DefaultTimeToLive ?? TimeSpan.FromDays(14);
        var perAgentActivityTimeout = registration.ActivityTimeout ?? options.DefaultActivityTimeout;
        var perAgentHeartbeatTimeout = registration.HeartbeatTimeout ?? options.DefaultHeartbeatTimeout;
        var perAgentApprovalTimeout = registration.ApprovalTimeout ?? options.DefaultApprovalTimeout;
        var perAgentRetryPolicy = registration.RetryPolicy ?? options.DefaultRetryPolicy;
        var perAgentMaxEntryCount = registration.MaxEntryCount ?? options.DefaultMaxEntryCount;
        var perAgentHistoryReducer = registration.HistoryReducer ?? options.DefaultHistoryReducer;
        var perAgentHistoryReducerKey = registration.HistoryReducerKey ?? options.DefaultHistoryReducerKey;

        var toolActivityOptions = BuildDurableAgentToolActivityOptions(
            registration,
            perAgentActivityTimeout,
            perAgentHeartbeatTimeout,
            perAgentRetryPolicy);

        var hasExternalStore = registration.HistoryStore is not null || options.HistoryStore is not null;

        // Feature L: pre-compute interceptor config (interceptor presence + skip/require-approval lists).
        // requiresApprovalTools is populated unconditionally — RequireApproval() is an absolute
        // floor that must be enforced even when no tool interceptor is registered (BLOCK-2 fix).
        // Feature B: scope-aware required tools (RequireApproval + ScopeAware) are excluded from
        // requiresApprovalTools and added to scopeAwareApprovalTools instead so GetEffectiveOutcome
        // does not enforce Rule 2 for them (the interceptor is responsible for approval gating).
        var hasInterceptor = registration.ToolInterceptorFactory is not null
                           || options.DefaultToolInterceptor is not null;
        ActivityOptions? interceptorActivityOpts = null;
        List<string>? interceptorSkippedTools = null;
        List<string>? requiresApprovalTools = null;
        List<string>? scopeAwareTools = null;
        List<string>? scopeAwareApprovalTools = null;

        foreach (var toolReg in registration.Tools)
        {
            var toolOpts = toolReg.Options;
            // RequiresApprovalTools exclusion guard (Task 3.4 / spec Section 13):
            // Only non-scope-aware required tools enter the absolute approval floor.
            if (toolOpts.RequireApprovalFlag && !toolOpts.ScopeAwareFlag)
            {
                (requiresApprovalTools ??= []).Add(toolReg.Name);
            }

            // Scope-aware tool lists.
            if (toolOpts.ScopeAwareFlag)
            {
                (scopeAwareTools ??= []).Add(toolReg.Name);
                if (toolOpts.RequireApprovalFlag)
                {
                    (scopeAwareApprovalTools ??= []).Add(toolReg.Name);
                }
            }
        }

        Dictionary<string, ActivityOptions>? perToolInterceptorOpts = null;

        if (hasInterceptor)
        {
            interceptorActivityOpts = new ActivityOptions
            {
                StartToCloseTimeout = perAgentActivityTimeout,
                HeartbeatTimeout = perAgentHeartbeatTimeout,
                RetryPolicy = perAgentRetryPolicy,
            };

            foreach (var toolReg in registration.Tools)
            {
                if (toolReg.Options.SkipInterceptorFlag)
                {
                    (interceptorSkippedTools ??= []).Add(toolReg.Name);
                }

                // Wire per-tool interceptor timeout when set (F2 fix).
                if (toolReg.Options.InterceptorTimeout.HasValue)
                {
                    perToolInterceptorOpts ??= new Dictionary<string, ActivityOptions>(StringComparer.OrdinalIgnoreCase);
                    perToolInterceptorOpts[toolReg.Name] = new ActivityOptions
                    {
                        StartToCloseTimeout = toolReg.Options.InterceptorTimeout,
                        HeartbeatTimeout = perAgentHeartbeatTimeout,
                        RetryPolicy = perAgentRetryPolicy,
                    };
                }
            }
        }

        // Feature B: resolve approval-scopes config.
        var useApprovalScopes = registration.UseApprovalScopes;
        bool useApprovalScopeStoreMode = false;
        string? alwaysScopesStoreKey = null;
        bool applyAlwaysScopesAtSessionStart = false;
        int maxAlwaysScopeCacheRecords = 0;
        int maxAlwaysScopeCacheBytes = 0;
        TimeSpan approvalScopeActivityTimeout = TimeSpan.Zero;
        int approvalScopeActivityMaximumAttempts = 0;

        if (useApprovalScopes)
        {
            // DefaultToolInterceptor incompatibility check (spec Section 8 / Task 3.4).
            if (options.DefaultToolInterceptor is not null)
            {
                throw new InvalidOperationException(
                    "UseApprovalScopes() cannot be combined with TemporalAgentsOptions.DefaultToolInterceptor. " +
                    "This release does not compose approval scopes with worker-default tool interceptors. " +
                    "Remove DefaultToolInterceptor from TemporalAgentsOptions or do not call UseApprovalScopes() on this agent.");
            }

            var scopeOpts = registration.ApprovalScopesOptions!;

            // Options validation (positive bounds).
            if (scopeOpts.MaxAlwaysScopeCacheRecords <= 0)
                throw new InvalidOperationException($"ApprovalScopesOptions.MaxAlwaysScopeCacheRecords for agent '{agentName}' must be a positive integer.");
            if (scopeOpts.MaxAlwaysScopeCacheBytes <= 0)
                throw new InvalidOperationException($"ApprovalScopesOptions.MaxAlwaysScopeCacheBytes for agent '{agentName}' must be a positive integer.");
            if (scopeOpts.ApprovalScopeActivityMaximumAttempts <= 0)
                throw new InvalidOperationException($"ApprovalScopesOptions.ApprovalScopeActivityMaximumAttempts for agent '{agentName}' must be a positive integer.");
            if (scopeOpts.ApprovalScopeActivityTimeout <= TimeSpan.Zero)
                throw new InvalidOperationException($"ApprovalScopesOptions.ApprovalScopeActivityTimeout for agent '{agentName}' must be greater than TimeSpan.Zero.");

            var hasScopeStore = scopeOpts.ApprovalScopeStore is not null
                             || options.ApprovalScopeStore is not null;
            useApprovalScopeStoreMode = hasScopeStore;
            alwaysScopesStoreKey = scopeOpts.AlwaysScopesStoreKey;
            applyAlwaysScopesAtSessionStart = scopeOpts.ApplyAlwaysScopesAtSessionStart;
            maxAlwaysScopeCacheRecords = scopeOpts.MaxAlwaysScopeCacheRecords;
            maxAlwaysScopeCacheBytes = scopeOpts.MaxAlwaysScopeCacheBytes;
            approvalScopeActivityTimeout = scopeOpts.ApprovalScopeActivityTimeout;
            approvalScopeActivityMaximumAttempts = scopeOpts.ApprovalScopeActivityMaximumAttempts;
        }

        // Startup validation for scope-aware required tools without UseApprovalScopes.
        // (Also enforced in DurableAgentBuilder.ToRegistration but validated again here as defense-in-depth.)
        foreach (var toolReg in registration.Tools)
        {
            var toolOpts = toolReg.Options;
            if (toolOpts.RequireApprovalFlag && toolOpts.ScopeAwareFlag && !useApprovalScopes)
            {
                throw new InvalidOperationException(
                    $"Tool '{toolReg.Name}' has ScopeAware() set but approval scopes are not enabled on agent '{agentName}'. " +
                    "Call UseApprovalScopes() before registering scope-aware required tools.");
            }

            if (toolOpts.RequireApprovalFlag && toolOpts.ScopeAwareFlag && toolOpts.SkipInterceptorFlag)
            {
                throw new InvalidOperationException(
                    $"Tool '{toolReg.Name}' cannot combine RequireApproval(), ScopeAware(), and SkipInterceptor(); approval " +
                    "scopes require the interceptor to enforce the missing-scope approval gate.");
            }
        }

        return new AgentWorkflowInput
        {
            AgentName = agentName,
            TaskQueue = taskQueue,
            TimeToLive = perAgentTimeToLive,
            ActivityTimeout = perAgentActivityTimeout,
            HeartbeatTimeout = perAgentHeartbeatTimeout,
            ApprovalTimeout = perAgentApprovalTimeout,
            RetryPolicy = perAgentRetryPolicy,
            MaxEntryCount = perAgentMaxEntryCount,
            // Both reducer forms are set:
            // - HistoryReducer: [JsonIgnore] delegate for in-process / embedded-test use
            //   (survives within the same process; stripped on wire serialization).
            // - HistoryReducerKey: serialized key for production durable workflows. When set,
            //   the workflow dispatches a ReduceHistoryByKey activity that resolves the delegate
            //   from DI — surviving wire serialization, worker restarts, and replay correctly.
            // The durable CAN path uses HistoryReducerKey when present; falls back to
            // HistoryReducer (inline) only when HistoryReducerKey is null.
            HistoryReducer = perAgentHistoryReducer,
            HistoryReducerKey = perAgentHistoryReducerKey,
            EnableSearchAttributes = options.EnableSearchAttributes,
            // Worker-side settings are baked in — non-null ResolvedWorkerConfig also serves as
            // the "WorkerSettingsResolved = true" signal under the Step 3c.1 migration.
            ResolvedWorkerConfig = new ProxyResolvedWorkerConfig
            {
                MaxToolCallsPerTurn = registration.MaxToolCallsPerTurn,
                UseExternalStoreMode = hasExternalStore,
                ToolActivityOptions = toolActivityOptions,
                InterceptorActivityOptions = interceptorActivityOpts,
                InterceptorToolActivityOptions = perToolInterceptorOpts,
                InterceptorSkippedTools = interceptorSkippedTools,
                RequiresApprovalTools = requiresApprovalTools,
                ScopeAwareTools = scopeAwareTools,
                ScopeAwareApprovalTools = scopeAwareApprovalTools,
                UseApprovalScopes = useApprovalScopes,
                UseApprovalScopeStoreMode = useApprovalScopeStoreMode,
                AlwaysScopesStoreKey = alwaysScopesStoreKey,
                ApplyAlwaysScopesAtSessionStart = applyAlwaysScopesAtSessionStart,
                MaxAlwaysScopeCacheRecords = maxAlwaysScopeCacheRecords,
                MaxAlwaysScopeCacheBytes = maxAlwaysScopeCacheBytes,
                ApprovalScopeActivityTimeout = approvalScopeActivityTimeout,
                ApprovalScopeActivityMaximumAttempts = approvalScopeActivityMaximumAttempts,
            },
        };
    }

    /// <summary>
    /// Builds a fully-populated <see cref="AgentJobInput"/> for a single fire-and-forget agent run.
    /// Shared by <see cref="ScheduleAgentAsync"/> and <see cref="ScheduleActivities"/> so both paths
    /// honour per-agent timeouts, per-tool options, and interceptor config.
    /// </summary>
    internal static AgentJobInput BuildAgentJobInput(
        string agentName,
        RunRequest request,
        TemporalAgentsOptions options,
        string taskQueue)
    {
        var effectiveActivityTimeout = options.DefaultActivityTimeout;
        var effectiveHeartbeatTimeout = options.DefaultHeartbeatTimeout;
        var effectiveRetryPolicy = options.DefaultRetryPolicy;
        Dictionary<string, ActivityOptions>? toolActivityOptions = null;
        ActivityOptions? interceptorActivityOpts = null;
        Dictionary<string, ActivityOptions>? perToolInterceptorOpts = null;
        List<string>? interceptorSkippedTools = null;
        List<string>? requiresApprovalTools = null;
        List<string>? scopeAwareTools = null;
        List<string>? scopeAwareApprovalTools = null;

        if (options.DurableAgentRegistrations.TryGetValue(agentName, out var registration))
        {
            effectiveActivityTimeout = registration.ActivityTimeout ?? options.DefaultActivityTimeout;
            effectiveHeartbeatTimeout = registration.HeartbeatTimeout ?? options.DefaultHeartbeatTimeout;
            effectiveRetryPolicy = registration.RetryPolicy ?? options.DefaultRetryPolicy;
            toolActivityOptions = BuildDurableAgentToolActivityOptions(
                registration, effectiveActivityTimeout, effectiveHeartbeatTimeout, effectiveRetryPolicy);

            // Feature B: RequiresApprovalTools exclusion guard — scope-aware required tools
            // go into scopeAwareApprovalTools, NOT requiresApprovalTools.
            foreach (var toolReg in registration.Tools)
            {
                var toolOpts = toolReg.Options;
                if (toolOpts.RequireApprovalFlag && !toolOpts.ScopeAwareFlag)
                    (requiresApprovalTools ??= []).Add(toolReg.Name);

                if (toolOpts.ScopeAwareFlag)
                {
                    (scopeAwareTools ??= []).Add(toolReg.Name);
                    if (toolOpts.RequireApprovalFlag)
                        (scopeAwareApprovalTools ??= []).Add(toolReg.Name);
                }
            }

            // Feature B: DefaultToolInterceptor incompatibility check (same as workflow input path).
            if (registration.UseApprovalScopes && options.DefaultToolInterceptor is not null)
            {
                throw new InvalidOperationException(
                    "UseApprovalScopes() cannot be combined with TemporalAgentsOptions.DefaultToolInterceptor. " +
                    "This release does not compose approval scopes with worker-default tool interceptors. " +
                    "Remove DefaultToolInterceptor from TemporalAgentsOptions or do not call UseApprovalScopes() on this agent.");
            }

            // Feature B: startup validation for scope-aware required tools.
            foreach (var toolReg in registration.Tools)
            {
                var toolOpts = toolReg.Options;
                if (toolOpts.RequireApprovalFlag && toolOpts.ScopeAwareFlag && !registration.UseApprovalScopes)
                {
                    throw new InvalidOperationException(
                        $"Tool '{toolReg.Name}' has ScopeAware() set but approval scopes are not enabled on agent '{agentName}'. " +
                        "Call UseApprovalScopes() before registering scope-aware required tools.");
                }

                if (toolOpts.RequireApprovalFlag && toolOpts.ScopeAwareFlag && toolOpts.SkipInterceptorFlag)
                {
                    throw new InvalidOperationException(
                        $"Tool '{toolReg.Name}' cannot combine RequireApproval(), ScopeAware(), and SkipInterceptor(); approval " +
                        "scopes require the interceptor to enforce the missing-scope approval gate.");
                }
            }

            var hasInterceptor = registration.ToolInterceptorFactory is not null
                              || options.DefaultToolInterceptor is not null;
            if (hasInterceptor)
            {
                interceptorActivityOpts = new ActivityOptions
                {
                    StartToCloseTimeout = effectiveActivityTimeout,
                    HeartbeatTimeout = effectiveHeartbeatTimeout,
                    RetryPolicy = effectiveRetryPolicy,
                };

                foreach (var toolReg in registration.Tools)
                {
                    if (toolReg.Options.SkipInterceptorFlag)
                        (interceptorSkippedTools ??= []).Add(toolReg.Name);

                    if (toolReg.Options.InterceptorTimeout.HasValue)
                    {
                        perToolInterceptorOpts ??= new Dictionary<string, ActivityOptions>(StringComparer.OrdinalIgnoreCase);
                        perToolInterceptorOpts[toolReg.Name] = new ActivityOptions
                        {
                            StartToCloseTimeout = toolReg.Options.InterceptorTimeout,
                            HeartbeatTimeout = effectiveHeartbeatTimeout,
                            RetryPolicy = effectiveRetryPolicy,
                        };
                    }
                }
            }
        }

        return new AgentJobInput
        {
            AgentName = agentName,
            TaskQueue = taskQueue,
            Request = request,
            ActivityTimeout = effectiveActivityTimeout,
            HeartbeatTimeout = effectiveHeartbeatTimeout,
            RetryPolicy = effectiveRetryPolicy,
            DurableAgentToolActivityOptions = toolActivityOptions,
            MaxToolCallsPerTurn = registration?.MaxToolCallsPerTurn ?? 20,
            InterceptorActivityOptions = interceptorActivityOpts,
            InterceptorToolActivityOptions = perToolInterceptorOpts,
            InterceptorSkippedTools = interceptorSkippedTools,
            RequiresApprovalTools = requiresApprovalTools,
            ScopeAwareTools = scopeAwareTools,
            ScopeAwareApprovalTools = scopeAwareApprovalTools,
        };
    }

    /// <summary>
    /// Builds an <see cref="AgentWorkflowInput"/> for a proxy-only declaration (this process
    /// called <see cref="TemporalAgentsOptions.AddAgentProxy"/> but not
    /// <see cref="TemporalAgentsOptions.AddDurableAgent"/>). The proxy client constructs the
    /// input locally to start the workflow on the server; the actual workflow execution and
    /// per-tool dispatch happen in the worker process which owns the
    /// <see cref="DurableAgentRegistration"/>. Per-tool activity options and external-store mode
    /// are intentionally left null/false here — those are resolved by the worker on its side.
    /// </summary>
    private static AgentWorkflowInput BuildProxyOnlyAgentWorkflowInput(
        string agentName,
        TemporalAgentsOptions options,
        string taskQueue,
        TimeSpan? proxyTtl)
    {
        var timeToLive = proxyTtl ?? options.DefaultTimeToLive ?? TimeSpan.FromDays(14);

        return new AgentWorkflowInput
        {
            AgentName = agentName,
            TaskQueue = taskQueue,
            TimeToLive = timeToLive,
            ActivityTimeout = options.DefaultActivityTimeout,
            HeartbeatTimeout = options.DefaultHeartbeatTimeout,
            ApprovalTimeout = options.DefaultApprovalTimeout,
            RetryPolicy = options.DefaultRetryPolicy,
            MaxEntryCount = options.DefaultMaxEntryCount,
            HistoryReducer = options.DefaultHistoryReducer,
            HistoryReducerKey = options.DefaultHistoryReducerKey,
            EnableSearchAttributes = options.EnableSearchAttributes,
            // Proxy-only construction: leave ResolvedWorkerConfig null. The worker resolves
            // settings (per-tool activity options, external-store mode, max iterations) from its
            // own DurableAgentRegistration on the first step via the NeedsWorkerSettingsResolution
            // handshake.
            ResolvedWorkerConfig = null,
        };
    }

    internal static Dictionary<string, ActivityOptions> BuildDurableAgentToolActivityOptions(
        DurableAgentRegistration registration,
        TimeSpan defaultActivityTimeout,
        TimeSpan defaultHeartbeatTimeout,
        RetryPolicy? defaultRetryPolicy)
    {
        var result = new Dictionary<string, ActivityOptions>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in registration.Tools)
        {
            var toolOpts = tool.Options;
            result[tool.Name] = new ActivityOptions
            {
                StartToCloseTimeout = toolOpts.StartToCloseTimeout ?? defaultActivityTimeout,
                HeartbeatTimeout = toolOpts.HeartbeatTimeout ?? defaultHeartbeatTimeout,
                RetryPolicy = toolOpts.RetryPolicy ?? defaultRetryPolicy,
                Summary = tool.Name,
            };
        }

        return result;
    }
}
