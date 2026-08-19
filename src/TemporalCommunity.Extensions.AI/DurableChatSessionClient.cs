using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Exceptions;
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
    private readonly IDurableChatWorkflowInputFactory _workflowInputFactory;
    private readonly ILogger _logger;

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
        : this(
            client,
            options,
            new DurableChatWorkflowInputFactory(options, functionRegistry: null, toolOptionsRegistry: null),
            logger)
    {
    }

    /// <summary>
    /// Internal constructor used by DI to inject the canonical workflow-input factory.
    /// </summary>
    internal DurableChatSessionClient(
        ITemporalClient client,
        DurableExecutionOptions options,
        IDurableChatWorkflowInputFactory workflowInputFactory,
        ILogger<DurableChatSessionClient>? logger)
    {
        // Primary constructors have no body for guard statements, so ArgumentNullException.ThrowIfNull()
        // cannot be used here — field initializers run before any constructor body would.
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = ValidateOptions(options);
        _workflowInputFactory = workflowInputFactory ??
            throw new ArgumentNullException(nameof(workflowInputFactory));
        _logger = logger ?? NullLogger<DurableChatSessionClient>.Instance;
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
    public async Task<DurableSessionResponse> SendAsync(
        string conversationId,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(messages);

        if (options?.Tools is { Count: > 0 })
        {
            throw new DurableConfigurationException(
                "ChatOptions.Tools cannot be used for a durable chat session. " +
                "Register worker-owned tools with AddDurableTools or AddDurableToolset so " +
                "Temporal can schedule each invocation as an activity.");
        }

        var workflowId = GetWorkflowId(conversationId);

        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            DurableChatTelemetry.ChatSendSpanName,
            ActivityKind.Client);

        span?.SetTag(DurableChatTelemetry.ConversationIdAttribute, conversationId);
        span?.SetTag(DurableChatTelemetry.RequestModelAttribute, options?.ModelId);

        _logger.LogClientSendingChat(workflowId);

        var workflowInput = _workflowInputFactory.Create();

        // Start the workflow if it doesn't exist, or reuse the existing one.
        // OriginalCreatedAt is intentionally omitted here — the workflow sets it to
        // Workflow.UtcNow on the first run and carries it forward through CAN transitions.
        var startOp = WithStartWorkflowOperation.Create(
            (DurableChatWorkflow wf) => wf.RunAsync(workflowInput),
            new WorkflowOptions(workflowId, _options.TaskQueue!)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                // Rpc is disallowed on the start operation — cancellation goes on the update options below.
            });

        // Resolve effective client key: per-call override wins, then worker-level default.
        var effectiveKey = options.GetChatClientKey() ?? _options.DefaultChatClientKey;

        // Send the chat turn via workflow update.
        var input = new DurableChatInput
        {
            Messages = messages as IList<ChatMessage> ?? messages.ToList(),
            Options = Internal.ChatOptionsSanitizer.PrepareForDurableTransport(options),
            ConversationId = conversationId,
            ClientKey = effectiveKey,
            CorrelationId = string.IsNullOrEmpty(correlationId) ? null : correlationId,
        };

        // Atomic update-with-start: starts the workflow if absent (UseExisting) and delivers the
        // chat turn in a single RPC, closing the client-crash window between start and update. Targets
        // by workflow-id, so it follows the continue-as-new chain like the previous run-less handle.
        // SDK caveat: this call may fail while the workflow still gets started (same partial-failure
        // window that the prior two-RPC sequence had).
        var responseEntry = await _client.ExecuteUpdateWithStartWorkflowAsync<DurableChatWorkflow, DurableSessionResponse>(
            wf => wf.ChatAsync(input),
            new WorkflowUpdateWithStartOptions(startOp)
            {
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            }).ConfigureAwait(false);

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
    /// Resolves a human decision for a pending tool approval request.
    /// </summary>
    public async Task<DurableApprovalResolutionResult> ResolveApprovalAsync(
        string conversationId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(decision);

        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(GetWorkflowId(conversationId));
        return await handle.ExecuteUpdateAsync<DurableChatWorkflow, DurableApprovalResolutionResult>(
            wf => wf.ResolveApprovalAsync(decision),
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
    /// <see cref="DurableExecutionOptions.WorkflowIdPrefix"/>.
    /// </summary>
    /// <remarks>
    /// This is a routing conversion, not authorization. Authenticate the caller and authorize the
    /// application-owned conversation before resolving or using its workflow ID.
    /// </remarks>
    public string GetWorkflowId(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return $"{_options.WorkflowIdPrefix}{conversationId}";
    }

    internal DurableChatWorkflowInput CreateWorkflowInput() => _workflowInputFactory.Create();
}
