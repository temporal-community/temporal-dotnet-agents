using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Skills;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Skills;

/// <summary>
/// Unit tests for <see cref="SkillResolver"/>.
/// </summary>
public class SkillResolverTests
{
    // ---------------------------------------------------------------------------
    // Helper factory for inline skills
    // ---------------------------------------------------------------------------

    private static AgentInlineSkill MakeSkill(string name, string description = "test desc") =>
        new AgentInlineSkill(
            name: name,
            description: description,
            instructions: $"## {name}\nSome instructions.");

    // ---------------------------------------------------------------------------
    // FindByNameAsync — case-insensitive lookup
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task FindByNameAsync_ExactMatch_ReturnsSkill()
    {
        var skill = MakeSkill("expense-report");
        var resolver = new SkillResolver(
            [skill],
            sources: ReadOnlyCollectionFromArray<AgentSkillsSource>());

        var found = await resolver.FindByNameAsync("expense-report");
        Assert.NotNull(found);
        Assert.Equal("expense-report", found.Frontmatter.Name);
    }

    [Fact]
    public async Task FindByNameAsync_CaseInsensitive_ReturnsSkill()
    {
        var skill = MakeSkill("summarize");
        var resolver = new SkillResolver(
            [skill],
            sources: ReadOnlyCollectionFromArray<AgentSkillsSource>());

        var found = await resolver.FindByNameAsync("SUMMARIZE");
        Assert.NotNull(found);

        found = await resolver.FindByNameAsync("Summarize");
        Assert.NotNull(found);
    }

    [Fact]
    public async Task FindByNameAsync_UnknownName_ReturnsNull()
    {
        var resolver = new SkillResolver(
            [MakeSkill("skill-a")],
            sources: ReadOnlyCollectionFromArray<AgentSkillsSource>());

        var found = await resolver.FindByNameAsync("does-not-exist");
        Assert.Null(found);
    }

    [Fact]
    public async Task FindByNameAsync_EmptyResolver_ReturnsNull()
    {
        var resolver = new SkillResolver(
            [],
            sources: ReadOnlyCollectionFromArray<AgentSkillsSource>());

        var found = await resolver.FindByNameAsync("any-name");
        Assert.Null(found);
    }

    // ---------------------------------------------------------------------------
    // EnsureLoadedAsync — duplicate name throws
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task EnsureLoadedAsync_DuplicateNameDirectSkills_Throws()
    {
        var a = MakeSkill("summarize");
        var b = MakeSkill("summarize"); // same name

        var resolver = new SkillResolver(
            [a, b],
            sources: ReadOnlyCollectionFromArray<AgentSkillsSource>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.EnsureLoadedAsync());
    }

    [Fact]
    public async Task EnsureLoadedAsync_DuplicateNameAcrossSourceAndSkill_Throws()
    {
        var directSkill = MakeSkill("common-name");

        // Source that returns a skill with the same name.
        var source = new StubSkillsSource([MakeSkill("common-name")]);

        var resolver = new SkillResolver(
            [directSkill],
            [source]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.EnsureLoadedAsync());
    }

    // ---------------------------------------------------------------------------
    // Thread-safety — concurrent calls trigger only one source scan
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task FindByNameAsync_ConcurrentCalls_OnlyOneScanRuns()
    {
        var scanCount = 0;

        var source = new CountingSkillsSource(
            () => Interlocked.Increment(ref scanCount),
            [MakeSkill("concurrent-skill")]);

        var resolver = new SkillResolver(
            [],
            [source]);

        // Fire multiple concurrent requests.
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => resolver.FindByNameAsync("concurrent-skill"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All calls should have resolved to the skill.
        Assert.All(results, r => Assert.NotNull(r));

        // Source scan should have run exactly once.
        Assert.Equal(1, scanCount);
    }

    // ---------------------------------------------------------------------------
    // EnsureLoadedAsync can be called before ProvideAIContextAsync (worker restart)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task FindByNameAsync_CalledBeforeProvider_MaterialisesCorrectly()
    {
        var skill = MakeSkill("standalone");
        var resolver = new SkillResolver(
            [skill],
            sources: ReadOnlyCollectionFromArray<AgentSkillsSource>());

        // Call FindByNameAsync directly (simulates InvokeAgentTool before provider loop).
        var found = await resolver.FindByNameAsync("standalone");
        Assert.NotNull(found);
    }

    // ---------------------------------------------------------------------------
    // GetSortedNames / GetAll — only valid after EnsureLoadedAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public void GetSortedNames_BeforeLoad_Throws()
    {
        var resolver = new SkillResolver(
            [],
            sources: ReadOnlyCollectionFromArray<AgentSkillsSource>());

        Assert.Throws<InvalidOperationException>(() => resolver.GetSortedNames());
    }

    [Fact]
    public async Task GetSortedNames_AfterLoad_ReturnsSortedNames()
    {
        var skills = new AgentSkill[]
        {
            MakeSkill("zebra"),
            MakeSkill("apple"),
            MakeSkill("mango"),
        };

        var resolver = new SkillResolver(
            skills,
            sources: ReadOnlyCollectionFromArray<AgentSkillsSource>());

        await resolver.EnsureLoadedAsync();

        var sorted = resolver.GetSortedNames();
        Assert.Equal(["apple", "mango", "zebra"], sorted);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static IReadOnlyList<T> ReadOnlyCollectionFromArray<T>(params T[] items) =>
        Array.AsReadOnly(items);

    /// <summary>A stub source that returns a fixed set of skills.</summary>
    private sealed class StubSkillsSource : AgentSkillsSource
    {
        private readonly IList<AgentSkill> _skills;

        internal StubSkillsSource(IList<AgentSkill> skills) => _skills = skills;

        public override Task<IList<AgentSkill>> GetSkillsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_skills);
    }

    /// <summary>A source that counts how many times GetSkillsAsync is called.</summary>
    private sealed class CountingSkillsSource : AgentSkillsSource
    {
        private readonly Action _onScan;
        private readonly IList<AgentSkill> _skills;

        internal CountingSkillsSource(Action onScan, IList<AgentSkill> skills)
        {
            _onScan = onScan;
            _skills = skills;
        }

        public override async Task<IList<AgentSkill>> GetSkillsAsync(
            CancellationToken cancellationToken = default)
        {
            _onScan();
            // Simulate async I/O so concurrent callers have a chance to pile up.
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            return _skills;
        }
    }
}
