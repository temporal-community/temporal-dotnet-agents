#pragma warning disable TA001 // IDurableToolSource is experimental; intentional consumption in builder registration path
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Internal carrier for a tool registered on a <see cref="DurableAgentBuilder"/>. The factory is
/// invoked while the immutable agent blueprint is first built from the worker's root provider;
/// the resolved <see cref="AIFunction"/> is cached for the lifetime of the worker.
/// </summary>
internal sealed record DurableToolRegistration(
    string Name,
    Func<IServiceProvider, AIFunction> Factory,
    DurableToolOptions Options)
{
    public TemporalMemorySearchRegistration? MemorySearch { get; init; }
}

/// <summary>
/// Fluent builder for registering a durable agent via <c>TemporalAgentsOptions.AddDurableAgent</c>.
/// Properties capture per-agent scalars; <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/> and
/// <c>AddContextProvider</c> capture per-agent collections.
/// </summary>
/// <remarks>
/// <para>
/// Per-agent scalar settings (timeouts, retry policy, max entry count, etc.) default to <see langword="null"/>
/// and inherit the corresponding worker-level value from <see cref="TemporalAgentsOptions"/> when unset.
/// <see cref="MaxToolCallsPerTurn"/> is the only per-agent setting with a built-in default
/// (<c>20</c>) — there is no worker-level fallback for it.
/// </para>
/// <para>
/// All <c>Add*</c> methods return the builder so configuration can be expressed fluently; using the
/// property setters directly is also fully supported.
/// </para>
/// <para>
/// <b>Factory composition lifecycle.</b> Tool factories registered through
/// <see cref="AddTool(string, Func{IServiceProvider, AIFunction}, Action{DurableToolOptions}?)"/>
/// run once while the immutable worker blueprint is built and must resolve only worker-lifetime-safe
/// services. In contrast, <see cref="ChatClient"/>,
/// <see cref="AddContextProvider(Func{IServiceProvider, AIContextProvider})"/>,
/// tool-interceptor factories are invoked from a fresh
/// <see cref="IServiceScope"/> for every activity attempt. They may resolve scoped services;
/// they must not retain per-session state in process fields because an activity can retry, move to
/// another worker, or run concurrently with another session.
/// </para>
/// </remarks>
public sealed class DurableAgentBuilder
{
    // Tools are stored in registration order; names are case-insensitive (consistent with
    // TemporalAgentsOptions agent-name handling).
    private readonly List<DurableToolRegistration> _tools = new();
    private readonly HashSet<string> _toolNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ContextProviderRegistration> _contextProviders = new();
    private readonly List<(string ToolName, string SourceProviderType)> _providerContributedTools = new();
    private Func<IServiceProvider, IDurableToolInterceptor<AgentToolContext>>? _toolInterceptorFactory;
    private bool _hasTemporalMemorySearch;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableAgentBuilder"/> class with the given agent
    /// name. This constructor is internal — instances are produced by
    /// <c>TemporalAgentsOptions.AddDurableAgent</c>.
    /// </summary>
    /// <param name="name">The case-insensitive agent name. Must be non-null and non-whitespace.</param>
    internal DurableAgentBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>
    /// Gets the agent name. Immutable for the life of the builder.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets a human-readable description of the agent. When set, the agent appears in
    /// <c>TemporalAgentsOptions.GetAgentDescriptors()</c> for use in routing prompts.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the agent's system instructions. When set, the library stamps these onto every
    /// LLM call's <see cref="ChatOptions.Instructions"/> regardless of what is set on
    /// <see cref="ChatOptions"/>.
    /// </summary>
    /// <remarks>
    /// Optional. Tool-only agents (no system prompt) are supported by leaving this <see langword="null"/>.
    /// </remarks>
    public string? Instructions { get; set; }

    /// <summary>
    /// Gets or sets the factory used to obtain the agent's <see cref="IChatClient"/>. The factory is
    /// invoked from the activity's scoped service provider for every LLM-step activity attempt.
    /// </summary>
    /// <remarks>
    /// Required at composition time. Registration with a <see langword="null"/> chat client throws
    /// at the end of the configure delegate.
    /// </remarks>
    public Func<IServiceProvider, IChatClient>? ChatClient { get; set; }

