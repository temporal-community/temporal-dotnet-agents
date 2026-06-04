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
/// Script execution is disabled by default. File-backed scripts are <b>not</b> supported
/// by the native SKILL.md scanner — <see cref="AddSkillsFromDirectory"/> throws
/// <see cref="NotSupportedException"/> when a runner is supplied. Script execution
/// works only for inline (<see cref="AgentInlineSkill"/>) and class-based
/// (<see cref="AgentClassSkill{TSelf}"/>) skills, or when a custom
/// <see cref="AgentSkillsSource"/> is registered via <see cref="AddSkillsSource"/>.
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
    /// a separate skill. Directories are scanned up to 2 levels deep by default
    /// (root + children + grandchildren).
    /// </param>
    /// <param name="runner">
    /// Not supported by the native SKILL.md scanner. Must be <see langword="null"/>.
    /// Use inline or class-based skills for script execution.
    /// </param>
    /// <param name="configure">
    /// Not supported by the native SKILL.md scanner. Must be <see langword="null"/>.
    /// </param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is whitespace.</exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when <paramref name="runner"/> or <paramref name="configure"/> is non-<see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Supported frontmatter fields:</b> <c>name</c>, <c>description</c>, <c>license</c>,
    /// <c>compatibility</c>. The raw SKILL.md content is passed as the skill's
    /// <c>Content</c> property (returned verbatim by <c>load_skill</c>).
    /// </para>
    /// <para>
    /// <b>Not supported:</b> resources, scripts, extension filters, script runners. For
    /// skills that require these features, use <see cref="AddSkillsSource"/> with a
    /// custom <see cref="AgentSkillsSource"/> implementation.
    /// </para>
    /// <para>
    /// <b>Frontmatter values must be unquoted strings.</b> A value like
    /// <c>name: "expense-report"</c> (with quotes) will include the literal quote
    /// characters, which will fail MAF name validation and cause the skill to be
    /// silently skipped during the directory scan.
    /// </para>
    /// <para>
    /// <b>Malformed or invalid SKILL.md files are silently skipped</b> (no diagnostics)
    /// unless a logger is injected by constructing a <see cref="FileSkillsSource"/> directly
    /// and passing it to <see cref="AddSkillsSource"/>.
    /// </para>
    /// </remarks>
    public SkillsBuilder AddSkillsFromDirectory(
        string path,
        AgentFileSkillScriptRunner? runner = null,
        Action<AgentFileSkillsSourceOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (runner is not null)
        {
            throw new NotSupportedException(
                "File-backed script execution is not supported by the native SKILL.md scanner. " +
                "Use inline or class-based skills for script execution.");
        }

        if (configure is not null)
        {
            throw new NotSupportedException(
                "AgentFileSkillsSourceOptions is not supported by the native SKILL.md scanner.");
        }

        _sources.Add(new FileSkillsSource(path));
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
    /// human approval before dispatching (Rule 2 floor). File-backed scripts are <b>not</b>
    /// supported by the native SKILL.md scanner — <see cref="AddSkillsFromDirectory"/> throws
    /// <see cref="NotSupportedException"/> when a runner is supplied. Script execution applies
    /// only to inline (<see cref="AgentInlineSkill"/>) and class-based
    /// (<see cref="AgentClassSkill{TSelf}"/>) skills, or to skills provided by a custom
    /// <see cref="AgentSkillsSource"/> registered via <see cref="AddSkillsSource"/>.
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
}
