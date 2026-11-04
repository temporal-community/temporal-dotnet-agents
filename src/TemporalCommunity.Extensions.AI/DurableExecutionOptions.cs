using Microsoft.Extensions.AI;
using Temporalio.Common;
using TemporalCommunity.Extensions.AI.Tools;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Configuration options for durable AI execution via Temporal.
/// </summary>
public sealed class DurableExecutionOptions
{
    /// <summary>
    /// Gets or sets the Temporal task queue for durable AI execution. Managed chat workflows start
    /// on this queue. Direct chat adapters assign it to each scheduled activity, so
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
    /// Gets or sets the Temporal retry policy for model and policy activities. When
    /// <see langword="null"/> (the default), model calls use a five-attempt bounded policy and
    /// policy activities use their own bounded default. Managed tool calls default to one attempt
    /// unless declared idempotent with <see cref="DurableChatToolOptions.Idempotent"/>; idempotent
    /// tools inherit this policy or the bounded tool default. More than one tool attempt requires
    /// that declaration. Attempt bounds are not total execution budgets and do not interpret HTTP
    /// <c>Retry-After</c>. An explicit policy must set a positive <c>MaximumAttempts</c>.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the workflow ID prefix for chat sessions. Defaults to "chat-".
    /// </summary>
    public string WorkflowIdPrefix { get; set; } = "chat-";

    /// <summary>
    /// Gets or sets the session idle lifetime. Admitted or completed work renews the absolute
    /// workflow-time deadline. Defaults to 14 days.
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
    /// Managed sessions freeze this profile when they start; per-turn profile changes are rejected.
    /// Custom workflows using <see cref="DurableChatClient"/> may still select a profile per call.
    /// </summary>
    /// <remarks>
    /// When the stock workflow is registered, worker startup checks that this default profile
    /// (or the unkeyed client) is registered without constructing the provider. The key is
    /// serialized in the session workflow input, while the actual provider is resolved by the
    /// activity from worker DI.
    /// </remarks>
    public string? DefaultChatClientKey { get; set; }

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
    ///     "my-reducer", (sp, key) => MyHistoryReducers.KeepRecentCompleteTurns);
    /// opts.DefaultHistoryReducerKey = "my-reducer";
    /// </code>
    /// </para>
    /// <para>
    /// <b>Determinism requirement:</b> the registered delegate must be pure and deterministic.
    /// An implementation that changes behaviour between deployments without a key change is a
    /// nondeterminism hazard for in-flight sessions (treat reducer changes like workflow-code
    /// changes: new key or <c>Workflow.Patched</c>).
    /// </para>
    /// <para>
    /// The projection must preserve complete request/response turns, ordering, and protected
    /// current, approval, or interrupted turns. Entry-count slicing can split a turn and is rejected.
    /// </para>
    /// </remarks>
    public string? DefaultHistoryReducerKey { get; set; }

    /// <summary>
    /// Gets or sets the explicit retained-history strategy used at a continue-as-new boundary
    /// when the session history must be reduced. Defaults to <see cref="DurableHistoryCompactionStrategy.None"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="DurableHistoryCompactionStrategy.SummarizeOldestTurns"/> uses the worker-owned
    /// history summarizer. <see cref="DurableHistoryCompactionStrategy.TrimRecentTurns"/> is
    /// lossy and records a truncation marker. A keyed <see cref="DefaultHistoryReducerKey"/>
    /// is another explicit strategy; it cannot be combined with this setting.
    /// </remarks>
    public DurableHistoryCompactionStrategy DefaultHistoryCompactionStrategy { get; set; }

    /// <summary>
    /// Gets or sets the keyed-service key captured in new managed-session workflow inputs and
    /// used by the worker to resolve an MEAI <see cref="IChatReducer"/> for each model call.
    /// The reducer receives isolated copies of eligible prior-turn context; its output is not
    /// written to retained session history.
    /// </summary>
    /// <remarks>
    /// Configure the key in the session-client options and register the reducer in worker DI.
    /// Co-hosted applications can use the same value in <c>AddDurableAI</c>:
    /// <code>
    /// services.AddKeyedSingleton&lt;IChatReducer&gt;("recent-context", new MessageCountingChatReducer(20));
    /// opts.DefaultContextReducerKey = "recent-context";
    /// </code>
    /// The session-client and worker must agree on the key. The reducer must preserve valid
    /// tool-call/result exchanges in its output. System and developer messages, the active turn,
    /// approvals, and interrupted turns are not reduced.
    /// </remarks>
    public string? DefaultContextReducerKey { get; set; }

    /// <summary>
    /// Gets or sets whether to require a visible application-owned MEAI inference telemetry
    /// wrapper. The library does not add inference spans; use the MEAI
    /// <c>UseOpenTelemetry()</c> middleware on the worker-owned chat client when instrumentation
    /// is desired. Select <see cref="DurableInferenceTelemetryMode.TrustOpaqueApplicationInstrumentation"/>
    /// when a custom client hides that wrapper and the application verifies instrumentation itself.
    /// </summary>
    public DurableInferenceTelemetryMode InferenceTelemetryMode { get; set; } =
        DurableInferenceTelemetryMode.ApplicationOwned;

