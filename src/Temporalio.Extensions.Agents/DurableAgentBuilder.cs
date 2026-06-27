using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Common;
using Temporalio.Extensions.Agents.Approvals;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.Skills;
using Temporalio.Extensions.Agents.Tools;
using Temporalio.Extensions.AI.Approvals;
using Temporalio.Extensions.AI.Session;
using Temporalio.Extensions.AI.Tools;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Internal carrier for a tool registered on a <see cref="DurableAgentBuilder"/>. The factory is
/// invoked at first activity dispatch (the same lifecycle as <see cref="DurableAgentBuilder.ChatClient"/>);
/// the resolved <see cref="AIFunction"/> is cached for the lifetime of the worker.
/// </summary>
internal sealed record DurableToolRegistration(
    string Name,
    Func<IServiceProvider, AIFunction> Factory,
    DurableToolOptions Options);

/// <summary>
/// Fluent builder for registering a durable agent via <c>TemporalAgentsOptions.AddDurableAgent</c>.
/// Properties capture per-agent scalars; <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/> and
/// <see cref="AddContextProvider(AIContextProvider)"/> capture per-agent collections.
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
/// <b>Factory composition lifecycle.</b> Factories registered via <see cref="ChatClient"/>,
/// <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/>,
/// <see cref="AddContextProvider(Func{IServiceProvider, AIContextProvider})"/>, and
/// <see cref="HistoryStore"/> are invoked once at first activity dispatch using the worker's root
/// <see cref="IServiceProvider"/>. The resolved values are cached for the lifetime of the worker
/// process. Anything resolved through these factories should therefore be a singleton (or carry its
/// own internal scoping); services registered with <c>AddScoped</c> will silently behave as
/// singletons under this composition path.
/// </para>
/// </remarks>
public sealed class DurableAgentBuilder
{
    // Tools are stored in registration order; names are case-insensitive (consistent with
    // TemporalAgentsOptions agent-name handling).
    private readonly List<DurableToolRegistration> _tools = new();
    private readonly HashSet<string> _toolNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Func<IServiceProvider, AIContextProvider>> _contextProviders = new();
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
    /// invoked once at first activity dispatch and the result is cached for the lifetime of the
    /// worker process.
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
    /// continue-as-new. When <see langword="null"/>, the full history is carried forward verbatim.
    /// </summary>
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer { get; set; }

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
    /// <b>Construction-idempotency contract.</b> Decorators added through this callback are
    /// constructed twice per agent registration per worker-process lifetime — once at startup
    /// validation (C-check dry-run) and once at first activity dispatch. Decorators with
    /// side-effect-bearing constructors (file handles, listeners, network connections) must
    /// defer those side effects to <c>RunAsync</c> or use lazy initialization patterns.
    /// </para>
    /// <para>
    /// <b>Forbidden middleware.</b> Calling <c>.Use(funcInvocationCallback)</c> (the agent-side
    /// equivalent of <c>FunctionInvokingChatClient</c>) inside this callback is rejected at
    /// worker startup with <see cref="Temporalio.Extensions.AI.Exceptions.DurableFunctionInvocationConflictException"/>
    /// — the durable libraries handle tool invocation as separate Temporal activities, and
    /// in-pipeline function-invocation middleware would conflict with that contract.
    /// </para>
    /// </remarks>
    public Action<AIAgentBuilder>? ConfigureAgentPipeline { get; set; }

    /// <summary>
    /// Gets or sets a per-agent <see cref="IAgentHistoryStore"/> factory. When <see langword="null"/>,
    /// inherits the worker-level <c>TemporalAgentsOptions.HistoryStore</c> (which itself may be
    /// <see langword="null"/>, meaning no external history is used). The factory is invoked once at
    /// first activity dispatch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is no per-agent opt-out mechanism.</b> External-history-store mode is activated
    /// when any history store factory is present — either a non-null factory on this property OR
    /// a non-null factory on <see cref="TemporalAgentsOptions.HistoryStore"/>. Setting this
    /// property to a factory that returns <see langword="null"/> does not disable the store; it
    /// causes the activity to throw at runtime when it attempts to append turns. If you need
    /// one agent on a worker to bypass an externally configured store, deploy that agent on a
    /// separate worker registration that does not set <see cref="TemporalAgentsOptions.HistoryStore"/>.
    /// </para>
    /// </remarks>
    public Func<IServiceProvider, IAgentHistoryStore>? HistoryStore { get; set; }

