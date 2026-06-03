using System.Reflection;
using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents.Skills;

/// <summary>
/// Fluent builder used inside <c>DurableAgentBuilder.UseSkills(Action&lt;SkillsBuilder&gt;)</c>
/// to register skills of all three MAF types and control optional script execution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Supported skill types (MAF 1.3.0):</b>
/// <list type="table">
/// <item>
///   <term>File-based</term>
///   <description>Use <see cref="AddSkillsFromDirectory"/> — scans a directory for SKILL.md files.</description>
/// </item>
/// <item>
///   <term>Inline</term>
///   <description>Use <see cref="AddSkill"/> with an <see cref="AgentInlineSkill"/> instance.</description>
/// </item>
/// <item>
///   <term>Class-based</term>
///   <description>Use <see cref="AddSkill"/> with an <see cref="AgentClassSkill{TSelf}"/> subclass instance.</description>
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>Script execution.</b> Call <see cref="EnableScriptExecution"/> to opt in to
/// <c>run_skill_script</c> registration with a built-in <c>RequireApproval()</c> gate.
/// Script execution is disabled by default because file-based scripts require a runner
/// delegate (supplied via <see cref="AddSkillsFromDirectory"/>'s <paramref name="runner"/>
/// parameter) and because arbitrary script execution carries side-effect risk.
/// Without <see cref="EnableScriptExecution"/>, <c>run_skill_script</c> is not registered
/// and the skill index does not mention script invocation.
/// </para>
/// </remarks>
public sealed class SkillsBuilder
{
    private readonly List<AgentSkill> _skills = [];
    private readonly List<AgentSkillsSource> _sources = [];

    /// <summary>
    /// Gets a value indicating whether script execution was opted in to via
    /// <see cref="EnableScriptExecution"/>.
    /// </summary>
    internal bool ScriptsEnabled { get; private set; }

    /// <summary>
    /// Registers a directory to scan for SKILL.md files (file-based skills).
    /// </summary>
    /// <param name="path">
    /// Path to scan for SKILL.md files. Each subdirectory containing a SKILL.md becomes
    /// a separate skill.
    /// </param>
    /// <param name="runner">
    /// Optional runner for file-based scripts. Required only when the scanned skills contain
    /// script files AND <see cref="EnableScriptExecution"/> is called. Inline and class-based
    /// scripts are delegate-backed and do not require a runner. If a file script is invoked
    /// without a runner, MAF throws <see cref="InvalidOperationException"/> at execution time.
    /// </param>
    /// <param name="configure">
    /// Optional callback to configure <see cref="AgentFileSkillsSourceOptions"/> (allowed
    /// resource/script extensions, script directories, etc.).
    /// </param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is whitespace.</exception>
    public SkillsBuilder AddSkillsFromDirectory(
        string path,
        AgentFileSkillScriptRunner? runner = null,
        Action<AgentFileSkillsSourceOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        AgentFileSkillsSourceOptions? options = null;
        if (configure is not null)
        {
            options = new AgentFileSkillsSourceOptions();
            configure(options);
        }

        // AgentFileSkillsSource is internal in MAF 1.3.0. We build an AgentSkillsProvider
        // with the path constructor (which creates an AgentFileSkillsSource internally)
        // and wrap it in an AgentSkillsSource adapter for SkillResolver.
        var provider = new AgentSkillsProvider(path, runner, options, null, null);
        _sources.Add(new ProviderBackedSkillsSource(provider));
        return this;
    }

    /// <summary>
    /// Registers an <see cref="AgentSkillsSource"/> directly. Use when you have a custom
    /// skill source implementation or a pre-built source from the MAF API.
    /// </summary>
    /// <param name="source">The source to add.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is <see langword="null"/>.</exception>
    public SkillsBuilder AddSkillsSource(AgentSkillsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources.Add(source);
        return this;
    }

    /// <summary>
    /// Registers a single <see cref="AgentSkill"/> instance
    /// (<see cref="AgentInlineSkill"/> or <see cref="AgentClassSkill{TSelf}"/>).
    /// </summary>
    /// <param name="skill">The skill to register.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="skill"/> is <see langword="null"/>.</exception>
    public SkillsBuilder AddSkill(AgentSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        _skills.Add(skill);
        return this;
    }