    /// <summary>
    /// Gets or sets a template <see cref="ChatOptions"/> instance applied to every LLM call. Use
    /// for LLM-call settings such as <see cref="ChatOptions.Temperature"/>,
    /// <see cref="ChatOptions.ResponseFormat"/>, <see cref="ChatOptions.MaxOutputTokens"/>, etc.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ChatOptions.Tools"/> and <see cref="ChatOptions.Instructions"/> set on this
    /// property are ignored. The agent's tools come from <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/>
    /// calls; the agent's instructions come from the <see cref="Instructions"/> property. Use
    /// <see cref="ChatOptions"/> for LLM-call settings only (Temperature, ResponseFormat,
    /// MaxOutputTokens, etc.).
    /// </para>
    /// </remarks>
    public ChatOptions? ChatOptions { get; set; }

    /// <summary>
    /// Gets or sets the per-agent session TTL. When <see langword="null"/>, inherits the worker-level
    /// <c>TemporalAgentsOptions.DefaultTimeToLive</c>.
    /// </summary>
    public TimeSpan? TimeToLive { get; set; }

    /// <summary>
    /// Gets or sets the per-agent maximum time the workflow waits for a human approval response.
    /// When <see langword="null"/>, inherits the worker-level <c>TemporalAgentsOptions.DefaultApprovalTimeout</c>.
    /// </summary>
    public TimeSpan? ApprovalTimeout { get; set; }

    /// <summary>
    /// Gets or sets the per-agent activity start-to-close timeout used for the
    /// <c>RunAgentStep</c> activity. When <see langword="null"/>, inherits the worker-level
    /// <c>TemporalAgentsOptions.DefaultActivityTimeout</c>.
    /// </summary>
    public TimeSpan? ActivityTimeout { get; set; }

    /// <summary>
    /// Gets or sets the per-agent activity heartbeat timeout used for the <c>RunAgentStep</c>
    /// activity. When <see langword="null"/>, inherits the worker-level
    /// <c>TemporalAgentsOptions.DefaultHeartbeatTimeout</c>.
    /// </summary>
    public TimeSpan? HeartbeatTimeout { get; set; }

    /// <summary>
    /// Gets or sets the retry policy applied to this agent's <c>RunAgentStep</c> activity (the LLM
    /// call). Per-tool retry policies are configured via
    /// <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/>.
    /// When <see langword="null"/>, inherits the worker-level <c>TemporalAgentsOptions.DefaultRetryPolicy</c>.
    /// </summary>
    /// <remarks>
    /// This policy applies to the LLM step only — it does not cascade to per-tool activity dispatches.
    /// Configure tool retries individually via <see cref="DurableToolOptions"/> (typically
    /// <see cref="DurableToolOptions.NoRetry"/> for non-idempotent write tools).
    /// </remarks>
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the per-agent maximum number of <see cref="DurableSessionEntry"/> instances
    /// retained before triggering continue-as-new. When <see langword="null"/>, inherits the
    /// worker-level <c>TemporalAgentsOptions.DefaultMaxEntryCount</c>.
    /// </summary>
    public int? MaxEntryCount { get; set; }

    /// <summary>
    /// Gets or sets this agent's maximum estimated model-context size in tokens, including its
    /// output reserve. When unset, inherits the worker-level limit.
    /// </summary>
    public int? MaxModelContextTokens { get; set; }

