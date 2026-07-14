using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Skills;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillsBuilder"/> and its MAF-native file-skill registration path.
/// </summary>
public class SkillsBuilderTests
{
    // ---------------------------------------------------------------------------
    // AddSkillsFromDirectory — script safety gates
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddSkillsFromDirectory_WithRunnerWithoutEnableScriptExecution_ThrowsWhenBuilt()
    {
        var builder = new SkillsBuilder();
        AgentFileSkillScriptRunner runner = (_, _, _, _, _) => Task.FromResult<object?>(null);

        builder.AddSkillsFromDirectory(Path.GetTempPath(), runner: runner);

        Assert.Throws<InvalidOperationException>(() => builder.BuildResolver());
    }

    [Fact]
    public void AddSkillsFromDirectory_WithScriptExtensionsButNoRunner_Throws()
    {
        var builder = new SkillsBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.AddSkillsFromDirectory(
            Path.GetTempPath(),
            configure: options => options.AllowedScriptExtensions = [".py"]));
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

    [Fact]
    public async Task AddSkillsFromDirectory_DiscoversNativeResourcesButSuppressesScriptsWithoutRunner()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sb-native-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var skillDir = Path.Combine(root, "native-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                "---\nname: native-skill\ndescription: A native test skill.\n---\n## Body\nInstructions.");
            File.WriteAllText(Path.Combine(skillDir, "notes.txt"), "Resource content");
            File.WriteAllText(Path.Combine(skillDir, "run.py"), "print('not discovered')");

            var builder = new SkillsBuilder();
            builder.AddSkillsFromDirectory(root);
            using var resolver = builder.BuildResolver();

            var skill = await resolver.FindByNameAsync("native-skill");
            Assert.NotNull(skill);
            Assert.NotNull(await skill.GetResourceAsync("notes.txt"));
            Assert.Null(await skill.GetScriptAsync("run.py"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddSkillsFromDirectory_WithRunnerAndEnableScriptExecution_DiscoversScripts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sb-native-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var skillDir = Path.Combine(root, "script-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                "---\nname: script-skill\ndescription: A script test skill.\n---\n## Body\nInstructions.");
            File.WriteAllText(Path.Combine(skillDir, "run.py"), "print('durable script')");

            AgentFileSkillScriptRunner runner = (_, _, _, _, _) => Task.FromResult<object?>(null);
            var builder = new SkillsBuilder();
            builder.AddSkillsFromDirectory(root, runner: runner).EnableScriptExecution();
            using var resolver = builder.BuildResolver();

            var skill = await resolver.FindByNameAsync("script-skill");
            Assert.NotNull(skill);
            Assert.NotNull(await skill.GetScriptAsync("run.py"));
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
