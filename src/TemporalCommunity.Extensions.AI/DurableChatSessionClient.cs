using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// External entry point for managed durable chat sessions.
/// Each conversation maps to a Temporal workflow that persists history across turns.
/// </summary>
public sealed class DurableChatSessionClient : IDurableChatSessionClient
{
    private readonly ITemporalClient _client;
    private readonly DurableExecutionOptions _options;
    private readonly ILogger _logger;
    private readonly DurableFunctionRegistry? _functionRegistry;
    private readonly DurableChatToolOptionsRegistry? _toolOptionsRegistry;
    // Snapshots are computed once at first use. DurableFunctionRegistry is effectively stable
    // after host construction — runtime mutation is not supported.
    private readonly Lazy<IReadOnlyDictionary<string, ActivityOptions>?> _toolActivityOptionsCache;
    private readonly Lazy<ActivityOptions?> _interceptorActivityOptionsCache;
    private readonly Lazy<IReadOnlyDictionary<string, ActivityOptions>?> _interceptorToolActivityOptionsCache;
    private readonly Lazy<IReadOnlyList<string>?> _interceptorSkippedToolsCache;
    private readonly Lazy<IReadOnlyList<string>?> _requiresApprovalToolsCache;

    /// <summary>
    /// Initializes a new <see cref="DurableChatSessionClient"/>.
    /// </summary>
    /// <param name="client">The Temporal client used to start and update workflows.</param>
    /// <param name="options">Durable execution configuration. Must pass validation before use.</param>
    /// <param name="logger">Optional logger. Defaults to a no-op logger when null.</param>
    public DurableChatSessionClient(
        ITemporalClient client,
        DurableExecutionOptions options,
        ILogger<DurableChatSessionClient>? logger = null)
        : this(client, options, logger, functionRegistry: null, toolOptionsRegistry: null)
    {
    }