    /// <summary>
    /// Gets or sets this agent's model-output token reserve. When unset, inherits the worker-level
    /// reserve.
    /// </summary>
    public int? ModelOutputTokenReserve { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of LLM-step iterations within a single agent turn. Each
    /// iteration may dispatch a serial batch of tool activities. When the cap is exceeded the
    /// workflow returns a structured error response. Defaults to <c>20</c>.
    /// </summary>
    /// <remarks>
    /// There is no worker-level fallback — every agent uses the value set on its builder (or the
    /// default <c>20</c>).
    /// </remarks>
    public int MaxToolCallsPerTurn { get; set; } = 20;

    /// <summary>
    /// Gets or sets the keyed-service key used to resolve the history-reducer delegate from DI.
    /// When non-null, the session client sets this key on the workflow input and the worker
    /// dispatches a <c>ReduceHistoryByKey</c> activity at continue-as-new time. Inherits
    /// <see cref="TemporalAgentsOptions.DefaultHistoryReducerKey"/> when <see langword="null"/>.
    /// </summary>
    public string? HistoryReducerKey { get; set; }

    /// <summary>
    /// Gets or sets the keyed worker-service key for an MEAI <see cref="IChatReducer"/> that
    /// shapes eligible prior-turn model context without changing retained session history.
    /// When unset, the worker-level <see cref="TemporalAgentsOptions.DefaultContextReducerKey"/>
    /// is used.
    /// </summary>
    public string? ContextReducerKey { get; set; }

    /// <summary>
    /// Gets or sets a callback that configures an <see cref="AIAgentBuilder"/> middleware pipeline
    /// wrapping the library-constructed <see cref="ChatClientAgent"/>. Inherits the worker-level
    /// <c>TemporalAgentsOptions.DefaultConfigureAgentPipeline</c> when <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback builds model-step middleware at activity dispatch time, such as
    /// <c>UseOpenTelemetry()</c>, <c>UseLogging()</c>, or custom <see cref="DelegatingAIAgent"/>
    /// decorators. The library composes the inner <see cref="ChatClientAgent"/>; user code only
    /// adds decorators around it via the supplied <see cref="AIAgentBuilder"/>.
    /// </para>
    /// <para>
    /// Custom wrapper classes must derive from <see cref="DelegatingAIAgent"/>, pass the factory's
    /// supplied <c>inner</c> agent to their base constructor, and preserve that chain. A factory
    /// that returns an unrelated agent, or an opaque <see cref="AIAgent"/> subclass that privately
    /// forwards calls, is rejected because the library cannot verify the durable leaf.
    /// </para>
    /// <code>
    /// pipeline.Use(_ => unrelatedAgent);                 // rejected: replaces inner
    /// pipeline.Use(inner => new OpaqueAgent(inner));     // rejected: not DelegatingAIAgent
    /// </code>
    /// <para>
    /// <b>Construction and lifetime contract.</b> The callback runs once during startup validation
    /// and once for every <c>RunDurableAgentStep</c> activity attempt, including retries.
    /// Provider preparation and completion also build a pipeline to supply their agent context,
    /// without invoking its run middleware. Each
    /// live build uses that activity's scoped service provider. Custom wrappers must not implement
    /// <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/> because MAF does not expose
    /// factory ownership; inject resource-owning dependencies from the activity scope instead.
    /// The library owns and disposes MAF's built-in <see cref="OpenTelemetryAgent"/> wrapper.
    /// </para>
    /// <para>
    /// During a live activity, outer middleware receives the exact restored
    /// <see cref="TemporalCommunity.Extensions.Agents.Session.TemporalAgentSession"/> and may
    /// update its <c>StateBag</c> with retry-safe state. It must forward that exact session
    /// instance. Replacing it or passing <see langword="null"/> is rejected. The innermost
    /// library boundary translates only the <see cref="ChatClientAgent"/> leaf to its transient
    /// sealed session type; middleware that requires <c>ChatClientAgentSession</c> is unsupported.
    /// </para>
    /// <para>
    /// <b>Forbidden middleware.</b> Calling <c>.Use(funcInvocationCallback)</c> (the agent-side
    /// equivalent of <c>FunctionInvokingChatClient</c>) inside this callback is rejected at
    /// worker startup with <see cref="TemporalCommunity.Extensions.AI.Exceptions.DurableFunctionInvocationConflictException"/>
    /// — the durable libraries handle tool invocation as separate Temporal activities, and
    /// in-pipeline function-invocation middleware would conflict with that contract.
    /// </para>
    /// </remarks>
    public Action<AIAgentBuilder>? ConfigureAgentPipeline { get; set; }

    internal Func<AIAgent, FunctionInvocationContext,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
        CancellationToken, ValueTask<object?>>? ToolMiddleware { get; set; }

    /// <summary>
    /// Registers a concrete <see cref="AIFunction"/> as a tool for this agent. The tool's
    /// <see cref="AIFunction.Name"/> must be unique within this agent.
    /// </summary>
    /// <param name="tool">The tool instance.</param>
    /// <param name="configure">Optional configuration callback for per-tool activity overrides.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="tool"/> has a null/empty <see cref="AIFunction.Name"/>, or when a
    /// tool with the same name has already been registered on this agent.
    /// </exception>
    public DurableAgentBuilder AddTool(AIFunction tool, Action<DurableToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new ArgumentException(
                "Tool must have a non-null, non-empty, non-whitespace Name.",
                nameof(tool));
        }

        AddToolCore(tool.Name, _ => tool, configure);
        return this;
    }

