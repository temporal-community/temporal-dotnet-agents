#pragma warning disable MAAI001 // experimental MAF skills surface (AgentSkillsSource/AgentFileSkillsSource); inventoried in Internal/ExperimentalApiSuppressions.cs
using Microsoft.Agents.AI;

namespace TemporalCommunity.Extensions.Agents.Skills;

/// <summary>
/// Fluent builder used inside <c>DurableAgentBuilder.UseSkills(Action&lt;SkillsBuilder&gt;)</c>
/// to register skills of all three MAF types and control optional script execution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Supported skill types:</b>
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
/// File-backed scripts additionally require an explicit runner supplied to
/// <see cref="AddSkillsFromDirectory"/>. Without both opt-ins, directory-backed script
/// discovery is disabled and the skill index does not mention script invocation.
/// </para>
/// </remarks>
public sealed class SkillsBuilder
{
    private readonly List<AgentSkill> _skills = [];
    private readonly List<AgentSkillsSource> _sources = [];
    private bool _hasDirectoryScriptRunner;

    /// <summary>
    /// Gets a value indicating whether script execution was opted in to via
    /// <see cref="EnableScriptExecution"/>.
    /// </summary>
    internal bool ScriptsEnabled { get; private set; }

    /// <summary>
    /// Registers a directory to scan for SKILL.md files (file-based skills).
    /// </summary>
    /// <param name="path">
    /// Path to scan for SKILL.md files. MAF discovers skill-root directories up to two
    /// levels deep by default. A directory that contains a SKILL.md is a skill root; its
    /// descendants are treated as files belonging to that skill rather than additional skills.
    /// </param>
    /// <param name="runner">
    /// Optional file-script runner. When supplied, <see cref="EnableScriptExecution"/> must
    /// also be called before the builder is finalized.
    /// </param>
    /// <param name="configure">
    /// Configures MAF's native file-skill discovery options. Script extensions require a
    /// non-null <paramref name="runner"/>.
    /// </param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when script extensions are configured without a script runner.
    /// </exception>
    /// <remarks>
    /// <para>
    /// MAF validates the YAML frontmatter and discovers resources according to the configured
    /// <see cref="AgentFileSkillsSourceOptions"/>. The raw SKILL.md content is available through
    /// <c>load_skill</c>.
    /// </para>
    /// <para>
    /// Resource discovery is enabled by default. File-backed scripts are discovered only when
    /// both a runner and <see cref="EnableScriptExecution"/> are configured.
    /// </para>
    /// </remarks>
    public SkillsBuilder AddSkillsFromDirectory(
        string path,
        AgentFileSkillScriptRunner? runner = null,
        Action<AgentFileSkillsSourceOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var options = new AgentFileSkillsSourceOptions();
        configure?.Invoke(options);

        if (runner is null)
        {
            if (options.AllowedScriptExtensions?.Any() == true)
            {
                throw new InvalidOperationException(
                    "File-backed script extensions require a non-null script runner. " +
                    "Supply a runner and call EnableScriptExecution().");
            }

            // AgentFileSkillsSource otherwise uses its default script extensions. Suppress
            // discovery unless the caller explicitly supplies a runnable script path.
            options.AllowedScriptExtensions = [];
        }
        else
        {
            _hasDirectoryScriptRunner = true;
        }

        _sources.Add(new AgentFileSkillsSource(path, runner, options));
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
    /// human approval before dispatching (Rule 2 floor). Directory-backed scripts also require
    /// a non-null runner supplied to <see cref="AddSkillsFromDirectory"/>.
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
    internal SkillResolver BuildResolver()
    {
        if (_hasDirectoryScriptRunner && !ScriptsEnabled)
        {
            throw new InvalidOperationException(
                "File-backed script runners require EnableScriptExecution() so scripts are " +
                "executed through the approval-gated durable tool.");
        }

        return new SkillResolver(_skills.AsReadOnly(), _sources.AsReadOnly());
    }
}
