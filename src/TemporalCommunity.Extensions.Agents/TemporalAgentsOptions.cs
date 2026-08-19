using Microsoft.Agents.AI;
using Temporalio.Client.Schedules;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Options for configuring Temporal agents. Agents are registered exclusively via
/// <see cref="AddDurableAgent(string, Action{DurableAgentBuilder})"/>; the v0.2 surface
/// (<c>AddAIAgent</c>, <c>AddAIAgentFactory</c>, etc.) was removed in v0.3.
/// </summary>
/// <remarks>
/// Worker-level default properties on this class use the <c>Default*</c> prefix
/// (e.g. <c>DefaultActivityTimeout</c>) to distinguish them from per-agent overrides on
/// <see cref="DurableAgentBuilder"/>, which use unprefixed names. The MEAI counterpart
/// <see cref="DurableExecutionOptions"/> uses unprefixed names throughout.
/// This asymmetry is intentional — do not rename properties on either class.
/// </remarks>
/// <seealso cref="DurableExecutionOptions"/>
public sealed class TemporalAgentsOptions
{
    // Agent names are case-insensitive across the durable-agent and proxy namespaces.
    private readonly Dictionary<string, DurableAgentRegistration> _durableAgentRegistrations =
        new(StringComparer.OrdinalIgnoreCase);

    // Proxy-only declarations (client-side processes). Stores the optional TTL; the proxy is wired
    // by AddTemporalAgentProxies / TemporalAgentsRegistrar.
    private readonly Dictionary<string, TimeSpan?> _proxyDeclarations =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ScheduleAgentRegistration> _scheduledRuns = [];

    internal TemporalAgentsOptions()
    {
    }

