using Microsoft.Extensions.AI;
using Temporalio.Common;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Configuration options for durable AI execution via Temporal.
/// </summary>
/// <remarks>
/// Property names on this class are unprefixed (e.g. <c>ActivityTimeout</c>). The MAF
/// counterpart <c>TemporalCommunity.Extensions.Agents.TemporalAgentsOptions</c> uses
/// <c>Default*</c>-prefixed names for worker-level defaults (e.g. <c>DefaultActivityTimeout</c>).
/// This asymmetry is intentional — do not rename properties on either class.
/// </remarks>
/// <seealso cref="global::TemporalCommunity.Extensions.Agents.TemporalAgentsOptions"/>
public sealed class DurableExecutionOptions
{
    /// <summary>
    /// Gets or sets the Temporal task queue for chat activities.
    /// Must be set before use.
    /// </summary>
    public string? TaskQueue { get; set; }

    /// <summary>
    /// Gets or sets the activity start-to-close timeout for LLM calls. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan ActivityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the Temporal retry policy for activities. When null, Temporal defaults apply.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the workflow ID prefix for chat sessions. Defaults to "chat-".
    /// </summary>
    public string WorkflowIdPrefix { get; set; } = "chat-";

    /// <summary>
    /// Gets or sets the session time-to-live. Defaults to 14 days.
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
    /// Per-call overrides via <see cref="TemporalChatOptionsExtensions.WithChatClientKey"/> take precedence.
    /// </summary>
    public string? DefaultChatClientKey { get; set; }

    /// <summary>
    /// Default keyed DI service key used to resolve an <see cref="IChatClientDecorator"/> that
    /// wraps the resolved <see cref="IChatClient"/> per request. When null (default), no
    /// decoration is applied unless the per-call
    /// <see cref="TemporalChatOptionsExtensions.WithChatClientFactoryKey(IChatClient, string)"/>
    /// sets one.
    /// </summary>
    /// <remarks>
    /// Per-call <see cref="TemporalChatOptionsExtensions.WithChatClientFactoryKey(Microsoft.Extensions.AI.ChatOptions, string)"/>
    /// takes precedence over this worker-level default. Built-in keys (e.g. <c>"tags"</c>) are
    /// pre-registered by <c>AddDurableAI</c> / <c>AddTemporalAgents</c>; custom decorators must
    /// be registered with <c>services.AddKeyedSingleton&lt;IChatClientDecorator, ...&gt;(key)</c>
    /// before the worker host starts.
    /// </remarks>
    public string? DefaultChatClientFactoryKey { get; set; }

    /// <summary>
    /// Gets or sets a reducer applied to conversation history before a continue-as-new transition.
    /// When null (default), the full history is carried forward.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this to trim or summarize history when the workflow is about to continue-as-new,
    /// preventing the carried history from growing unbounded across runs.
    /// </para>
    /// <para>
    /// <b>Workflow determinism:</b> the reducer runs inside the workflow task scheduler and
    /// must be synchronous — do not perform async I/O, call LLM APIs, or use <c>Task.Delay</c>.
    /// </para>
    /// </remarks>
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer { get; set; }

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
    /// All supporting infrastructure (options, DataConverter, activities, embeddings) is
    /// still registered regardless of this setting.
    /// </para>
    /// </remarks>
    public bool RegisterDefaultWorkflow { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of LLM iterations the Pattern 3 dispatch loop will
    /// execute before synthesizing an "iterations exceeded" sentinel response and aborting
    /// the turn. Defaults to <c>20</c>.
    /// </summary>
    /// <remarks>
    /// Only relevant when Pattern 3 is active (durable tools registered via
    /// <see cref="DurableAIServiceCollectionExtensions.AddDurableTools(global::Temporalio.Extensions.Hosting.ITemporalWorkerServiceOptionsBuilder, global::Microsoft.Extensions.AI.AIFunction[])"/>).
    /// </remarks>
    public int MaxToolCallsPerTurn { get; set; } = 20;

    /// <summary>
    /// Gets or sets the maximum number of consecutive iterations in which one or more tools
    /// may fail before the Pattern 3 dispatch loop surfaces a non-retryable
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
    /// Gets or sets a factory that creates a worker-level
    /// <see cref="IDurableToolInterceptor{TContext}"/> for intercepting tool calls before
    /// dispatch in the Pattern 3 loop. When <see langword="null"/> (default), no
    /// interceptor activity is dispatched and tools are invoked directly.
    /// </summary>
    /// <remarks>
    /// The factory receives the worker-side <see cref="IServiceProvider"/>. Register
    /// the interceptor implementation in DI and resolve it via the factory:
    /// <code>
    /// opts.DefaultToolInterceptor = sp => sp.GetRequiredService&lt;MyInterceptor&gt;();
    /// </code>
    /// When non-null, the session client pre-computes interceptor <c>ActivityOptions</c>
    /// at session start and freezes them into <c>DurableChatWorkflowInput</c> so replay
    /// is deterministic regardless of which worker processes a given turn.
    /// </remarks>
    public Func<IServiceProvider, IDurableToolInterceptor<DurableToolContext>>? DefaultToolInterceptor
    {
        get; set;
    }

    internal void Validate()
    {
        if (string.IsNullOrEmpty(TaskQueue))
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

        if (MaxEntryCount <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxEntryCount)} must be greater than zero in {nameof(DurableExecutionOptions)}.");
        }

        if (MaxToolCallsPerTurn <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxToolCallsPerTurn)} must be greater than zero in {nameof(DurableExecutionOptions)}.");
        }
    }
}