    /// <summary>
    /// Registers a tool produced by a factory. The factory is invoked once, at first activity
    /// dispatch, and the resolved <see cref="AIFunction"/> is cached for the worker's lifetime.
    /// This is <em>not</em> the same lifecycle as <see cref="ChatClient"/>, which is resolved
    /// afresh from the activity attempt's scope on every LLM step.
    /// </summary>
    /// <param name="name">
    /// The tool name. Must be non-null and non-whitespace, and unique within this agent. Required as
    /// an explicit parameter so duplicate-name detection happens synchronously at registration —
    /// without it, the duplicate check would be deferred to first dispatch when the factory runs.
    /// </param>
    /// <param name="factory">
    /// Factory that produces the <see cref="AIFunction"/>. Receives the worker's root
    /// <see cref="IServiceProvider"/> — any service you resolve inside this factory is held for
    /// the worker's lifetime (singleton semantics), regardless of how it was registered in DI.
    /// If you need per-call scoped resolution inside the tool body itself, resolve services via
    /// <c>TemporalAgentContext.Current.GetService&lt;T&gt;()</c> at invocation time rather than
    /// capturing them in the factory. That reads from the scope the <c>InvokeAgentTool</c> activity
    /// opens for the call, so a scoped registration behaves as scoped.
    /// </param>
    /// <param name="configure">Optional configuration callback for per-tool activity overrides.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="name"/> is null/empty, or when a tool with the same name has
    /// already been registered on this agent.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    public DurableAgentBuilder AddTool(string name, Func<IServiceProvider, AIFunction> factory, Action<DurableToolOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        AddToolCore(name, factory, configure);
        return this;
    }

    /// <summary>
    /// Registers multiple concrete tools at once. Equivalent to calling
    /// <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/> for each entry, in order.
    /// </summary>
    /// <param name="tools">One or more <see cref="AIFunction"/> instances to register.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="tools"/> is <see langword="null"/> or contains a <see langword="null"/> entry.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when any entry has a null/empty <see cref="AIFunction.Name"/>, or duplicates a name
    /// already registered on this agent.
    /// </exception>
    public DurableAgentBuilder AddTools(params AIFunction[] tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return AddTools((IEnumerable<AIFunction>)tools);
    }

    /// <summary>
    /// Registers an ordered sequence of concrete tools. The sequence is enumerated exactly once.
    /// </summary>
    /// <param name="tools">The tools to register. An empty sequence is a no-op.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    public DurableAgentBuilder AddTools(IEnumerable<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (var tool in tools)
        {
            AddTool(tool);
        }

        return this;
    }

    /// <summary>
    /// Registers a concrete <see cref="AIContextProvider"/> for this agent, with optional durable
    /// tool specs that are registered as separate Temporal activities at the same time.
    /// </summary>
    /// <param name="provider">The provider instance.</param>
    /// <param name="durableTools">
    /// Optional collection of <see cref="DurableToolRegistrationSpec"/> entries contributed by
    /// this provider. When supplied, the provider is transparently wrapped in a
    /// <c>DurableContextProviderWrapper</c> that implements <see cref="IDurableToolSource"/>.
    /// Each spec is registered via <c>AddTool</c> (case-insensitive uniqueness is enforced —
    /// an <see cref="ArgumentException"/> is thrown on collision with any tool already registered
    /// via <c>agent.AddTool()</c> or another <c>AddContextProvider</c> call).
    /// </param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="provider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when any spec's tool name collides with a name already registered on this agent.
    /// Check both <c>agent.AddTool()</c> calls and <c>DurableToolRegistrationSpec</c> entries
    /// in <c>AddContextProvider</c>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// In managed session workflows, <c>InvokingAsync</c> prepares context once before the
    /// model/tool loop and <c>InvokedAsync</c> runs at terminal turn completion. Activity retries
    /// may repeat either hook.
    /// </para>
    /// <para>
    /// Registered tools are seeded into provider context. Providers may select or filter only
    /// those exact registered function instances. Provider-created functions and wrappers fail
    /// before the first model call.
    /// </para>
    /// <para>
    /// Tool activities use one attempt by default. Setting this agent's
    /// <see cref="RetryPolicy"/> opts every tool on the agent into retries; do so only when all
    /// tools are safe to repeat or use downstream idempotency keys.
    /// </para>
    /// </remarks>
    public DurableAgentBuilder AddContextProvider(
        AIContextProvider provider,
        IEnumerable<DurableToolRegistrationSpec>? durableTools = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        AIContextProvider registered = provider;

        // Collect provider-contributed tool specs at registration time.
        IReadOnlyList<DurableToolRegistrationSpec>? specs = null;

        // Materialise once to avoid double-enumeration of a single-pass IEnumerable.
        var specsList = durableTools?.ToList();
        if (specsList is { Count: > 0 })
        {
            // Explicit specs supplied — wrap the provider so it acts as IDurableToolSource.
            specs = specsList;
            registered = new DurableContextProviderWrapper(provider, specs);
        }
        else if (provider is IDurableToolSource source)
        {
            // Provider self-declares its tools.
            var extracted = source.GetDurableTools();
            if (extracted?.Count > 0)
                specs = extracted;
        }

        // Register provider-contributed tools via AddToolCore (same path as AddTool —
        // enforces case-insensitive uniqueness, throws ArgumentException on collision).
        // Collision error message names both AddTool and AddContextProvider as possible sources.
        if (specs is not null)
        {
            foreach (var spec in specs)
                AddToolCore(
                    spec.Tool.Name,
                    _ => spec.Tool,
                    spec.Configure,
                    sourceHint: $"DurableToolRegistrationSpec in AddContextProvider (provider: {provider.GetType().Name})");

            // Record for audit logging when the agent blueprint is built.
            _providerContributedTools.AddRange(
                specs.Select(s => (s.Tool.Name, provider.GetType().Name)));
        }

        // Record that declarations were INSPECTED on the instance path, not that any were found.
        // An IDurableToolSource may legitimately declare none; treating that as "not registered"
        // made the runtime reject it as factory-registered and print advice about an overload the
        // caller never used.
        _contextProviders.Add(new ContextProviderRegistration(_ => registered, DeclarationsRegistered: true));
        return this;
    }

    /// <summary>
    /// Registers an <see cref="AIContextProvider"/> via a factory. The factory is invoked from a
    /// scoped service provider for provider preparation and terminal completion activity attempts
    /// in managed session workflows.
    /// </summary>
    /// <param name="factory">Factory that produces the <see cref="AIContextProvider"/>.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Managed session workflows prepare provider context once per logical turn and record the
    /// result; the completion hook runs at the terminal outcome. Activity retries can repeat
    /// either callback.
    /// A provider may be constructed again for an activity retry or on another worker; per-session
    /// state must live in the <c>StateBag</c>, and external effects must tolerate activity retry.
    /// </para>
    /// <para>
    /// Provider-returned tools are accepted in managed session workflows only when they are the
    /// exact registered function instances seeded into the MAF context. A changed tool set is
    /// frozen for the turn. Provider-created functions, wrappers, and same-name/schema substitutes
    /// fail before model dispatch. This narrow route does not support stock on-demand
    /// memory-search providers.
    /// </para>
    /// <para>
    /// A provider resolved by this factory does not exist at build time, so its
    /// <see cref="IDurableToolSource"/> declarations cannot be collected. If it implements that
    /// interface, the first preparation activity fails with a non-retryable configuration error;
    /// use the instance overload
    /// <c>AddContextProvider(AIContextProvider, IEnumerable{DurableToolRegistrationSpec}?)</c>
    /// for such a provider.
    /// </para>
    /// <para>
    /// A provider that returns tools from <c>InvokingAsync</c> must return the exact registered
    /// function objects; arbitrary returned tools are rejected before model dispatch.
    /// </para>
    /// </remarks>
    public DurableAgentBuilder AddContextProvider(Func<IServiceProvider, AIContextProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        // A factory cannot have its IDurableToolSource declarations collected here — the provider
        // does not exist yet. The activity rejects that combination rather than dropping tools.
        _contextProviders.Add(new ContextProviderRegistration(factory, DeclarationsRegistered: false));
        return this;
    }

    internal DurableAgentBuilder AddTemporalMemorySearchBridge(
        TemporalMemorySearchRegistration bridge,
        string toolDescription,
        Action<DurableToolOptions>? configureTool = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolDescription);
        if (bridge.ProviderFactory is null || string.IsNullOrWhiteSpace(bridge.CollectionName))
        {
            throw new ArgumentException("Memory-search provider and collection must be configured.", nameof(bridge));
        }
        if (string.IsNullOrWhiteSpace(bridge.ToolName))
        {
            throw new ArgumentException("Memory-search tool names cannot be empty.", nameof(bridge));
        }
        if (_hasTemporalMemorySearch)
        {
            throw new ArgumentException($"Agent '{Name}' can register only one Temporal memory-search bridge.");
        }

        if (bridge.SearchTime == ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling)
        {
            AddToolCore(
                bridge.ToolName,
                _ => CreateMemorySearchPlaceholder(bridge.ToolName, toolDescription),
                configureTool,
                sourceHint: "Temporal memory-search bridge");
            _tools[^1] = _tools[^1] with { MemorySearch = bridge };
        }

        _contextProviders.Add(new ContextProviderRegistration(
            services => bridge.ProviderFactory(services),
            DeclarationsRegistered: true,
            MemorySearch: bridge));
        _hasTemporalMemorySearch = true;
        return this;
    }

    private static AIFunction CreateMemorySearchPlaceholder(string name, string description) =>
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<string>>)UnavailableMemorySearchAsync,
            name,
            description);

    private static Task<string> UnavailableMemorySearchAsync(
        string userQuestion,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Temporal memory-search functions are invoked only through the durable activity route.");

    /// <summary>
    /// Registers a per-agent interceptor factory. The factory is invoked from the activity's scoped
    /// service provider for every activity attempt. Per-agent interceptor wins over
    /// <see cref="TemporalAgentsOptions.DefaultToolInterceptor"/> (H1 rule).
    /// </summary>
    /// <remarks>
    /// Accepts any factory whose return type is assignable to
    /// <c>IDurableToolInterceptor&lt;AgentToolContext&gt;</c>. That includes:
    /// <list type="bullet">
    ///   <item><see cref="IAgentToolInterceptor"/> implementations (the MAF-specific sub-interface)</item>
    ///   <item>Types implementing <c>IDurableToolInterceptor&lt;<see cref="DurableToolContext"/>&gt;</c>
    ///   directly — contravariance makes them assignable to the <see cref="AgentToolContext"/> slot</item>
    /// </list>
    /// </remarks>
    /// <param name="factory">Factory that produces the interceptor.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    public DurableAgentBuilder AddToolInterceptor(
        Func<IServiceProvider, IDurableToolInterceptor<AgentToolContext>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _toolInterceptorFactory = factory;
        return this;
    }

    /// <summary>Internal accessor for Phase 2 registration plumbing.</summary>
    internal IReadOnlyList<DurableToolRegistration> ToolRegistrations => _tools;

    /// <summary>Internal accessor for Phase 2 registration plumbing.</summary>
    internal IReadOnlyList<ContextProviderRegistration> ContextProviderFactories => _contextProviders;

    /// <summary>
    /// Produces an immutable <see cref="DurableAgentRegistration"/> snapshot of this builder. Called
    /// by <c>TemporalAgentsOptions.AddDurableAgent</c> after the configure delegate completes.
    /// </summary>
    /// <returns>The flattened registration record.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="ChatClient"/> is <see langword="null"/>.
    /// </exception>
    internal DurableAgentRegistration ToRegistration()
    {
        if (ChatClient is null)
        {
            throw new InvalidOperationException(
                $"DurableAgentBuilder for agent '{Name}' has no ChatClient set. Assign agent.ChatClient = sp => ... in the configure delegate.");
        }

        // Validate loop / history bounds. A non-positive MaxToolCallsPerTurn makes the
        // dispatch loop body never run, so the agent returns "iterations exceeded" without
        // ever calling the LLM. MaxEntryCount is nullable (null = inherit the worker-level
        // default), so only a non-null, non-positive value is invalid.
        if (MaxToolCallsPerTurn <= 0)
        {
            throw new InvalidOperationException(
                $"DurableAgentBuilder.MaxToolCallsPerTurn for agent '{Name}' must be a positive integer.");
        }

        if (MaxEntryCount is < 4)
        {
            throw new InvalidOperationException(
                $"DurableAgentBuilder.MaxEntryCount for agent '{Name}' must be at least 4 when set (null inherits the worker-level default) to retain a complete request/response turn.");
        }

        if (MaxModelContextTokens is <= 0
            || ModelOutputTokenReserve is < 0
            || (MaxModelContextTokens is { } modelLimit
                && ModelOutputTokenReserve is { } outputReserve
                && outputReserve >= modelLimit))
        {
            throw new InvalidOperationException(
                $"DurableAgentBuilder model-context token budget for agent '{Name}' is invalid.");
        }

        return new DurableAgentRegistration(
            Name: Name,
            Description: Description,
            Instructions: Instructions,
            ChatClient: ChatClient,
            ChatOptions: ChatOptions,
            Tools: _tools.ToArray(),
            ContextProviderFactories: _contextProviders.ToArray(),
            TimeToLive: TimeToLive,
            ApprovalTimeout: ApprovalTimeout,
            ActivityTimeout: ActivityTimeout,
            HeartbeatTimeout: HeartbeatTimeout,
            RetryPolicy: RetryPolicy,
            MaxEntryCount: MaxEntryCount,
            MaxModelContextTokens: MaxModelContextTokens,
            ModelOutputTokenReserve: ModelOutputTokenReserve,
            MaxToolCallsPerTurn: MaxToolCallsPerTurn,
            HistoryReducerKey: HistoryReducerKey,
            ContextReducerKey: ContextReducerKey,
            ConfigureAgentPipeline: ConfigureAgentPipeline,
            ToolMiddleware: ToolMiddleware,
            ToolInterceptorFactory: _toolInterceptorFactory,
            ProviderContributedTools: _providerContributedTools.Count > 0
                ? _providerContributedTools.ToArray()
                : null);
    }

    private void AddToolCore(
        string name,
        Func<IServiceProvider, AIFunction> factory,
        Action<DurableToolOptions>? configure,
        string? sourceHint = null)
    {
        if (_toolNames.Contains(name))
        {
            var source = sourceHint is null
                ? $"Tool '{name}' is already registered on agent '{Name}'."
                : $"Tool '{name}' is already registered on agent '{Name}'. " +
                  $"Source of duplicate: {sourceHint}. " +
                  "Check both agent.AddTool() calls and DurableToolRegistrationSpec entries in AddContextProvider.";
            throw new ArgumentException(source, nameof(name));
        }

        var options = new DurableToolOptions();
        configure?.Invoke(options);
        _toolNames.Add(name);
        _tools.Add(new DurableToolRegistration(name, factory, options));
    }
}
