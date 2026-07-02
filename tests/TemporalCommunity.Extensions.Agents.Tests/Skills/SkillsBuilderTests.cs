using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Skills;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillsBuilder"/> — focuses on the native SKILL.md scanner path
/// (AddSkillsFromDirectory) and its NotSupportedException guards.
/// </summary>
public class SkillsBuilderTests
{
    // ---------------------------------------------------------------------------
    // AddSkillsFromDirectory — NotSupportedException guards
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddSkillsFromDirectory_WithNonNullRunner_ThrowsNotSupportedException()
    {
        var builder = new SkillsBuilder();
        // AgentFileSkillScriptRunner is a delegate — create any non-null instance.
        AgentFileSkillScriptRunner runner = (_, _, _, _, _) => Task.FromResult<object?>(null);

        Assert.Throws<NotSupportedException>(() =>
            builder.AddSkillsFromDirectory(Path.GetTempPath(), runner: runner));
    }

    [Fact]
    public void AddSkillsFromDirectory_WithNonNullConfigure_ThrowsNotSupportedException()
    {
        var builder = new SkillsBuilder();

        Assert.Throws<NotSupportedException>(() =>
            builder.AddSkillsFromDirectory(Path.GetTempPath(), configure: _ => { }));
    }

    // ---------------------------------------------------------------------------
    // AddSkillsFromDirectory — path validation
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddSkillsFromDirectory_NullPath_ThrowsArgumentNullException()
    {
        var builder = new SkillsBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.AddSkillsFromDirectory(null!));
    }

    [Fact]
    public void AddSkillsFromDirectory_EmptyPath_ThrowsArgumentException()
    {
        var builder = new SkillsBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddSkillsFromDirectory(string.Empty));
    }

    [Fact]
    public void AddSkillsFromDirectory_WhitespacePath_ThrowsArgumentException()
    {
        var builder = new SkillsBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddSkillsFromDirectory("   "));
    }

    // ---------------------------------------------------------------------------
    // AddSkillsFromDirectory — fluent chaining
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddSkillsFromDirectory_ReturnsSameBuilder_ForFluency()
    {
        var builder = new SkillsBuilder();
        var returned = builder.AddSkillsFromDirectory(Path.GetTempPath());
        Assert.Same(builder, returned);
    }

    // ---------------------------------------------------------------------------
    // AddSkillsFromDirectory — skills resolved via FindByNameAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AddSkillsFromDirectory_ValidDirectory_SkillsResolvedByName()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // Write a valid SKILL.md into a subdirectory.
            var skillDir = Path.Combine(root, "my-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                "---\nname: my-skill\ndescription: A test skill.\n---\n## Body\nInstructions.");

            var builder = new SkillsBuilder();
            builder.AddSkillsFromDirectory(root);
            var resolver = builder.BuildResolver();

            var found = await resolver.FindByNameAsync("my-skill");
            Assert.NotNull(found);
            Assert.Equal("my-skill", found.Frontmatter.Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // AddSkill / AddSkills
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddSkill_NullSkill_ThrowsArgumentNullException()
    {
        var builder = new SkillsBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.AddSkill(null!));
    }

    [Fact]
    public async Task AddSkill_ValidSkill_ResolvedByName()
    {
        var builder = new SkillsBuilder();
        var skill = new AgentInlineSkill("test-skill", "desc", "## Instructions\nOk.");
        builder.AddSkill(skill);
        var resolver = builder.BuildResolver();

        var found = await resolver.FindByNameAsync("test-skill");
        Assert.NotNull(found);
    }
}