    /// <summary>
    /// Internal constructor used by DI to inject the durable-tool registries needed for
    /// Pattern 3 activation. External callers use the public 3-arg constructor; the
    /// session client they get back will be Pattern 1 only (no registries injected).
    /// </summary>
    internal DurableChatSessionClient(
        ITemporalClient client,
        DurableExecutionOptions options,
        ILogger<DurableChatSessionClient>? logger,
        DurableFunctionRegistry? functionRegistry,
        DurableChatToolOptionsRegistry? toolOptionsRegistry)
    {
        // Primary constructors have no body for guard statements, so ArgumentNullException.ThrowIfNull()
        // cannot be used here — field initializers run before any constructor body would.
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = ValidateOptions(options);
        _logger = logger ?? NullLogger<DurableChatSessionClient>.Instance;
        _functionRegistry = functionRegistry;
        _toolOptionsRegistry = toolOptionsRegistry;
        _toolActivityOptionsCache = new Lazy<IReadOnlyDictionary<string, ActivityOptions>?>(
            BuildToolActivityOptions,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _interceptorActivityOptionsCache = new Lazy<ActivityOptions?>(
            BuildInterceptorActivityOptions,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _interceptorToolActivityOptionsCache = new Lazy<IReadOnlyDictionary<string, ActivityOptions>?>(
            BuildInterceptorToolActivityOptions,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _interceptorSkippedToolsCache = new Lazy<IReadOnlyList<string>?>(
            BuildInterceptorSkippedTools,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _requiresApprovalToolsCache = new Lazy<IReadOnlyList<string>?>(
            BuildRequiresApprovalTools,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // Validates and returns the options; used as a field initializer so validation fires
    // at construction time even though there is no explicit constructor body.
    private static DurableExecutionOptions ValidateOptions(DurableExecutionOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        opts.Validate();
        return opts;
    }

    /// <summary>
    /// Sends messages to a durable chat session and returns the response entry.
    /// Starts the session workflow if not already running.
    /// </summary>
    /// <param name="conversationId">A unique identifier for the conversation.</param>
    /// <param name="messages">The messages to send.</param>
    /// <param name="options">Optional chat options.</param>
    /// <param name="correlationId">
    /// Optional caller-supplied correlation ID for this turn. When null/empty, the
    /// workflow auto-generates one via <c>Workflow.NewGuid()</c>. Useful for threading
    /// upstream HTTP/gRPC trace IDs into the workflow for cross-system log correlation.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response entry from the LLM, including per-turn <see cref="UsageDetails"/> and correlation ID.</returns>
    public async Task<DurableSessionResponse> ChatAsync(
        string conversationId,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(messages);

        var workflowId = GetWorkflowId(conversationId);

        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            DurableChatTelemetry.ChatSendSpanName,
            ActivityKind.Client);

        span?.SetTag(DurableChatTelemetry.ConversationIdAttribute, conversationId);
        span?.SetTag(DurableChatTelemetry.RequestModelAttribute, options?.ModelId);

        _logger.LogClientSendingChat(workflowId);

        // Eagerly resolve per-tool ActivityOptions for every registered tool at session
        // start. The resulting dict (or null when no tools are registered) is the
        // activation marker for Pattern 3 — it freezes into workflow history and survives
        // continue-as-new transitions deterministically.
        var toolActivityOptions = _toolActivityOptionsCache.Value;

        // Interceptor config: all four are computed once and frozen into workflow history
        // for replay-deterministic behaviour. RequiresApprovalTools is populated even when
        // no interceptor is registered (BLOCK-2 fix — RequireApproval() is an absolute floor).
        var interceptorActivityOptions = _interceptorActivityOptionsCache.Value;
        var interceptorToolActivityOptions = _interceptorToolActivityOptionsCache.Value;
        var interceptorSkippedTools = _interceptorSkippedToolsCache.Value;
        var requiresApprovalTools = _requiresApprovalToolsCache.Value;

        // Start the workflow if it doesn't exist, or reuse the existing one.
        // OriginalCreatedAt is intentionally omitted here — the workflow sets it to
        // Workflow.UtcNow on the first run and carries it forward through CAN transitions.
        await _client.StartWorkflowAsync(
            (DurableChatWorkflow wf) => wf.RunAsync(new DurableChatWorkflowInput
            {
                TimeToLive = _options.SessionTimeToLive,
                ActivityTimeout = _options.ActivityTimeout,
                HeartbeatTimeout = _options.HeartbeatTimeout,
                RetryPolicy = _options.RetryPolicy,
                ApprovalTimeout = _options.ApprovalTimeout,
                EnableSearchAttributes = _options.EnableSearchAttributes,
                MaxEntryCount = _options.MaxEntryCount,
                // Both reducer forms are set:
                // - HistoryReducer: [JsonIgnore] delegate for in-process / embedded-test use.
                // - HistoryReducerKey: serialized key for production durable workflows.
                // The durable CAN path uses HistoryReducerKey when present; falls back to
                // HistoryReducer (inline) only when HistoryReducerKey is null.
                HistoryReducer = _options.HistoryReducer,
                HistoryReducerKey = _options.DefaultHistoryReducerKey,
                ToolActivityOptions = toolActivityOptions,
                MaxToolCallsPerTurn = _options.MaxToolCallsPerTurn,
                MaximumConsecutiveErrorsPerRequest = _options.MaximumConsecutiveErrorsPerRequest,
                IncludeDetailedErrors = _options.IncludeDetailedErrors,
                InterceptorActivityOptions = interceptorActivityOptions,
                InterceptorToolActivityOptions = interceptorToolActivityOptions,
                InterceptorSkippedTools = interceptorSkippedTools,
                RequiresApprovalTools = requiresApprovalTools,
            }),
            new WorkflowOptions(workflowId, _options.TaskQueue!)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            }).ConfigureAwait(false);

        // Use a handle WITHOUT a pinned RunId so updates follow the continue-as-new chain.
        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);

        // Resolve effective client key: per-call override wins, then worker-level default.
        var effectiveKey = options.GetChatClientKey() ?? _options.DefaultChatClientKey;

        // Send the chat turn via workflow update.
        var input = new DurableChatInput
        {
            Messages = messages as IList<ChatMessage> ?? messages.ToList(),
            Options = options,
            ConversationId = conversationId,
            ClientKey = effectiveKey,
            CorrelationId = string.IsNullOrEmpty(correlationId) ? null : correlationId,
        };

        var responseEntry = await handle.ExecuteUpdateAsync<DurableChatWorkflow, DurableSessionResponse>(
            wf => wf.ChatAsync(input),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);

        span?.SetTag(DurableChatTelemetry.InputTokensAttribute, responseEntry.Usage?.InputTokenCount);
        span?.SetTag(DurableChatTelemetry.OutputTokensAttribute, responseEntry.Usage?.OutputTokenCount);

        return responseEntry;
    }

    /// <summary>
    /// Retrieves the conversation history for a session as a list of
    /// <see cref="DurableSessionEntry"/> instances. Each turn appears as a request entry
    /// followed by a response entry.
    /// </summary>
    public async Task<IReadOnlyList<DurableSessionEntry>> GetHistoryAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var workflowId = GetWorkflowId(conversationId);
        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);

        return await handle.QueryAsync<DurableChatWorkflow, IReadOnlyList<DurableSessionEntry>>(
            wf => wf.GetHistory(),
            new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
    }

    // ── HITL: Tool Approval ─────────────────────────────────────────────

    /// <summary>
    /// Returns the currently pending approval request for a session, or null if none.
    /// </summary>
    public async Task<DurableApprovalRequest?> GetPendingApprovalAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(GetWorkflowId(conversationId));
        return await handle.QueryAsync<DurableChatWorkflow, DurableApprovalRequest?>(
            wf => wf.GetPendingApproval(),
            new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits a human decision for a pending tool approval request.
    /// Unblocks the workflow's <c>RequestApprovalAsync</c> update.
    /// </summary>
    public async Task SubmitApprovalAsync(
        string conversationId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(decision);

        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(GetWorkflowId(conversationId));
        await handle.ExecuteUpdateAsync(
            wf => wf.SubmitApprovalAsync(decision),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a graceful shutdown signal to the session workflow so it exits its session loop
    /// rather than sitting parked until <see cref="DurableExecutionOptions.SessionTimeToLive"/>.
    /// </summary>
    public async Task ShutdownAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var handle = _client.GetWorkflowHandle(GetWorkflowId(conversationId));
        await handle.SignalAsync(
            DurableChatWorkflowBase<ChatResponse>.ShutdownSignalName,
            Array.Empty<object>(),
            new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Generates the workflow ID from a conversation ID using the configured
    /// <see cref="DurableExecutionOptions.WorkflowIdPrefix"/>. Use this in tool
    /// closures or external code that needs to address the workflow directly (e.g.,
    /// <c>temporalClient.GetWorkflowHandle(sessionClient.GetWorkflowId(conversationId))</c>)
    /// so the prefix stays in sync with the worker configuration.
    /// </summary>
    public string GetWorkflowId(string conversationId) =>
        $"{_options.WorkflowIdPrefix}{conversationId}";

    /// <summary>
    /// Builds the per-tool <see cref="ActivityOptions"/> dictionary that the workflow uses
    /// to dispatch each tool call when Pattern 3 is active. Returns <see langword="null"/>
    /// when no durable tools are registered — that's the workflow's signal to stay on the
    /// Pattern 1 single-activity path.
    /// </summary>
    /// <remarks>
    /// Iterates every entry in the function registry (NOT just tools with explicit option
    /// overrides) so the workflow has a complete activation snapshot. Per-tool overrides
    /// from <see cref="_toolOptionsRegistry"/> are applied if present; otherwise each slot
    /// is filled from <see cref="DurableExecutionOptions"/> defaults.
    /// </remarks>
    private IReadOnlyDictionary<string, ActivityOptions>? BuildToolActivityOptions()
    {
        if (_functionRegistry is null || _functionRegistry.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, ActivityOptions>(
            _functionRegistry.Count,
            StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in _functionRegistry)
        {
            DurableChatToolOptions? perTool = null;
            _toolOptionsRegistry?.TryGetValue(kvp.Key, out perTool);

            result[kvp.Key] = new ActivityOptions
            {
                StartToCloseTimeout = perTool?.StartToCloseTimeout ?? _options.ActivityTimeout,
                HeartbeatTimeout = perTool?.HeartbeatTimeout ?? _options.HeartbeatTimeout,
                RetryPolicy = perTool?.RetryPolicy ?? _options.RetryPolicy,
                Summary = kvp.Key,
            };
        }

        return result;
    }

    /// <summary>
    /// Builds the shared <see cref="ActivityOptions"/> for the <c>RunToolInterceptor</c>
    /// activity. Returns <see langword="null"/> when no interceptor factory is registered —
    /// that's the workflow's signal to skip the interceptor fan-out entirely.
    /// </summary>
    private ActivityOptions? BuildInterceptorActivityOptions()
    {
        if (_options.DefaultToolInterceptor is null)
        {
            return null;
        }

        return new ActivityOptions
        {
            StartToCloseTimeout = _options.ActivityTimeout,
            HeartbeatTimeout = _options.HeartbeatTimeout,
            RetryPolicy = _options.RetryPolicy,
        };
    }

    /// <summary>
    /// Builds per-tool <see cref="ActivityOptions"/> overrides for the <c>RunToolInterceptor</c>
    /// activity. Only tools with a non-null <c>InterceptorTimeout</c> get an entry.
    /// Returns <see langword="null"/> when no interceptor is registered or no per-tool
    /// overrides are configured.
    /// </summary>
    private IReadOnlyDictionary<string, ActivityOptions>? BuildInterceptorToolActivityOptions()
    {
        if (_options.DefaultToolInterceptor is null || _toolOptionsRegistry is null)
        {
            return null;
        }

        Dictionary<string, ActivityOptions>? result = null;

        foreach (var kvp in _toolOptionsRegistry)
        {
            if (kvp.Value.InterceptorTimeout.HasValue)
            {
                result ??= new Dictionary<string, ActivityOptions>(StringComparer.OrdinalIgnoreCase);
                result[kvp.Key] = new ActivityOptions
                {
                    StartToCloseTimeout = kvp.Value.InterceptorTimeout,
                    HeartbeatTimeout = _options.HeartbeatTimeout,
                    RetryPolicy = _options.RetryPolicy,
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the list of tool names that should skip the interceptor activity.
    /// Returns <see langword="null"/> when no interceptor is registered or no tools
    /// have <c>SkipInterceptorFlag</c> set.
    /// </summary>
    private IReadOnlyList<string>? BuildInterceptorSkippedTools()
    {
        if (_options.DefaultToolInterceptor is null || _toolOptionsRegistry is null)
        {
            return null;
        }

        List<string>? result = null;

        foreach (var kvp in _toolOptionsRegistry)
        {
            if (kvp.Value.SkipInterceptorFlag)
            {
                (result ??= new List<string>()).Add(kvp.Key);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the list of tool names that always require human approval before dispatch.
    /// Populated unconditionally regardless of whether a <c>DefaultToolInterceptor</c> is
    /// registered (BLOCK-2 fix — <c>RequireApproval()</c> is an absolute configuration-time floor).
    /// Returns <see langword="null"/> when no tools have <c>RequireApprovalFlag</c> set.
    /// </summary>
    private IReadOnlyList<string>? BuildRequiresApprovalTools()
    {
        if (_toolOptionsRegistry is null)
        {
            return null;
        }

        List<string>? result = null;

        foreach (var kvp in _toolOptionsRegistry)
        {
            if (kvp.Value.RequireApprovalFlag)
            {
                (result ??= new List<string>()).Add(kvp.Key);
            }
        }

        return result;
    }
}
