#pragma warning disable MAAI001 // experimental MAF skills surface (AgentSkill/AgentFileSkill); inventoried in Internal/ExperimentalApiSuppressions.cs
using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents.Skills;

/// <summary>
/// Shared closure instance that lazily materializes the skill map from registered
/// <see cref="AgentSkill"/> instances and <see cref="AgentSkillsSource"/> objects.
/// Created once by <c>DurableAgentBuilder.UseSkills()</c> and captured by both the
/// <see cref="SkillsContextProvider"/> and the skill tool closures.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety.</b> Multiple <c>InvokeAgentTool</c> activities may call
/// <see cref="FindByNameAsync"/> concurrently on the same worker. A
/// <see cref="SemaphoreSlim"/> (1, 1) guard ensures only one source scan runs.
/// </para>
/// <para>
/// <b>Worker restart / continue-as-new.</b> After a restart, <c>InvokeAgentTool</c>
/// activities can be replayed before <c>ProvideAIContextAsync</c> has been called on
/// the new worker instance. <see cref="FindByNameAsync"/> handles this safely by
/// materialising on demand without waiting for the provider loop.
/// </para>
/// <para>
/// <b>Not registered in DI.</b> <c>sp.GetRequiredService&lt;SkillResolver&gt;()</c>
/// would fail. Resolve by capturing the instance in closures inside
/// <c>DurableAgentBuilder.UseSkills()</c>.
/// </para>
/// </remarks>
internal sealed class SkillResolver
{
    private readonly IReadOnlyList<AgentSkill> _skills;
    private readonly IReadOnlyList<AgentSkillsSource> _sources;

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Dictionary<string, AgentSkill>? _loaded;

    /// <summary>
    /// Initializes a new instance of <see cref="SkillResolver"/>.
    /// </summary>
    /// <param name="skills">Inline/class-based skills registered directly.</param>
    /// <param name="sources">File-based skill sources that must be scanned asynchronously.</param>
    internal SkillResolver(
        IReadOnlyList<AgentSkill> skills,
        IReadOnlyList<AgentSkillsSource> sources)
    {
        _skills = skills;
        _sources = sources;
    }

    /// <summary>
    /// Ensures the skill map is loaded. Safe to call concurrently — only one scan runs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when two or more skills share the same name (case-insensitive).
    /// </exception>
    internal async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        // Fast path: already loaded.
        if (_loaded is not null)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the gate.
            if (_loaded is not null)
            {
                return;
            }

            var map = new Dictionary<string, AgentSkill>(StringComparer.OrdinalIgnoreCase);

            // Register directly-provided skills first.
            foreach (var skill in _skills)
            {
                var name = skill.Frontmatter.Name;
                if (map.ContainsKey(name))
                {
                    throw new InvalidOperationException(
                        $"Duplicate skill name '{name}' detected during SkillResolver initialization. " +
                        "Each skill must have a unique name (case-insensitive).");
                }

                map[name] = skill;
            }

            // Scan all registered sources.
            foreach (var source in _sources)
            {
                var fromSource = await source.GetSkillsAsync(cancellationToken).ConfigureAwait(false);
                if (fromSource is null)
                {
                    continue;
                }

                foreach (var skill in fromSource)
                {
                    var name = skill.Frontmatter.Name;
                    if (map.ContainsKey(name))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate skill name '{name}' detected during SkillResolver initialization. " +
                            "Each skill must have a unique name (case-insensitive).");
                    }

                    map[name] = skill;
                }
            }

            _loaded = map;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Looks up a skill by name (case-insensitive). Awaits <see cref="EnsureLoadedAsync"/>
    /// first to ensure the skill map is populated.
    /// </summary>
    /// <param name="name">The skill name to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The <see cref="AgentSkill"/> with the given name, or <see langword="null"/> if not found.
    /// </returns>
    internal async Task<AgentSkill?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        _loaded!.TryGetValue(name, out var skill);
        return skill;
    }

    /// <summary>
    /// Returns all loaded skill names in ascending OrdinalIgnoreCase order.
    /// Must only be called after <see cref="EnsureLoadedAsync"/> has completed.
    /// </summary>
    internal IReadOnlyList<string> GetSortedNames()
    {
        var loaded = _loaded;
        if (loaded is null)
        {
            throw new InvalidOperationException(
                "SkillResolver has not been loaded yet. Call EnsureLoadedAsync first.");
        }

        var names = new List<string>(loaded.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Returns all loaded skills. Must only be called after <see cref="EnsureLoadedAsync"/>
    /// has completed.
    /// </summary>
    internal IReadOnlyDictionary<string, AgentSkill> GetAll()
    {
        return _loaded
               ?? throw new InvalidOperationException(
                   "SkillResolver has not been loaded yet. Call EnsureLoadedAsync first.");
    }
}