    /// <summary>
    /// Gets or sets the keyed-DI name of the <see cref="Compaction.ICompactionStrategy"/> to
    /// apply when in-session compaction triggers. <see langword="null"/> inherits the
    /// worker-level <see cref="TemporalAgentsOptions.DefaultCompactionStrategy"/>; both
    /// <see langword="null"/> disables compaction for the agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Step 6a — API surface. The activity-side trigger evaluator (Step 6b) decides when
    /// compaction fires; the workflow dispatches the configured strategy as a separate
    /// <c>CompactHistory</c> activity (Step 6d). Built-in keys pre-registered in Step 6c:
    /// <c>"truncation"</c>, <c>"sliding-window"</c>, <c>"summarization"</c>.
    /// </para>
    /// <para>
    /// Marked <c>[Experimental("TA002")]</c> at the API-surface level (consumer code that
    /// sets this property triggers the diagnostic) — the wire shape becomes stable when
    /// compaction ships in a non-preview release.
    /// </para>
    /// </remarks>
    [Experimental("TA002")]
    public string? CompactionStrategyKey { get; set; }

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
    /// <param name="factory">Factory that produces the <see cref="AIFunction"/>.</param>
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
    /// Registers a concrete <see cref="AIContextProvider"/> for this agent.
    /// </summary>
    /// <param name="provider">The provider instance.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="provider"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// In durable agents, the provider's <c>InvokingAsync</c> and <c>InvokedAsync</c> hooks fire
    /// once per LLM call (per <c>RunAgentStep</c> activity), not once per turn. Make these hooks
    /// idempotent and cheap, or cache results via <c>StateBag</c> to skip redundant work within a
    /// turn. The provider instance is constructed once per agent per worker process and shared
    /// across all sessions on that worker — treat fields as effectively read-only after
    /// construction; per-session state must live in the <c>StateBag</c>.
    /// </remarks>
    public DurableAgentBuilder AddContextProvider(AIContextProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _contextProviders.Add(_ => provider);
        return this;
    }

    /// <summary>
    /// Registers an <see cref="AIContextProvider"/> via a factory. The factory is invoked once at
    /// first activity dispatch (the same lifecycle as <see cref="ChatClient"/>) and the resolved
    /// instance is cached for the worker's lifetime.
    /// </summary>
    /// <param name="factory">Factory that produces the <see cref="AIContextProvider"/>.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// In durable agents, the provider's <c>InvokingAsync</c> and <c>InvokedAsync</c> hooks fire
    /// once per LLM call (per <c>RunAgentStep</c> activity), not once per turn. Make these hooks
    /// idempotent and cheap, or cache results via <c>StateBag</c> to skip redundant work within a
    /// turn. The provider instance is constructed once per agent per worker process and shared
    /// across all sessions on that worker — treat fields as effectively read-only after
    /// construction; per-session state must live in the <c>StateBag</c>.
    /// </remarks>
    public DurableAgentBuilder AddContextProvider(Func<IServiceProvider, AIContextProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _contextProviders.Add(factory);
        return this;
    }

    /// <summary>
    /// Registers progressive-disclosure skills support for this agent. Internally creates a
    /// <see cref="SkillsContextProvider"/> (registered via <see cref="AddContextProvider(AIContextProvider)"/>)
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
    /// with a <c>RequireApproval()</c> gate. File-backed scripts are <b>not</b> supported by
    /// the native SKILL.md scanner — <see cref="SkillsBuilder.AddSkillsFromDirectory"/> throws
    /// <see cref="NotSupportedException"/> when a runner is supplied. Script execution is only
    /// available for inline and class-based skills, or via a custom
    /// <see cref="AgentSkillsSource"/> registered through
    /// <see cref="SkillsBuilder.AddSkillsSource"/>.
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

                    var content = skill.Content;
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

                    var resource = skill.Resources?.FirstOrDefault(
                        r => r.Name.Equals(resourceName, StringComparison.OrdinalIgnoreCase));
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