    /// <summary>
    /// Gets or sets whether inference and tool telemetry may include message, tool-argument, and
    /// tool-result content, and whether package-owned activity logs include full exception details
    /// at Error level. When unset, the MEAI
    /// <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c> environment default is used.
    /// </summary>
    /// <remarks>
    /// Enabling capture can expose sensitive application data to telemetry exporters. This value
    /// configures visible MEAI <see cref="OpenTelemetryChatClient"/> wrappers and library tool
    /// spans consistently. When disabled, package-owned activity logs include sanitized failure
    /// details at Error and the full exception at Debug. Opaque application-owned wrappers must be
    /// configured to the same value; provider and application logs are unaffected.
    /// </remarks>
    public bool? CaptureTelemetryContent { get; set; }

    /// <summary>
    /// Gets or sets the optional keyed chat-client profile used by the library-owned history
    /// summarizer. When unset, the managed session's default chat-client profile is used.
    /// </summary>
    /// <remarks>
    /// The matching <see cref="IChatClient"/> must be registered in worker DI. This setting and
    /// the summarizer budgets are frozen into new session inputs and carried across
    /// Continue-as-New; they do not enable automatic history compaction by themselves.
    /// </remarks>
    public string? DefaultHistorySummarizerClientKey { get; set; }

    /// <summary>
    /// Gets or sets the summarizer's total context budget, including its output reserve.
    /// Defaults to 8192 tokens.
    /// </summary>
    public int HistorySummarizerMaxContextTokens { get; set; } = 8192;

    /// <summary>
    /// Gets or sets the summarizer's response-token reserve. Defaults to 1024 tokens.
    /// </summary>
    public int HistorySummarizerOutputTokenReserve { get; set; } = 1024;

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
    /// All supporting infrastructure (options, DataConverter, activities, and
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
    /// Gets or sets the maximum estimated model context size in tokens. The output reserve is
    /// included in this total. When null, no local context limit is enforced.
    /// </summary>
    public int? MaxModelContextTokens { get; set; }

    /// <summary>
    /// Gets or sets the number of tokens reserved for model output when
    /// <see cref="MaxModelContextTokens"/> is configured. Defaults to 1024.
    /// </summary>
    public int ModelOutputTokenReserve { get; set; } = 1024;

    /// <summary>
    /// Gets or sets the maximum number of terminal turn outcomes retained for request-ID
    /// deduplication. Defaults to 128. Pending turns are never evicted.
    /// </summary>
    public int MaxTerminalOutcomeCount { get; set; } = 128;

    /// <summary>
    /// Gets or sets the maximum serialized size, in bytes, of the terminal outcome ledger.
    /// Defaults to 1 MiB. An individual outcome larger than this budget is not retained.
    /// </summary>
    public int MaxTerminalOutcomeBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the conservative maximum size, in bytes, of one serialized Temporal payload.
    /// Defaults to 1 MiB and includes a separate reserve for payload-codec expansion.
    /// </summary>
    /// <remarks>
    /// The guard measures the complete payload produced by the configured payload converter,
    /// including metadata, then reserves <see cref="PayloadCodecExpansionAllowanceBytes"/> for
    /// codec expansion. The default is below common Temporal per-payload limits; lower it when
    /// the namespace or deployment has a smaller limit. Do not raise it above the applicable
    /// service limit. Codecs that can expand payloads must declare a sufficient allowance.
    /// </remarks>
    public int MaximumTransportPayloadSizeBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the number of bytes reserved for payload-codec expansion. Defaults to zero.
    /// </summary>
    /// <remarks>
    /// The built-in gzip codec never expands a payload. Set this when using a custom codec whose
    /// maximum expansion is known; the value is subtracted from
    /// <see cref="MaximumTransportPayloadSizeBytes"/> for every guarded payload.
    /// </remarks>
    public int PayloadCodecExpansionAllowanceBytes { get; set; }

    /// <summary>
    /// Gets or sets the history event count at which new turns stop being admitted and the
    /// workflow prepares to continue as new. Defaults to 40,000 events.
    /// </summary>
    public int HistoryEventSoftLimit { get; set; } = 40_000;

    /// <summary>
    /// Gets or sets the history event count at which pending approvals expire to preserve
    /// headroom for the active turn to finish. Defaults to 45,000 events.
    /// </summary>
    public int HistoryEventReserveLimit { get; set; } = 45_000;

