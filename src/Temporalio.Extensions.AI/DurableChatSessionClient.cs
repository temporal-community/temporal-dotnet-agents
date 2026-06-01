using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

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
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = ValidateOptions(options);
        _logger = logger ?? NullLogger<DurableChatSessionClient>.Instance;
        _functionRegistry = functionRegistry;
        _toolOptionsRegistry = toolOptionsRegistry;
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

        _logger.LogDebug("Sending chat to session {WorkflowId}", workflowId);

        // Eagerly resolve per-tool ActivityOptions for every registered tool at session
        // start. The resulting dict (or null when no tools are registered) is the
        // activation marker for Pattern 3 — it freezes into workflow history and survives
        // continue-as-new transitions deterministically.
        var toolActivityOptions = BuildToolActivityOptions();

        // Start the workflow if it doesn't exist, or reuse the existing one.
        // OriginalCreatedAt is intentionally omitted here — the workflow sets it to
        // Workflow.UtcNow on the first run and carries it forward through CAN transitions.
        await _client.StartWorkflowAsync(
            (DurableChatWorkflow wf) => wf.RunAsync(new DurableChatWorkflowInput
            {
                TimeToLive = _options.SessionTimeToLive,
                ActivityTimeout = _options.ActivityTimeout,
                HeartbeatTimeout = _options.HeartbeatTimeout,
                ApprovalTimeout = _options.ApprovalTimeout,
                EnableSearchAttributes = _options.EnableSearchAttributes,
                MaxEntryCount = _options.MaxEntryCount,
                HistoryReducer = _options.HistoryReducer,
                ToolActivityOptions = toolActivityOptions,
                MaxToolCallsPerTurn = _options.MaxToolCallsPerTurn,
                MaximumConsecutiveErrorsPerRequest = _options.MaximumConsecutiveErrorsPerRequest,
                IncludeDetailedErrors = _options.IncludeDetailedErrors,
            }),
            new WorkflowOptions(workflowId, _options.TaskQueue!)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            });

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
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });

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
            new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
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
            new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
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
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
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
}