    /// <summary>
    /// Gets or sets the worker-level default TTL for agent workflows. Agents that do not set
    /// <see cref="DurableAgentBuilder.TimeToLive"/> inherit this value. Defaults to 14 days.
    /// Set to <see langword="null"/> to disable TTL by default.
    /// </summary>
    public TimeSpan? DefaultTimeToLive { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Gets or sets the worker-level default activity start-to-close timeout used by the
    /// <c>RunAgentStep</c> activity. Agents inherit this value when
    /// <see cref="DurableAgentBuilder.ActivityTimeout"/> is unset. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan DefaultActivityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the worker-level default heartbeat timeout for agent step activities.
    /// Agents inherit this value when <see cref="DurableAgentBuilder.HeartbeatTimeout"/> is unset.
    /// Defaults to 2 minutes.
    /// </summary>
    public TimeSpan DefaultHeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the worker-level default approval timeout for human-in-the-loop flows.
    /// Agents inherit this value when <see cref="DurableAgentBuilder.ApprovalTimeout"/> is unset.
    /// Defaults to 7 days.
    /// </summary>
    public TimeSpan DefaultApprovalTimeout { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets the worker-level retry-policy override applied to model, tool, and policy
    /// activities. Agents inherit this value when
    /// <see cref="DurableAgentBuilder.RetryPolicy"/> is unset. When <see langword="null"/> (and no
    /// per-agent policy is set), a bounded backstop of <c>new RetryPolicy { MaximumAttempts = 5 }</c>
    /// is applied at session start rather than the Temporal server default (<c>MaximumAttempts = 0</c>,
    /// i.e. unlimited retries). Model calls use a 2-second maximum backoff; tools and policy
    /// activities use a 30-second maximum backoff. Both defaults allow five attempts. Set an
    /// explicit policy to replace these defaults exactly. Per-tool retry policies are configured via
    /// <see cref="DurableAgentBuilder.AddTool(Microsoft.Extensions.AI.AIFunction, Action{DurableToolOptions}?)"/>.
    /// </summary>
    public RetryPolicy? DefaultRetryPolicy { get; set; }

    /// <summary>
    /// Default <c>true</c>. Upserts AgentName / SessionCreatedAt / TurnCount search
    /// attributes on the workflow, enabling operational queries in the Temporal Web UI.
    /// Requires server-side pre-registration of the attribute keys — automatic with
    /// <c>temporal server start-dev</c>; on production clusters use the Temporal CLI to
    /// register them once before starting the worker. Set to <c>false</c> to disable.
    /// </summary>
    public bool EnableSearchAttributes { get; set; } = true;

    /// <summary>
    /// Gets or sets the worker-level default maximum number of <see cref="DurableSessionEntry"/>
    /// instances retained before triggering continue-as-new. Agents inherit this value when
    /// <see cref="DurableAgentBuilder.MaxEntryCount"/> is unset. Defaults to 1000. Continue-as-new
    /// also fires on Temporal SDK's own
    /// <see cref="Temporalio.Workflows.Workflow.ContinueAsNewSuggested"/> threshold, whichever
    /// comes first.
    /// </summary>
    public int DefaultMaxEntryCount { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the worker-level default deterministic, pure history reducer applied before
    /// continue-as-new. Agents inherit this value when <see cref="DurableAgentBuilder.HistoryReducer"/>
    /// is unset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This delegate is kept for in-process and unit-test use. For production durable workflows,
    /// prefer <see cref="DefaultHistoryReducerKey"/> which is serialized and survives the wire.
    /// If both are set, <see cref="DefaultHistoryReducerKey"/> takes precedence for the durable path.
    /// </para>
    /// <para>
    /// WARNING: This delegate is not serialized. It does not survive workflow start serialization
    /// and will be silently ignored at continue-as-new time in production durable workflows unless
    /// <see cref="DefaultHistoryReducerKey"/> is also configured.
    /// </para>
    /// </remarks>
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? DefaultHistoryReducer { get; set; }

    /// <summary>
    /// Gets or sets the worker-level default keyed-service key used to resolve the history-reducer
    /// delegate from DI. Agents inherit this value when
    /// <see cref="DurableAgentBuilder.HistoryReducerKey"/> is unset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Register the reducer in DI before calling <c>AddTemporalAgents</c>:
    /// <code>
    /// services.AddKeyedSingleton&lt;Func&lt;IList&lt;DurableSessionEntry&gt;, IList&lt;DurableSessionEntry&gt;&gt;&gt;(
    ///     "my-reducer", (sp, key) => history => history.TakeLast(50).ToList());
    /// opts.DefaultHistoryReducerKey = "my-reducer";
    /// </code>
    /// </para>
    /// <para>
    /// <b>Determinism requirement:</b> the registered delegate must be pure and deterministic.
    /// An implementation that changes behaviour between deployments without a key change is a
    /// nondeterminism hazard for in-flight sessions.
    /// </para>
    /// </remarks>
    public string? DefaultHistoryReducerKey { get; set; }

    /// <summary>
    /// Gets or sets the worker-level default interceptor factory. Accepts any factory that
    /// produces an <see cref="IDurableToolInterceptor{TContext}"/> for
    /// <see cref="AgentToolContext"/> — including factories that return
    /// <see cref="IAgentToolInterceptor"/> (a sub-interface) or any interceptor implementing
    /// <c>IDurableToolInterceptor&lt;DurableToolContext&gt;</c> (assignable via contravariance).
    /// When a per-agent interceptor is not registered via
    /// <see cref="DurableAgentBuilder.AddToolInterceptor"/>, the agent inherits this value.
    /// The H1 rule applies: per-agent registration always wins over this worker default.
    /// </summary>
    /// <remarks>
    /// The factory is invoked from the activity's scoped service provider for every activity
    /// attempt.
    /// </remarks>
    public Func<IServiceProvider, IDurableToolInterceptor<AgentToolContext>>? DefaultToolInterceptor { get; set; }

    /// <summary>
    /// Gets or sets the worker-level default agent-pipeline configuration callback. When an agent
    /// does not set <see cref="DurableAgentBuilder.ConfigureAgentPipeline"/>, it inherits this value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback receives an <see cref="AIAgentBuilder"/> the user can compose with
    /// <c>UseOpenTelemetry()</c>, <c>UseLogging()</c>, or custom <see cref="DelegatingAIAgent"/>
    /// decorators. The pipeline is materialized at activity dispatch time and wraps the
    /// <see cref="ChatClientAgent"/> the library constructs internally.
    /// </para>
    /// <para>
    /// Custom wrapper classes must derive from <see cref="DelegatingAIAgent"/>, pass the factory's
    /// supplied <c>inner</c> agent to their base constructor, and preserve that chain. Returning an
    /// unrelated agent or an opaque <see cref="AIAgent"/> wrapper is rejected at validation or
    /// activity dispatch because its durable leaf cannot be verified.
    /// </para>
    /// <code>
    /// pipeline.Use(_ => unrelatedAgent);                 // rejected: replaces inner
    /// pipeline.Use(inner => new OpaqueAgent(inner));     // rejected: not DelegatingAIAgent
    /// </code>
    /// <para>
    /// The callback runs once per registered agent during startup validation and once for every
    /// live LLM-step activity attempt, including retries. Validation and live builds each receive
    /// a scoped service provider. Custom wrappers must be non-disposable; resolve resource-owning
    /// dependencies from that scope. The library owns and disposes MAF's built-in
    /// <see cref="OpenTelemetryAgent"/> wrapper.
    /// </para>
    /// <para>
    /// Live middleware receives the restored
    /// <see cref="TemporalCommunity.Extensions.Agents.Session.TemporalAgentSession"/>. It may make
    /// retry-safe <c>StateBag</c> changes but must forward the exact session instance. Replacing or
    /// removing it is rejected; middleware that requires the leaf's transient
    /// <c>ChatClientAgentSession</c> is unsupported.
    /// </para>
    /// </remarks>
    public Action<AIAgentBuilder>? DefaultConfigureAgentPipeline { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the startup C-check that validates user-supplied agent
    /// pipelines (via <see cref="DurableAgentBuilder.ConfigureAgentPipeline"/>) is skipped.
    /// Configuration failures are then deferred to the first-invocation B-check.
    /// </summary>
    /// <remarks>
    /// This flag is internal — intended for test scenarios that need to bypass the dry-run
    /// validation (for example, to exercise the B-check fallback path). Production users should
    /// not need to set this; the C-check is designed to fail loudly only when the configuration
    /// is genuinely invalid.
    /// </remarks>
    internal bool SkipDryRunCCheck { get; set; }

    /// <summary>
    /// Returns the internal durable-agent registration for the given name, or <see langword="null"/>
    /// if no agent with that name is registered (or it's a proxy-only declaration). Internal to
    /// the library — exposed for validator and registrar plumbing.
    /// </summary>
    internal DurableAgentRegistration? TryGetDurableRegistration(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _durableAgentRegistrations.TryGetValue(name, out var registration) ? registration : null;
    }

    /// <summary>
    /// Registers a durable agent and returns this options instance for chaining. The configure
    /// delegate populates a <see cref="DurableAgentBuilder"/>. Tool factories are evaluated when
    /// the worker builds its immutable agent blueprint; chat-client and context-provider factories
    /// are evaluated from the scoped provider for each activity attempt.
    /// </summary>
    /// <param name="name">
    /// Case-insensitive agent name. Must be unique within this options instance.
    /// </param>
    /// <param name="configure">
    /// Builder callback invoked synchronously during this method. Must assign
    /// <see cref="DurableAgentBuilder.ChatClient"/> before returning, otherwise this method throws
    /// <see cref="InvalidOperationException"/>.
    /// </param>
    /// <returns>This options instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null/empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="name"/> is already registered, or when the configure delegate
    /// completed without assigning <see cref="DurableAgentBuilder.ChatClient"/>.
    /// </exception>
    public TemporalAgentsOptions AddDurableAgent(string name, Action<DurableAgentBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        if (_durableAgentRegistrations.ContainsKey(name) || _proxyDeclarations.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"An agent with name '{name}' has already been registered. Agent names must be unique " +
                "across AddDurableAgent and AddAgentProxy.");
        }

        var builder = new DurableAgentBuilder(name);
        configure(builder);

        if (builder.ChatClient is null)
        {
            throw new InvalidOperationException(
                $"DurableAgent '{name}' requires ChatClient. Set agent.ChatClient = sp => sp.GetRequiredService<IChatClient>() in the configure delegate.");
        }

        var registration = builder.ToRegistration();
        _durableAgentRegistrations.Add(name, registration);

        return this;
    }

    /// <summary>
    /// Declares a named agent proxy for client-only scenarios where the real agent
    /// implementation runs in a separate worker process. No factory is required; call this from
    /// <see cref="ServiceCollectionExtensions.AddTemporalAgentProxies"/> instead of
    /// <see cref="AddDurableAgent(string, Action{DurableAgentBuilder})"/>.
    /// </summary>
    /// <param name="name">
    /// Case-insensitive agent name that must match the name used by the remote worker.
    /// Must be unique within this options instance.
    /// </param>
    /// <param name="timeToLive">
    /// Per-agent session TTL used when the proxy starts a new workflow. When null,
    /// <see cref="DefaultTimeToLive"/> is used.
    /// </param>
    /// <returns>This options instance, for fluent chaining.</returns>
    public TemporalAgentsOptions AddAgentProxy(string name, TimeSpan? timeToLive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_durableAgentRegistrations.ContainsKey(name) || _proxyDeclarations.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"An agent with name '{name}' has already been registered. Agent names must be unique " +
                "across AddDurableAgent and AddAgentProxy.");
        }

        _proxyDeclarations.Add(name, timeToLive);
        return this;
    }

    /// <summary>
    /// Registers a scheduled agent run that is created with Temporal at worker startup.
    /// </summary>
    /// <param name="agentName">Name of the agent to invoke on each schedule tick.</param>
    /// <param name="scheduleId">
    /// Unique schedule identifier. If a schedule with this ID already exists on startup,
    /// a warning is logged and the existing schedule is left unchanged.
    /// </param>
    /// <param name="request">The request to send to the agent on each scheduled run.</param>
    /// <param name="spec">When and how often the schedule fires.</param>
    /// <param name="policy">Overlap and catchup policy. Defaults to <see cref="SchedulePolicy"/> defaults.</param>
    public TemporalAgentsOptions AddScheduledAgentRun(
        string agentName,
        string scheduleId,
        RunRequest request,
        ScheduleSpec spec,
        SchedulePolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);

        _scheduledRuns.Add(new ScheduleAgentRegistration(agentName, scheduleId, request, spec, policy));
        return this;
    }

