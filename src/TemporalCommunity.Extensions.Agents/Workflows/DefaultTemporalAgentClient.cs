using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using Temporalio.Exceptions;
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

        var startOp = WithStartWorkflowOperation.Create(
            (AgentWorkflow wf) => wf.RunAsync(BuildAgentWorkflowInput(sessionId.AgentName)),
            new WorkflowOptions(sessionId.WorkflowId, taskQueue)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                // Rpc is disallowed on the start operation — cancellation goes on the update options below.
            });

        _logger.LogClientSendingUpdate(sessionId.AgentName, sessionId.WorkflowId);

        // Atomic update-with-start: starts the workflow if absent (UseExisting) and delivers the
        // update in a single RPC, closing the client-crash window between start and update. Targets
        // by workflow-id, so it follows the continue-as-new chain like the previous run-less handle.
        // SDK caveat: this call may fail while the workflow still gets started (same partial-failure
        // window that the prior two-RPC sequence had).
        AgentResponse response;
        try
        {
            response = await client.ExecuteUpdateWithStartWorkflowAsync<AgentWorkflow, AgentResponse>(
                wf => wf.RunAgentAsync(request),
                new WorkflowUpdateWithStartOptions(startOp)
                {
                    Rpc = new RpcOptions { CancellationToken = cancellationToken },
                }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (TemporalCommunity.Extensions.AI.Internal.DurableTurnFailureMapper.Map(
                exception, request.RequestId) is { } mappedFailure)
            {
                throw mappedFailure;
            }

            throw;
        }

        _logger.LogClientUpdateCompleted(sessionId.AgentName, sessionId.WorkflowId);
        return response;
    }

    /// <inheritdoc/>
    public async Task<TemporalAgentTurnOutcome> GetTurnOutcomeAsync(
        TemporalAgentSessionId sessionId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        TemporalAgentTurnOutcome? outcome;
        try
        {
            outcome = await handle.QueryAsync<AgentWorkflow, TemporalAgentTurnOutcome?>(
                workflow => workflow.GetTurnOutcome(requestId),
                new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
                .ConfigureAwait(false);
        }
        catch (RpcException exception) when (
            exception.Code == RpcException.StatusCode.NotFound
            && exception.GrpcStatus.Value is { Details.Count: 1 } status
            && status.Details[0].TryUnpack(
                out Temporalio.Api.ErrorDetails.V1.NotFoundFailure _))
        {
            outcome = null;
        }

        return outcome ?? new TemporalAgentTurnOutcome
        {
            RequestId = requestId,
            Status = TemporalAgentTurnOutcomeStatus.Unavailable,
        };
    }

    /// <inheritdoc/>
    public async Task RunAgentFireAndForgetAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startOp = WithStartWorkflowOperation.Create(
            (AgentWorkflow wf) => wf.RunAsync(BuildAgentWorkflowInput(sessionId.AgentName)),
            new WorkflowOptions(sessionId.WorkflowId, taskQueue)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
            });

        _logger.LogClientFireAndForget(sessionId.AgentName, sessionId.WorkflowId);

        await client.StartUpdateWithStartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAgentFireAndForgetAsync(request),
            new WorkflowStartUpdateWithStartOptions(startOp, WorkflowUpdateStage.Accepted)
            {
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            }).ConfigureAwait(false);
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
    public async Task<DurableChatHistoryBudgetStatus> GetHistoryBudgetAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.QueryAsync<AgentWorkflow, DurableChatHistoryBudgetStatus>(
            workflow => workflow.GetHistoryBudget(),
            new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DurableHistoryRecoveryResult> RetryHistoryCompactionAsync(
        TemporalAgentSessionId sessionId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.ExecuteUpdateAsync<AgentWorkflow, DurableHistoryRecoveryResult>(
            workflow => workflow.RetryHistoryCompactionAsync(operationId),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DurableHistoryRecoveryResult> TrimBlockedHistoryAsync(
        TemporalAgentSessionId sessionId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.ExecuteUpdateAsync<AgentWorkflow, DurableHistoryRecoveryResult>(
            workflow => workflow.TrimBlockedHistoryAsync(operationId),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DurableTurnCancellationResult> CancelTurnAsync(
        TemporalAgentSessionId sessionId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.ExecuteUpdateAsync<AgentWorkflow, DurableTurnCancellationResult>(
            workflow => workflow.CancelTurnAsync(requestId),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DurableApprovalResolutionResult> ResolveApprovalAsync(
        TemporalAgentSessionId sessionId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
            wf => wf.ResolveApprovalAsync(decision),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ShutdownAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        await handle.ExecuteUpdateAsync(
            workflow => workflow.RequestShutdownAsync(),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
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
        // Model steps keep the bounded retry backstop. Tool activities instead make one attempt
        // unless the agent explicitly opts in through its per-agent RetryPolicy.
        var configuredRetryPolicy = registration.RetryPolicy ?? options.DefaultRetryPolicy;
        var perAgentModelRetryPolicy = TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.ResolveForModel(
            configuredRetryPolicy);
        var perAgentToolRetryPolicy =
            TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.ResolveForAgentTool(registration.RetryPolicy);
        var perAgentMaxEntryCount = registration.MaxEntryCount ?? options.DefaultMaxEntryCount;
        var modelContextBudget = AgentModelContextBudget.Resolve(registration, options);
        var perAgentHistoryReducerKey = registration.HistoryReducerKey ?? options.DefaultHistoryReducerKey;

        var toolActivityOptions = BuildDurableAgentToolActivityOptions(
            registration,
            perAgentActivityTimeout,
            perAgentHeartbeatTimeout,
            perAgentToolRetryPolicy);

        // Feature L: pre-compute interceptor config (interceptor presence + skip/require-approval lists).
        // requiresApprovalTools is populated unconditionally — RequireApproval() is an absolute
        // floor that must be enforced even when no tool interceptor is registered (BLOCK-2 fix).
        var hasInterceptor = registration.ToolInterceptorFactory is not null
                           || options.DefaultToolInterceptor is not null;
        ActivityOptions? interceptorActivityOpts = null;
        List<string>? interceptorSkippedTools = null;
        List<string>? requiresApprovalTools = null;

        foreach (var toolReg in registration.Tools)
        {
            var toolOpts = toolReg.Options;
            if (toolOpts.RequireApprovalFlag)
            {
                (requiresApprovalTools ??= []).Add(toolReg.Name);
            }

        }

        Dictionary<string, ActivityOptions>? perToolInterceptorOpts = null;

        if (hasInterceptor)
        {
            interceptorActivityOpts = new ActivityOptions
            {
                StartToCloseTimeout = perAgentActivityTimeout,
                HeartbeatTimeout = perAgentHeartbeatTimeout,
                RetryPolicy = perAgentToolRetryPolicy,
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
                        RetryPolicy = perAgentToolRetryPolicy,
                    };
                }
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
            RetryPolicy = perAgentModelRetryPolicy,
            MaxEntryCount = perAgentMaxEntryCount,
            MaxModelContextTokens = modelContextBudget.MaximumTokens,
            ModelOutputTokenReserve = modelContextBudget.OutputTokenReserve,
            // HistoryReducerKey is serialized for durable workflows. When set,
            //   the workflow dispatches a ReduceHistoryByKey activity that resolves the delegate
            //   from DI — surviving wire serialization, worker restarts, and replay correctly.
            HistoryReducerKey = perAgentHistoryReducerKey,
            EnableSearchAttributes = options.EnableSearchAttributes,
            // Worker-side settings are baked in. A non-null bundle means this workflow does not
            // need the proxy-start resolution handshake.
            ResolvedWorkerConfig = new ProxyResolvedWorkerConfig
            {
                MaxToolCallsPerTurn = registration.MaxToolCallsPerTurn,
                ToolActivityOptions = toolActivityOptions,
                InterceptorActivityOptions = interceptorActivityOpts,
                InterceptorToolActivityOptions = perToolInterceptorOpts,
                InterceptorSkippedTools = interceptorSkippedTools,
                RequiresApprovalTools = requiresApprovalTools,
            },
        };
    }

    /// <summary>
    /// Builds an <see cref="AgentWorkflowInput"/> for a proxy-only declaration (this process
    /// called <see cref="TemporalAgentsOptions.AddAgentProxy"/> but not
    /// <see cref="TemporalAgentsOptions.AddDurableAgent"/>). The proxy client constructs the
    /// input locally to start the workflow on the server; the actual workflow execution and
    /// per-tool dispatch happen in the worker process which owns the
    /// <see cref="DurableAgentRegistration"/>. Per-tool activity options are intentionally left
    /// unresolved here and are supplied by the worker on its side.
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
            // Bounded backstop (MaximumAttempts = 5) when unset — see DefaultRetryPolicy.
            RetryPolicy = TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.ResolveForModel(
                options.DefaultRetryPolicy),
            MaxEntryCount = options.DefaultMaxEntryCount,
            MaxModelContextTokens = options.DefaultMaxModelContextTokens,
            ModelOutputTokenReserve = options.DefaultModelOutputTokenReserve,
            HistoryReducerKey = options.DefaultHistoryReducerKey,
            EnableSearchAttributes = options.EnableSearchAttributes,
            // Proxy-only construction: leave ResolvedWorkerConfig null. The worker resolves
            // settings (per-tool activity options and max iterations) from its
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
                RetryPolicy = ResolveToolRetryPolicy(toolOpts, defaultRetryPolicy, tool.Name),
                Summary = tool.Name,
            };
        }

        return result;
    }

    private static RetryPolicy ResolveToolRetryPolicy(
        DurableToolOptions toolOptions,
        RetryPolicy? perAgentRetryPolicy,
        string toolName)
    {
        if (toolOptions.RetryPolicy is { } perToolRetryPolicy)
        {
            if (perToolRetryPolicy.MaximumAttempts != 1)
            {
                throw new InvalidOperationException(
                    $"Tool '{toolName}' configures per-tool retries. Configure DurableAgentBuilder.RetryPolicy instead; " +
                    "that policy applies to every tool on the agent.");
            }

            if (perAgentRetryPolicy is { MaximumAttempts: > 1 })
            {
                throw new InvalidOperationException(
                    $"Tool '{toolName}' disables retries while the agent has an explicit retry policy. " +
                    "Tool retry behavior must be consistent across the agent; remove the per-tool retry setting.");
            }
        }

        return TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.ResolveForAgentTool(perAgentRetryPolicy);
    }
}
