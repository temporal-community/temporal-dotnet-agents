using System.Reflection;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Temporalio.Extensions.Agents.Skills;

/// <summary>
/// An <see cref="AgentSkillsSource"/> that scans a directory tree for <c>SKILL.md</c>
/// files and materializes them as <see cref="AgentFileSkill"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scanning.</b> The scanner walks the specified directory up to
/// <paramref name="maxDepth"/> levels deep (default 2: root + children + grandchildren).
/// Each <c>SKILL.md</c> found is parsed for YAML frontmatter containing <c>name</c> and
/// <c>description</c>. The entire raw file content is passed as the skill's
/// <see cref="AgentFileSkill.Content"/> property.
/// </para>
/// <para>
/// <b>Error handling.</b> I/O errors and parse failures on individual files are logged as
/// warnings and skipped — the scan continues. I/O errors on child directories are also
/// logged and skipped. Errors on the root directory propagate (the caller must handle them).
/// </para>
/// <para>
/// <b>maxDepth semantics.</b> 0 = root directory only; 1 = root + one level of
/// subdirectories; 2 = root + two levels (default).
/// </para>
/// <para>
/// <b>Supported frontmatter fields.</b> <c>name</c>, <c>description</c>, <c>license</c>,
/// <c>compatibility</c>. Frontmatter values must be unquoted strings — quoted values
/// (e.g. <c>name: "my-skill"</c>) will include the quotes, which will fail MAF name
/// validation and cause the skill to be silently skipped.
/// </para>
/// <para>
/// <b>Not supported.</b> resources, scripts, extension filters, script runners.
/// </para>
/// </remarks>
internal sealed class FileSkillsSource : AgentSkillsSource
{
    // AgentFileSkill has an internal constructor in MAF 1.3.0. We cache the ConstructorInfo
    // once per type so the reflection cost is paid only on the first scan.
    private static readonly ConstructorInfo? s_agentFileSkillCtor =
        typeof(AgentFileSkill).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [
                typeof(AgentSkillFrontmatter),
                typeof(string),
                typeof(string),
                typeof(IReadOnlyList<AgentSkillResource>),
                typeof(IReadOnlyList<AgentSkillScript>),
            ],
            modifiers: null);

    private readonly string _directory;
    private readonly int _maxDepth;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="FileSkillsSource"/>.
    /// </summary>
    /// <param name="directory">The root directory to scan for SKILL.md files.</param>
    /// <param name="maxDepth">
    /// Maximum directory depth to scan. 0 = root only; 1 = root + children;
    /// 2 = root + children + grandchildren (default).
    /// </param>
    /// <param name="logger">
    /// Optional logger for warnings about malformed files or inaccessible directories.
    /// When <see langword="null"/>, <see cref="NullLogger.Instance"/> is used.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="directory"/> is <see langword="null"/> or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxDepth"/> is less than zero.
    /// </exception>
    public FileSkillsSource(string directory, int maxDepth = 2, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth,
                "maxDepth must be zero or greater.");
        }

        _directory = directory;
        _maxDepth = maxDepth;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public override async Task<IList<AgentSkill>> GetSkillsAsync(CancellationToken ct = default)
    {
        var results = new List<AgentSkill>();
        await VisitDirectoryAsync(_directory, _maxDepth, results, isRoot: true, ct).ConfigureAwait(false);

        // Sort alphabetically by name (OrdinalIgnoreCase) for deterministic ordering.
        results.Sort((a, b) =>
            string.Compare(a.Frontmatter.Name, b.Frontmatter.Name, StringComparison.OrdinalIgnoreCase));

        return results;
    }

    private async Task VisitDirectoryAsync(
        string dir,
        int remainingDepth,
        List<AgentSkill> results,
        bool isRoot,
        CancellationToken ct)
    {
        if (isRoot)
        {
            // Root errors propagate — do NOT wrap in try/catch.
            await VisitDirectoryBodyAsync(dir, remainingDepth, results, ct).ConfigureAwait(false);
        }
        else
        {
            // Non-root: catch I/O errors so sibling directories are still processed.
            try
            {
                await VisitDirectoryBodyAsync(dir, remainingDepth, results, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogWarning(ex,
                    "FileSkillsSource: skipping inaccessible directory '{Directory}'.", dir);
            }
        }
    }

    private async Task VisitDirectoryBodyAsync(
        string dir,
        int remainingDepth,
        List<AgentSkill> results,
        CancellationToken ct)
    {
        // Step A: check for cancellation before doing any I/O.
        ct.ThrowIfCancellationRequested();

        // Step B: scan SKILL.md files in this directory only (TopDirectoryOnly).
        foreach (var file in Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.TopDirectoryOnly))
        {
            string rawContent;
            try
            {
                rawContent = await File.ReadAllTextAsync(file, Encoding.UTF8, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex,
                    "FileSkillsSource: could not read '{File}'; skipping.", file);
                continue;
            }

            // BOM fallback strip (File.ReadAllTextAsync with Encoding.UTF8 normally handles BOM,
            // but this catches any remaining U+FEFF character).
            rawContent = rawContent.TrimStart('﻿');

            var parsed = SkillsMarkdownParser.TryParse(rawContent);
            if (parsed is null)
            {
                _logger.LogWarning(
                    "FileSkillsSource: '{File}' has missing or malformed frontmatter; skipping.", file);
                continue;
            }

            try
            {
                var fm = new AgentSkillFrontmatter(parsed.Name, parsed.Description, parsed.Compatibility!)
                {
                    License = parsed.License,
                };
                var skill = CreateFileSkill(fm, rawContent, Path.GetDirectoryName(file)!);
                results.Add(skill);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex,
                    "FileSkillsSource: '{File}' produced an invalid skill (check name/description format); skipping.", file);
            }
        }

        // Step C: if at max depth, do not recurse further.
        if (remainingDepth == 0)
        {
            return;
        }

        // Step D: recurse into subdirectories.
        ct.ThrowIfCancellationRequested();
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            ct.ThrowIfCancellationRequested();
            await VisitDirectoryAsync(sub, remainingDepth - 1, results, isRoot: false, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates an <see cref="AgentFileSkill"/> instance. Because <c>AgentFileSkill</c> has an
    /// internal constructor in MAF 1.3.0, reflection is used — the <see cref="ConstructorInfo"/>
    /// is cached in <see cref="s_agentFileSkillCtor"/> so the cost is paid only once.
    /// </summary>
    private static AgentFileSkill CreateFileSkill(
        AgentSkillFrontmatter frontmatter,
        string content,
        string directoryPath)
    {
        if (s_agentFileSkillCtor is null)
        {
            throw new InvalidOperationException(
                "FileSkillsSource: could not locate the AgentFileSkill constructor. " +
                "This may indicate a breaking change in the Microsoft.Agents.AI library.");
        }

        return (AgentFileSkill)s_agentFileSkillCtor.Invoke(
            [frontmatter, content, directoryPath, null, null]);
    }
}