    /// <summary>
    /// Registers multiple <see cref="AgentSkill"/> instances.
    /// </summary>
    /// <param name="skills">The skills to register.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="skills"/> is <see langword="null"/> or contains a
    /// <see langword="null"/> entry.
    /// </exception>
    public SkillsBuilder AddSkills(IEnumerable<AgentSkill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        foreach (var skill in skills)
        {
            AddSkill(skill);
        }

        return this;
    }

    /// <summary>
    /// Opts in to <c>run_skill_script</c> tool registration with a built-in
    /// <c>RequireApproval()</c> gate. Disabled by default.
    /// </summary>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <remarks>
    /// <para>
    /// When enabled, the <c>run_skill_script</c> tool is registered and will always require
    /// human approval before dispatching (Rule 2 floor). The approval gate does not eliminate
    /// execution risk for file-based scripts — callers should ensure a script runner is
    /// supplied to <see cref="AddSkillsFromDirectory"/> when file scripts are present.
    /// </para>
    /// <para>
    /// Inline and class-based scripts are delegate-backed and do not require a runner.
    /// </para>
    /// </remarks>
    public SkillsBuilder EnableScriptExecution()
    {
        ScriptsEnabled = true;
        return this;
    }

    /// <summary>
    /// Builds and returns a <see cref="SkillResolver"/> from the registered skills and sources.
    /// Called internally by <c>DurableAgentBuilder.UseSkills()</c>.
    /// </summary>
    internal SkillResolver BuildResolver() =>
        new SkillResolver(_skills.AsReadOnly(), _sources.AsReadOnly());

    /// <summary>
    /// Adapts an <see cref="AgentSkillsProvider"/> (which is an <see cref="AIContextProvider"/>,
    /// not an <see cref="AgentSkillsSource"/>) to the <see cref="AgentSkillsSource"/> interface
    /// by extracting its internal source via reflection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why reflection?</b> <c>AgentFileSkillsSource</c> is internal in MAF 1.3.0, so we
    /// cannot instantiate it directly. <c>AgentSkillsProvider(string path, ...)</c> creates
    /// one internally and stores it in <c>_source : AgentSkillsSource</c>. We extract that
    /// field once per instance to forward <see cref="GetSkillsAsync"/> calls.
    /// </para>
    /// <para>
    /// This reflection access is isolated to this private class and protected by a null-check
    /// fallback. If the MAF internal field is renamed in a future version, <c>GetSkillsAsync</c>
    /// will throw <see cref="InvalidOperationException"/> with a clear diagnostic.
    /// </para>
    /// </remarks>
    private sealed class ProviderBackedSkillsSource : AgentSkillsSource
    {
        // Lazily cache the FieldInfo so reflection happens once per type, not per instance.
        private static readonly FieldInfo? s_sourceField =
            typeof(AgentSkillsProvider).GetField("_source",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly AgentSkillsSource _inner;

        internal ProviderBackedSkillsSource(AgentSkillsProvider provider)
        {
            if (s_sourceField is null)
            {
                throw new InvalidOperationException(
                    "SkillsBuilder.AddSkillsFromDirectory: could not locate the internal " +
                    "_source field on AgentSkillsProvider. This may indicate a breaking " +
                    "change in the Microsoft.Agents.AI library. Please report this issue.");
            }

            var inner = s_sourceField.GetValue(provider) as AgentSkillsSource;
            _inner = inner ?? throw new InvalidOperationException(
                "SkillsBuilder.AddSkillsFromDirectory: AgentSkillsProvider._source was null " +
                "or not an AgentSkillsSource. This may indicate a breaking change in the " +
                "Microsoft.Agents.AI library.");
        }

        public override Task<IList<AgentSkill>> GetSkillsAsync(
            CancellationToken cancellationToken = default) =>
            _inner.GetSkillsAsync(cancellationToken);
    }
}
