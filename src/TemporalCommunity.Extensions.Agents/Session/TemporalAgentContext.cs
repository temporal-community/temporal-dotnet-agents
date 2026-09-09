using System.Linq.Expressions;
using Temporalio.Activities;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Approvals;

namespace TemporalCommunity.Extensions.Agents.Session;

/// <summary>
/// Provides async-local access to Temporal capabilities for agent tools executing inside
/// an <see cref="AgentActivities.ExecuteAgentAsync"/> activity.
/// Equivalent to <c>DurableAgentContext</c>.
/// </summary>
public sealed class TemporalAgentContext
{
    private static readonly AsyncLocal<TemporalAgentContext?> s_current = new();
    private readonly ITemporalClient _client;
    private readonly IServiceProvider _services;

    internal TemporalAgentContext(
        ITemporalClient client,
        TemporalAgentSession session,
        IServiceProvider services)
    {
        _client = client;
        CurrentSession = session;
        _services = services;
    }

    private static readonly AsyncLocal<ContextUnavailability?> s_unavailable = new();

    /// <summary>Gets the current <see cref="TemporalAgentContext"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// No context is set. When the tool activity recorded why, the message names the specific
    /// execution path and what to use instead of in-tool approval there.
    /// </exception>
    public static TemporalAgentContext Current =>
        s_current.Value ?? throw new InvalidOperationException(UnavailableMessage());

    internal static void SetCurrent(TemporalAgentContext? ctx)
    {
        s_current.Value = ctx;
        if (ctx is not null)
        {
            s_unavailable.Value = null;
        }
    }

    /// <summary>
    /// Records why no context could be established, so <see cref="Current"/> can explain itself.
    /// Pass <see langword="null"/> to clear.
    /// </summary>
    /// <remarks>
    /// An explicit signal recorded where the decision is made, rather than something inferred from
    /// a caught exception at the call site: a tool body throws
    /// <see cref="InvalidOperationException"/> for plenty of reasons of its own, and relabelling
    /// one of those as an unsupported-path diagnostic would mislead worse than saying nothing.
    /// </remarks>
    internal static void SetUnavailable(
        ContextUnavailableReason? reason,
        string? workflowId = null,
        string? detail = null) =>
        s_unavailable.Value = reason is { } r ? new ContextUnavailability(r, workflowId, detail) : null;

    private static string UnavailableMessage()
    {
        if (s_unavailable.Value is not { } u)
        {
            return "No TemporalAgentContext is available in the current async context.";
        }

        var where = u.WorkflowId is { Length: > 0 } id ? $" (workflow ID '{id}')" : string.Empty;

        return u.Reason switch
        {
            ContextUnavailableReason.SubAgentPath =>
                $"TemporalAgentContext is not available to this tool{where}: the activity's workflow " +
                "ID is not an agent session ID, so there is no agent session to attach. In a running " +
                "application this means the workflow-local sub-agent path " +
                "(WorkflowAgents.GetTemporalAgent) — the tool activity runs under your orchestrating " +
                "workflow. (A test harness supplying an arbitrary workflow ID reaches this same " +
                "state.) In-tool approval (RequestApprovalAsync) is not supported on the sub-agent " +
                "path, and neither is workflow-parked approval: RequireApproval() and an " +
                "interceptor's PauseForApproval() both degrade to Block there. Own the approval in " +
                "the orchestrating workflow, or drive the agent through a managed session via " +
                "TemporalAIAgentProxy.",

            ContextUnavailableReason.ScheduledJobPath =>
                $"TemporalAgentContext is not available to this tool{where}: the workflow ID parses " +
                $"as an agent session but {u.Detail ?? "names a different agent than this tool's"}, " +
                "so attaching that session would target the wrong workflow. A scheduled agent job " +
                "has exactly this shape — its workflow ID is ta-{agent}-scheduled-{runId}, which " +
                "parses to the agent name plus a '-scheduled' suffix. Scheduled jobs cannot park for " +
                "external review at all: in-tool approval (RequestApprovalAsync) is unsupported, and " +
                "RequireApproval() and an interceptor's PauseForApproval() both degrade to Block. " +
                "Use a managed session via TemporalAIAgentProxy for work that needs human review.",

            _ => "No TemporalAgentContext is available in the current async context.",
        };
    }