                        var script = skill.Scripts?.FirstOrDefault(
                            s => s.Name.Equals(scriptName, StringComparison.OrdinalIgnoreCase));
                        if (script is null)
                        {
                            return $"Script '{scriptName}' not found in '{skillName}'.";
                        }

                        Dictionary<string, object?>? rawArgs = null;
                        try
                        {
                            rawArgs = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                argumentsJson, AIJsonUtilities.DefaultOptions);
                        }
                        catch (JsonException ex)
                        {
                            return $"Invalid arguments JSON: {ex.Message}";
                        }

                        var args = new AIFunctionArguments(
                            rawArgs ?? new Dictionary<string, object?>());
                        return await script.RunAsync(skill, args, ct).ConfigureAwait(false);
                    },
                    name: "run_skill_script",
                    description: "Execute a script from a skill."),
                opts => opts.NoRetry().RequireApproval());
        }

        return this;
    }

    /// <summary>
    /// Registers a per-agent interceptor factory. The factory is invoked once at first activity
    /// dispatch and the resolved instance is cached for the worker's lifetime. Per-agent interceptor
    /// wins over <see cref="TemporalAgentsOptions.DefaultToolInterceptor"/> (H1 rule).
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
            // Validate AlwaysScopesStoreKey.
            if (string.IsNullOrWhiteSpace(scopeOpts.AlwaysScopesStoreKey))
            {
                throw new InvalidOperationException(
                    $"ApprovalScopesOptions.AlwaysScopesStoreKey for agent '{Name}' must be non-null and non-whitespace.");
            }

            if (scopeOpts.AlwaysScopesStoreKey == "temporal.approval_scopes.session")
            {
                throw new InvalidOperationException(
                    "ApprovalScopesOptions.AlwaysScopesStoreKey cannot be set to 'temporal.approval_scopes.session' — " +
                    "that key is reserved for session-scope records managed by Feature B internally. Use a different store key.");
            }

            // Validate numeric bounds.
            if (scopeOpts.MaxAlwaysScopeCacheRecords <= 0)
            {
                throw new InvalidOperationException(
                    $"ApprovalScopesOptions.MaxAlwaysScopeCacheRecords for agent '{Name}' must be a positive integer.");
            }

            if (scopeOpts.MaxAlwaysScopeCacheBytes <= 0)
            {
                throw new InvalidOperationException(
                    $"ApprovalScopesOptions.MaxAlwaysScopeCacheBytes for agent '{Name}' must be a positive integer.");
            }

            if (scopeOpts.ApprovalScopeActivityMaximumAttempts <= 0)
            {
                throw new InvalidOperationException(
                    $"ApprovalScopesOptions.ApprovalScopeActivityMaximumAttempts for agent '{Name}' must be a positive integer.");
            }

            if (scopeOpts.ApprovalScopeActivityTimeout <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"ApprovalScopesOptions.ApprovalScopeActivityTimeout for agent '{Name}' must be greater than TimeSpan.Zero.");
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
            HistoryStore: HistoryStore,
            TimeToLive: TimeToLive,
            ApprovalTimeout: ApprovalTimeout,
            ActivityTimeout: ActivityTimeout,
            HeartbeatTimeout: HeartbeatTimeout,
            RetryPolicy: RetryPolicy,
            MaxEntryCount: MaxEntryCount,
            MaxToolCallsPerTurn: MaxToolCallsPerTurn,
            HistoryReducer: HistoryReducer,
            ConfigureAgentPipeline: ConfigureAgentPipeline,
            CompactionStrategyKey: CompactionStrategyKey,
            ToolInterceptorFactory: _toolInterceptorFactory,
            UseApprovalScopes: _useApprovalScopes,
            ApprovalScopesOptions: _approvalScopesOptions);
    }

    private void AddToolCore(string name, Func<IServiceProvider, AIFunction> factory, Action<DurableToolOptions>? configure)
    {
        if (_toolNames.Contains(name))
        {
            throw new ArgumentException(
                $"Tool '{name}' is already registered on agent '{Name}'.",
                nameof(name));
        }

        var options = new DurableToolOptions();
        configure?.Invoke(options);
        _toolNames.Add(name);
        _tools.Add(new DurableToolRegistration(name, factory, options));
    }
}
