#pragma warning disable TA001 // IDurableToolSource is experimental; intentional consumption in builder registration path
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Skills;
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
    DurableToolOptions Options);

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
    private readonly List<Func<IServiceProvider, AIContextProvider>> _contextProviders = new();
    private readonly List<(string ToolName, string SourceProviderType)> _providerContributedTools = new();
    private Func<IServiceProvider, IDurableToolInterceptor<AgentToolContext>>? _toolInterceptorFactory;
    private bool _useApprovalScopes;
    private ApprovalScopesOptions? _approvalScopesOptions;

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
    /// Gets or sets the maximum number of LLM-step iterations within a single agent turn. Each
    /// iteration may dispatch a parallel batch of tool activities. When the cap is exceeded the
    /// workflow returns a structured error response. Defaults to <c>20</c>.
    /// </summary>
    /// <remarks>
    /// There is no worker-level fallback — every agent uses the value set on its builder (or the
    /// default <c>20</c>).
    /// </remarks>
    public int MaxToolCallsPerTurn { get; set; } = 20;

    /// <summary>
    /// Gets or sets a deterministic, pure reducer applied to the agent's accumulated history before
    /// continue-as-new. Kept for in-process and unit-test use. For production durable workflows,
    /// prefer <see cref="HistoryReducerKey"/> which is serialized and survives the wire.
    /// </summary>
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer { get; set; }

    /// <summary>
    /// Gets or sets the keyed-service key used to resolve the history-reducer delegate from DI.
    /// When non-null, the session client sets this key on the workflow input and the worker
    /// dispatches a <c>ReduceHistoryByKey</c> activity at continue-as-new time. Inherits
    /// <see cref="TemporalAgentsOptions.DefaultHistoryReducerKey"/> when <see langword="null"/>.
    /// </summary>
    public string? HistoryReducerKey { get; set; }

    /// <summary>
    /// Gets or sets a callback that configures an <see cref="AIAgentBuilder"/> middleware pipeline
    /// wrapping the library-constructed <see cref="ChatClientAgent"/>. Inherits the worker-level
    /// <c>TemporalAgentsOptions.DefaultConfigureAgentPipeline</c> when <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback runs at activity dispatch time and lets users compose middleware such as
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
    /// and once for every <c>RunDurableAgentStep</c> activity attempt, including retries. Each
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
    /// Registers a tool produced by a factory. The factory is invoked at first activity dispatch
    /// (the same lifecycle as <see cref="ChatClient"/>) and the resolved <see cref="AIFunction"/>
    /// is cached for the worker's lifetime.
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
    /// <c>TemporalAgentContext.Current?.Services</c> at invocation time rather than capturing
    /// them in the factory.
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
    /// In durable agents, the provider's <c>InvokingAsync</c> and <c>InvokedAsync</c> hooks fire
    /// once per LLM call (per <c>RunAgentStep</c> activity), not once per turn. Make these hooks
    /// idempotent and cheap, or cache results via <c>StateBag</c> to skip redundant work within a
    /// turn.
    /// </para>
    /// <para>
    /// When <paramref name="durableTools"/> is provided (or the provider implements
    /// <see cref="IDurableToolSource"/>), the per-iteration strip in <c>AgentActivities</c>
    /// automatically nulls out <c>AIContext.Tools</c> after this provider's <c>InvokingAsync</c>
    /// call, preventing downstream providers from seeing stale tool lists.
    /// </para>
    /// <para>
    /// <b>Non-idempotent tools MUST set <c>Configure = opts =&gt; opts.NoRetry()</c></b>
    /// to prevent double-execution on activity retry.
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

            // Record for audit logging at ComposeDurableAgent time.
            _providerContributedTools.AddRange(
                specs.Select(s => (s.Tool.Name, provider.GetType().Name)));
        }

        _contextProviders.Add(_ => registered);
        return this;
    }

    /// <summary>
    /// Registers an <see cref="AIContextProvider"/> via a factory. The factory is invoked from the
    /// activity's scoped service provider for every LLM-step activity attempt.
    /// </summary>
    /// <param name="factory">Factory that produces the <see cref="AIContextProvider"/>.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// In durable agents, the provider's <c>InvokingAsync</c> and <c>InvokedAsync</c> hooks fire
    /// once per LLM call (per <c>RunAgentStep</c> activity), not once per turn. Make these hooks
    /// idempotent and cheap, or cache results via <c>StateBag</c> to skip redundant work within a
    /// turn. A provider may be constructed again for an activity retry or on another worker;
    /// per-session state must live in the <c>StateBag</c>, and external effects must tolerate
    /// activity retry.
    /// </para>
    /// <para>
    /// <b>Tool definitions returned in <c>AIContext.Tools</c> by context providers are ignored</b>
    /// and will not be dispatched as durable activities. Providers that contribute tools
    /// (e.g., <c>HyperlightCodeActProvider</c>, <c>LocalCodeActProvider</c>,
    /// <c>TextSearchProvider</c> in on-demand mode, <c>AgentSkillsProvider</c>) are designed for
    /// MAF's in-process function-invocation harness; their tools are not compatible with
    /// per-tool durable activity dispatch. To register a tool with durable execution semantics,
    /// use <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/> instead. To register a
    /// tool alongside a context provider, use the instance overload
    /// <c>AddContextProvider(AIContextProvider, IEnumerable{DurableToolRegistrationSpec})</c> instead.
    /// </para>
    /// <para>
    /// If the provider resolved by this factory implements <see cref="IDurableToolSource"/> or
    /// returns tools from <c>InvokingAsync</c>, the framework cannot detect this at startup — the
    /// <c>LogError</c> fires only at the first workflow execution. If the provider contributes
    /// tools, prefer the instance overload
    /// <c>AddContextProvider(AIContextProvider, IEnumerable{DurableToolRegistrationSpec}?)</c>
    /// so tool registration can occur at startup.
    /// </para>
    /// </remarks>
    public DurableAgentBuilder AddContextProvider(Func<IServiceProvider, AIContextProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _contextProviders.Add(factory);
        return this;
    }

    /// <summary>
    /// Registers progressive-disclosure skills support for this agent. Internally creates a
    /// <see cref="SkillsContextProvider"/> (registered via <c>AddContextProvider(AIContextProvider)</c>)
    /// and the skill tools <c>load_skill</c>, <c>read_skill_resource</c>, and optionally
    /// <c>run_skill_script</c> (when <see cref="SkillsBuilder.EnableScriptExecution"/> is called).
    /// </summary>
    /// <param name="configure">Callback to register skills and configure options.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>How it works.</b> The skill index (name + description per skill, ~100 tokens per skill)
    /// is injected as a system message on every LLM call via <see cref="SkillsContextProvider"/>.
    /// The index is built on first use and cached in <c>AgentSessionStateBag["temporal.skills_index"]</c>
    /// so it survives continue-as-new transitions. Full skill content is loaded on demand by the
    /// <c>load_skill</c> tool — each invocation is a separate <c>InvokeAgentTool</c> Temporal
    /// activity visible in the Web UI.
    /// </para>
    /// <para>
    /// <b>Script execution.</b> Script support is disabled by default. Call
    /// <see cref="SkillsBuilder.EnableScriptExecution"/> to register <c>run_skill_script</c>
    /// with a <c>RequireApproval()</c> gate. File-backed scripts additionally require a runner
    /// supplied to <see cref="SkillsBuilder.AddSkillsFromDirectory"/>. The runner is rejected
    /// unless script execution is enabled, so every file-backed script runs through the same
    /// approval-gated durable tool path.
    /// </para>
    /// <para>
    /// <b>File-skill drift.</b> <see cref="SkillResolver"/> re-materialises from file sources
    /// on first use after a worker restart or continue-as-new. If the directory contents have
    /// changed, the resolver reflects the new state while the StateBag still holds the old index.
    /// Treat file skill sources as immutable for the lifetime of a session.
    /// </para>
    /// </remarks>
    public DurableAgentBuilder UseSkills(Action<SkillsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new SkillsBuilder();
        configure(builder);
        var resolver = builder.BuildResolver();
        bool scriptsEnabled = builder.ScriptsEnabled;

        // 1. Register the provider (injects skill index as system message each LLM call).
        AddContextProvider(new SkillsContextProvider(resolver, scriptsEnabled));

        // 2. Register load_skill with SkipInterceptor — read-only, no side effects.
        AddTool(
            "load_skill",
            _ => AIFunctionFactory.Create(
                async ([Description("Skill name")] string name, CancellationToken ct) =>
                {
                    var skill = await resolver.FindByNameAsync(name, ct).ConfigureAwait(false);
                    if (skill is null)
                    {
                        return $"Skill '{name}' not found.";
                    }

                    var content = await skill.GetContentAsync(ct).ConfigureAwait(false);
                    if (!scriptsEnabled && content.Contains("<scripts>", StringComparison.Ordinal))
                    {
                        content = SkillsContextProvider.StripScriptsSection(content);
                    }

                    return content;
                },
                name: "load_skill",
                description: "Load the full instructions for a skill by name."),
            opts => opts.SkipInterceptor());

        // 3. Register read_skill_resource — no SkipInterceptor by default; resource delegates
        //    can be side-effectful and users may want interceptor coverage.
        AddTool(
            "read_skill_resource",
            sp => AIFunctionFactory.Create(
                async (
                    [Description("Skill name")] string skillName,
                    [Description("Resource name")] string resourceName,
                    CancellationToken ct) =>
                {
                    var skill = await resolver.FindByNameAsync(skillName, ct).ConfigureAwait(false);
                    if (skill is null)
                    {
                        return $"Skill '{skillName}' not found.";
                    }

                    var resource = await skill.GetResourceAsync(resourceName, ct).ConfigureAwait(false);
                    if (resource is null)
                    {
                        return $"Resource '{resourceName}' not found in '{skillName}'.";
                    }

                    return await resource.ReadAsync(sp, ct).ConfigureAwait(false);
                },
                name: "read_skill_resource",
                description: "Read a supplementary resource file from a skill."));

        // 4. Register run_skill_script only when EnableScriptExecution() was called.
        if (scriptsEnabled)
        {
            AddTool(
                "run_skill_script",
                sp => AIFunctionFactory.Create(
                    async (
                        [Description("Skill name")] string skillName,
                        [Description("Script name")] string scriptName,
                        [Description("JSON arguments object")] string argumentsJson,
                        CancellationToken ct) =>
                    {
                        var skill = await resolver.FindByNameAsync(skillName, ct).ConfigureAwait(false);
                        if (skill is null)
                        {
                            return $"Skill '{skillName}' not found.";
                        }

                        var script = await skill.GetScriptAsync(scriptName, ct).ConfigureAwait(false);
                        if (script is null)
                        {
                            return $"Script '{scriptName}' not found in '{skillName}'.";
                        }

                        JsonElement? argsElement = null;
                        try
                        {
                            argsElement = JsonSerializer.Deserialize<JsonElement>(
                                argumentsJson, AIJsonUtilities.DefaultOptions);
                        }
                        catch (JsonException ex)
                        {
                            return $"Invalid arguments JSON: {ex.Message}";
                        }

                        return await script.RunAsync(skill, argsElement, sp, ct).ConfigureAwait(false);
                    },
                    name: "run_skill_script",
                    description: "Execute a script from a skill."),
                opts => opts.NoRetry().RequireApproval());
        }

        return this;
    }

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
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="UseApprovalScopes"/> has already been called. Approval scopes
    /// install the built-in <c>ScopedApprovalInterceptor</c>, and scope-aware required tools have
    /// been excluded from <c>RequiresApprovalTools</c> — replacing the interceptor would silently
    /// bypass approval for those tools.
    /// </exception>
    public DurableAgentBuilder AddToolInterceptor(
        Func<IServiceProvider, IDurableToolInterceptor<AgentToolContext>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (_useApprovalScopes)
        {
            throw new InvalidOperationException(
                "Cannot register a custom tool interceptor after UseApprovalScopes() — scope-aware " +
                "required tools have been excluded from RequiresApprovalTools and rely on " +
                "ScopedApprovalInterceptor to enforce the approval gate. Replacing the interceptor " +
                "would silently bypass approval for those tools.");
        }

        _toolInterceptorFactory = factory;
        return this;
    }

    /// <summary>
    /// Registers the built-in scope-aware approval interceptor for this agent. The interceptor
    /// checks session and always-scopes before parking the workflow for human approval; tools
    /// that are not scope-annotated or have no matching scope record fall through to the standard
    /// <see cref="DurableToolOptions.RequireApproval()"/> gate.
    /// </summary>
    /// <param name="configure">
    /// Optional callback to configure <see cref="ApprovalScopesOptions"/>. When
    /// <see langword="null"/>, default options are used.
    /// </param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="AddToolInterceptor"/> has already been called. Approval scopes
    /// install the built-in <c>ScopedApprovalInterceptor</c>, and this release does not compose
    /// approval scopes with custom tool interceptors.
    /// </exception>
    public DurableAgentBuilder UseApprovalScopes(Action<ApprovalScopesOptions>? configure = null)
    {
        if (_toolInterceptorFactory is not null)
        {
            throw new InvalidOperationException(
                "UseApprovalScopes() cannot be combined with AddToolInterceptor(). Approval scopes " +
                "install the built-in ScopedApprovalInterceptor, and this release does not compose " +
                "approval scopes with custom tool interceptors.");
        }

        var opts = new ApprovalScopesOptions();
        configure?.Invoke(opts);
        _approvalScopesOptions = opts;
        _useApprovalScopes = true;
        _toolInterceptorFactory = _ => new ScopedApprovalInterceptor(opts);

        return this;
    }

    /// <summary>Internal accessor for Phase 2 registration plumbing.</summary>
    internal IReadOnlyList<DurableToolRegistration> ToolRegistrations => _tools;

    /// <summary>Internal accessor for Phase 2 registration plumbing.</summary>
    internal IReadOnlyList<Func<IServiceProvider, AIContextProvider>> ContextProviderFactories => _contextProviders;

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

        // Builder-time validation for approval-scope related combinations.
        if (_useApprovalScopes && _approvalScopesOptions is { } scopeOpts)
        {
            if (scopeOpts.MaxSessionScopeRecords <= 0)
            {
                throw new InvalidOperationException(
                    $"ApprovalScopesOptions.MaxSessionScopeRecords for agent '{Name}' must be a positive integer.");
            }

            if (scopeOpts.MaxSessionScopeBytes <= 0)
            {
                throw new InvalidOperationException(
                    $"ApprovalScopesOptions.MaxSessionScopeBytes for agent '{Name}' must be a positive integer.");
            }
        }

        // Validate per-tool combinations.
        foreach (var toolReg in _tools)
        {
            var opts = toolReg.Options;

            // RequireApproval + ScopeAware requires UseApprovalScopes to be called.
            if (opts.RequireApprovalFlag && opts.ScopeAwareFlag && !_useApprovalScopes)
            {
                throw new InvalidOperationException(
                    $"Tool '{toolReg.Name}' has ScopeAware() set but approval scopes are not enabled on agent '{Name}'. " +
                    "Call UseApprovalScopes() before registering scope-aware required tools.");
            }

            // RequireApproval + ScopeAware + SkipInterceptor is always invalid.
            if (opts.RequireApprovalFlag && opts.ScopeAwareFlag && opts.SkipInterceptorFlag)
            {
                throw new InvalidOperationException(
                    $"Tool '{toolReg.Name}' cannot combine RequireApproval(), ScopeAware(), and SkipInterceptor(); approval " +
                    "scopes require the interceptor to enforce the missing-scope approval gate.");
            }
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

        if (MaxEntryCount is <= 0)
        {
            throw new InvalidOperationException(
                $"DurableAgentBuilder.MaxEntryCount for agent '{Name}' must be a positive integer when set (null inherits the worker-level default).");
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
            MaxToolCallsPerTurn: MaxToolCallsPerTurn,
            HistoryReducer: HistoryReducer,
            HistoryReducerKey: HistoryReducerKey,
            ConfigureAgentPipeline: ConfigureAgentPipeline,
            ToolInterceptorFactory: _toolInterceptorFactory,
            UseApprovalScopes: _useApprovalScopes,
            ApprovalScopesOptions: _approvalScopesOptions,
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