    private sealed record ContextUnavailability(
        ContextUnavailableReason Reason,
        string? WorkflowId,
        string? Detail);


    /// <summary>
    /// Gets the restored durable agent session for this activity attempt. This is the same
    /// instance supplied to outer MAF middleware; retry-safe <c>StateBag</c> changes made through
    /// either reference are serialized after the model step.
    /// </summary>
    public TemporalAgentSession CurrentSession { get; }

    /// <summary>Starts a new workflow and returns its workflow ID.</summary>
    public async Task<string> StartWorkflowAsync<TWorkflow>(
        Expression<Func<TWorkflow, Task>> workflowRunCall,
        WorkflowOptions options)
    {
        var handle = await _client.StartWorkflowAsync(workflowRunCall, options).ConfigureAwait(false);
        return handle.Id;
    }

    /// <summary>Gets the description of an existing workflow.</summary>
    public async Task<WorkflowExecutionDescription?> GetWorkflowDescriptionAsync(string workflowId)
    {
        try
        {
            var handle = _client.GetWorkflowHandle(workflowId);
            return await handle.DescribeAsync().ConfigureAwait(false);
        }
        catch (RpcException)
        {
            return null;
        }
    }

    /// <summary>Sends a signal to an existing workflow.</summary>
    public Task SignalWorkflowAsync<TWorkflow>(
        string workflowId,
        Expression<Func<TWorkflow, Task>> signalCall)
    {
        var handle = _client.GetWorkflowHandle<TWorkflow>(workflowId);
        return handle.SignalAsync(signalCall);
    }

    /// <summary>Gets a service from the DI container.</summary>
    public TService? GetService<TService>(object? serviceKey = null)
    {
        return (TService?)GetService(typeof(TService), serviceKey);
    }

    /// <summary>Gets a service from the DI container.</summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is not null)
        {
            if (_services is not IKeyedServiceProvider ksp)
            {
                throw new InvalidOperationException("The service provider does not support keyed services.");
            }

            return ksp.GetKeyedService(serviceType, serviceKey);
        }