    /// <summary>Gets all registered scheduled runs for use by <see cref="ScheduleRegistrationService"/>.</summary>
    internal IReadOnlyList<ScheduleAgentRegistration> GetScheduledRuns() => _scheduledRuns;

    // ── Agent Registry (read-only introspection) ──────────────────────────

    /// <summary>
    /// Returns the names of all registered agents (durable and proxy), in registration order.
    /// </summary>
    public IReadOnlyList<string> GetRegisteredAgentNames() =>
        [.. _durableAgentRegistrations.Keys, .. _proxyDeclarations.Keys];

    /// <summary>
    /// Returns <see langword="true"/> if an agent with the given name is registered.
    /// The check is case-insensitive.
    /// </summary>
    public bool IsAgentRegistered(string name) =>
        !string.IsNullOrEmpty(name)
        && (_durableAgentRegistrations.ContainsKey(name) || _proxyDeclarations.ContainsKey(name));

    /// <summary>
    /// Returns descriptors for all registered durable agents that have a description.
    /// Use this in routing activities to build an LLM dispatch prompt.
    /// </summary>
    public IReadOnlyList<AgentDescriptor> GetAgentDescriptors() =>
        [.. _durableAgentRegistrations
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value.Description))
            .Select(kvp => new AgentDescriptor(kvp.Key, kvp.Value.Description!))];

    /// <summary>
    /// Returns the description for the given agent, or <see langword="null"/> if the agent
    /// has no description or is not registered. The lookup is case-insensitive.
    /// </summary>
    public string? GetAgentDescription(string agentName)
    {
        if (string.IsNullOrEmpty(agentName))
        {
            return null;
        }

        return _durableAgentRegistrations.TryGetValue(agentName, out var reg)
            ? reg.Description
            : null;
    }

    /// <summary>
    /// Gets the durable-agent registrations. Empty when no <see cref="AddDurableAgent"/> calls
    /// have been made.
    /// </summary>
    internal IReadOnlyDictionary<string, DurableAgentRegistration> DurableAgentRegistrations =>
        _durableAgentRegistrations;

    /// <summary>Gets the proxy-only declarations.</summary>
    internal IReadOnlyDictionary<string, TimeSpan?> ProxyDeclarations => _proxyDeclarations;

    /// <summary>
    /// Gets the resolved TTL for a specific agent. Per-agent value (durable registration or proxy
    /// declaration) wins; otherwise falls back to <see cref="DefaultTimeToLive"/>.
    /// </summary>
    internal TimeSpan? GetTimeToLive(string agentName)
    {
        if (_durableAgentRegistrations.TryGetValue(agentName, out var reg) && reg.TimeToLive.HasValue)
        {
            return reg.TimeToLive;
        }

        if (_proxyDeclarations.TryGetValue(agentName, out var proxyTtl) && proxyTtl.HasValue)
        {
            return proxyTtl;
        }

        return DefaultTimeToLive;
    }
}