    /// <summary>
    /// Gets or sets the history size in bytes at which new turns stop being admitted and the
    /// workflow prepares to continue as new. Defaults to 40 MiB.
    /// </summary>
    public int HistorySizeSoftLimitBytes { get; set; } = 40 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the history size in bytes at which pending approvals expire to preserve
    /// headroom for the active turn to finish. Defaults to 45 MiB.
    /// </summary>
    public int HistorySizeReserveLimitBytes { get; set; } = 45 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum number of matching duplicate requests allowed to join one active
    /// turn. Additional joiners are rejected with a typed Pending error. Defaults to 32.
    /// </summary>
    public int MaxConcurrentTurnJoiners { get; set; } = 32;

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
        if (string.IsNullOrWhiteSpace(TaskQueue))
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

        if (MaxEntryCount < 4)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxEntryCount)} must be at least 4 in {nameof(DurableExecutionOptions)} to retain a complete request/response turn.");
        }

        if (MaxModelContextTokens is <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxModelContextTokens)} must be positive when set.");
        }

        if (ModelOutputTokenReserve < 0
            || (MaxModelContextTokens is { } contextLimit && ModelOutputTokenReserve >= contextLimit))
        {
            throw new InvalidOperationException(
                $"{nameof(ModelOutputTokenReserve)} must be non-negative and smaller than the configured context limit.");
        }

        if (HistorySummarizerMaxContextTokens <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(HistorySummarizerMaxContextTokens)} must be positive.");
        }

        if (HistorySummarizerOutputTokenReserve <= 0
            || HistorySummarizerOutputTokenReserve >= HistorySummarizerMaxContextTokens)
        {
            throw new InvalidOperationException(
                $"{nameof(HistorySummarizerOutputTokenReserve)} must be positive and smaller than " +
                $"{nameof(HistorySummarizerMaxContextTokens)}.");
        }

        if (MaxTerminalOutcomeCount <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxTerminalOutcomeCount)} must be positive in {nameof(DurableExecutionOptions)}.");
        }

        if (MaxTerminalOutcomeBytes <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxTerminalOutcomeBytes)} must be positive in {nameof(DurableExecutionOptions)}.");
        }

        if (MaximumTransportPayloadSizeBytes <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaximumTransportPayloadSizeBytes)} must be positive in {nameof(DurableExecutionOptions)}.");
        }

        if (PayloadCodecExpansionAllowanceBytes < 0
            || PayloadCodecExpansionAllowanceBytes >= MaximumTransportPayloadSizeBytes)
        {
            throw new InvalidOperationException(
                $"{nameof(PayloadCodecExpansionAllowanceBytes)} must be non-negative and smaller than " +
                $"{nameof(MaximumTransportPayloadSizeBytes)} in {nameof(DurableExecutionOptions)}.");
        }

        if (HistoryEventSoftLimit <= 0
            || HistoryEventReserveLimit <= HistoryEventSoftLimit)
        {
            throw new InvalidOperationException(
                $"{nameof(HistoryEventSoftLimit)} must be positive and smaller than " +
                $"{nameof(HistoryEventReserveLimit)} in {nameof(DurableExecutionOptions)}.");
        }

        if (HistorySizeSoftLimitBytes <= 0
            || HistorySizeReserveLimitBytes <= HistorySizeSoftLimitBytes)
        {
            throw new InvalidOperationException(
                $"{nameof(HistorySizeSoftLimitBytes)} must be positive and smaller than " +
                $"{nameof(HistorySizeReserveLimitBytes)} in {nameof(DurableExecutionOptions)}.");
        }

        if (MaxConcurrentTurnJoiners <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxConcurrentTurnJoiners)} must be positive in {nameof(DurableExecutionOptions)}.");
        }

        Internal.DurableTransportPayloadGuard.EnsureBoundedRetryPolicy(
            RetryPolicy,
            nameof(RetryPolicy));

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

        if (DefaultChatClientKey is not null && string.IsNullOrWhiteSpace(DefaultChatClientKey))
        {
            throw new InvalidOperationException(
                $"{nameof(DefaultChatClientKey)} must be null or a non-empty profile key.");
        }

        if (DefaultHistorySummarizerClientKey is not null
            && string.IsNullOrWhiteSpace(DefaultHistorySummarizerClientKey))
        {
            throw new InvalidOperationException(
                $"{nameof(DefaultHistorySummarizerClientKey)} must be null or a non-empty profile key.");
        }

        if (!Enum.IsDefined(typeof(DurableHistoryCompactionStrategy), DefaultHistoryCompactionStrategy))
        {
            throw new InvalidOperationException(
                $"{nameof(DefaultHistoryCompactionStrategy)} is not a supported strategy.");
        }

        if (!Enum.IsDefined(typeof(DurableInferenceTelemetryMode), InferenceTelemetryMode))
        {
            throw new InvalidOperationException(
                $"{nameof(InferenceTelemetryMode)} is not a supported mode.");
        }

        if (DefaultHistoryReducerKey is not null
            && DefaultHistoryCompactionStrategy != DurableHistoryCompactionStrategy.None)
        {
            throw new InvalidOperationException(
                $"{nameof(DefaultHistoryReducerKey)} cannot be combined with " +
                $"{nameof(DefaultHistoryCompactionStrategy)}.");
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