        return _services.GetService(serviceType);
    }

    // ── GAP 3: Human-in-the-Loop ────────────────────────────────────────────

    /// <summary>
    /// Sends an approval request to the backing <see cref="AgentWorkflow"/> and blocks
    /// until a human resolves a decision via <see cref="ITemporalAgentClient.ResolveApprovalAsync"/>.
    /// </summary>
    /// <remarks>
    /// Call this from inside a tool implementation when the action requires human review:
    /// <code>
    /// var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
    ///     new DurableApprovalRequest { RequestId = Guid.NewGuid().ToString("N"), Description = "Send email to all users" });
    /// if (!decision.Approved) throw new OperationCanceledException("Action rejected by reviewer.");
    /// </code>
    /// <para>
    /// <b>Timeout note:</b> the calling activity blocks for the duration of human review.
    /// Ensure <c>ActivityTimeout</c> is set to exceed your expected review time
    /// (e.g. <c>TimeSpan.FromHours(24)</c>) on the agent's <see cref="TemporalAgentsOptions"/>.
    /// </para>
    /// <para>
    /// <b>Cancellation risk:</b> if the activity is cancelled or times out while the workflow is
    /// waiting for a human response, the pending approval remains set in workflow state. Ordinary
    /// runs still proceed — the <c>RunAgentAsync</c> validator does not consult pending approvals —
    /// but the next approval request fails with <c>DurableApprovalAlreadyPending</c>, because only
    /// one may be outstanding at a time. To recover, resolve the stale request externally with
    /// <see cref="ITemporalAgentClient.CancelPendingApprovalAsync"/>, or with
    /// <see cref="ITemporalAgentClient.ResolveApprovalAsync"/> and a
    /// <see cref="TemporalCommunity.Extensions.AI.Approvals.DurableApprovalDecision"/> whose <c>Approved</c> is
    /// <see langword="false"/>.
    /// </para>
    /// </remarks>
    public async Task<DurableApprovalDecision> RequestApprovalAsync(
        DurableApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var handle = _client.GetWorkflowHandle<AgentWorkflow>(CurrentSession.SessionId.WorkflowId);

        // Outside an activity (unit tests, direct use) there is no activity token to honour and
        // no heartbeat to send.
        if (!ActivityExecutionContext.HasCurrent)
        {
            return await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
                wf => wf.RequestApprovalAsync(request),
                new WorkflowUpdateOptions
                {
                    Rpc = new RpcOptions { CancellationToken = cancellationToken },
                }).ConfigureAwait(false);
        }

        var ctx = ActivityExecutionContext.Current;

        // Link BEFORE starting the update, and use the linked token for the RPC as well as the
        // pump. Most tools pass no token of their own, so binding the RPC to the caller token
        // alone would leave the tool awaiting the workflow after the activity was cancelled —
        // the pump would stop and the wait would not.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, ctx.CancellationToken);

        var approval = handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
            wf => wf.RequestApprovalAsync(request),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = linked.Token } });

        var interval = HeartbeatInterval(ctx.Info.HeartbeatTimeout);

        // No heartbeat timeout configured means nothing can expire for lack of one.
        if (interval is null)
        {
            return await approval.ConfigureAwait(false);
        }

        var pump = HeartbeatUntilStoppedAsync(ctx, interval.Value, linked.Token);

        try
        {
            return await approval.ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();

            // Observe the pump so a fault in it cannot surface later as an unobserved task
            // exception. Its own cancellation is expected and uninteresting.
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Heartbeat cadence for a review wait, or <see langword="null"/> when the activity has no
    /// heartbeat timeout and therefore cannot expire for want of one.
    /// </summary>
    /// <remarks>
    /// A third of the timeout leaves room for two missed beats — scheduling jitter, a slow worker,
    /// a GC pause — before Temporal declares the activity dead. There is no lower floor: a floor
    /// larger than a sub-second heartbeat timeout would expire the activity before the first beat
    /// went out. Only a degenerate sub-3-tick timeout is clamped, and only to one tick.
    /// </remarks>
    private static TimeSpan? HeartbeatInterval(TimeSpan? heartbeatTimeout)
    {
        if (heartbeatTimeout is not { } timeout || timeout <= TimeSpan.Zero)
        {
            return null;
        }

        // Strictly a third, with no floor. A one-second floor would exceed any sub-second
        // heartbeat timeout, so the activity would expire before the pump ever sent a beat —
        // the very failure this pump exists to prevent. A tiny timeout is the caller's choice;
        // matching it is not a heartbeat storm we get to refuse.
        var third = TimeSpan.FromTicks(timeout.Ticks / 3);

        // Guard the degenerate case: a timeout under 3 ticks floors to zero, and Task.Delay
        // would then spin.
        return third > TimeSpan.Zero ? third : TimeSpan.FromTicks(1);
    }

    /// <summary>
    /// Heartbeats until cancelled, keeping the activity alive for the length of a human review.
    /// </summary>
    /// <remarks>
    /// Without this the tool activity heartbeats exactly once — before the tool body runs — so any
    /// review outlasting the heartbeat timeout killed the activity long before the approval
    /// timeout was reached.
    /// </remarks>
    private static async Task HeartbeatUntilStoppedAsync(
        ActivityExecutionContext ctx,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ctx.Heartbeat("awaiting approval decision");
        }
    }
}
