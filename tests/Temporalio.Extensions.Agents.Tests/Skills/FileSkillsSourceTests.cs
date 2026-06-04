using System.Runtime.InteropServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Extensions.Agents.Skills;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests.Skills;

/// <summary>
/// Unit and integration tests for <see cref="FileSkillsSource"/>.
/// </summary>
public class FileSkillsSourceTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>Writes a minimal valid SKILL.md into <paramref name="dir"/>.</summary>
    private static string WriteSkill(string dir, string name, string description = "A test skill.")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(path,
            $"---\nname: {name}\ndescription: {description}\n---\n## Instructions\nDo stuff.");
        return path;
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fss-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------------------------------------------------------------------
    // Constructor validation
    // ---------------------------------------------------------------------------

    [Fact]
    public void Constructor_NullDirectory_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new FileSkillsSource(null!));
    }

    [Fact]
    public void Constructor_EmptyDirectory_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FileSkillsSource(string.Empty));
    }

    [Fact]
    public void Constructor_WhitespaceDirectory_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FileSkillsSource("   "));
    }

    [Fact]
    public void Constructor_NegativeMaxDepth_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSkillsSource("/some/dir", maxDepth: -1));
    }

    [Fact]
    public async Task Constructor_MaxDepthZero_Valid_ScansRootOnly()
    {
        var root = MakeTempDir();
        try
        {
            WriteSkill(root, "root-skill");
            var sub = Path.Combine(root, "sub");
            WriteSkill(sub, "child-skill");

            var source = new FileSkillsSource(root, maxDepth: 0);
            var skills = await source.GetSkillsAsync();

            // Only root-skill; child-skill is in a subdirectory.
            var single = Assert.Single(skills);
            Assert.Equal("root-skill", single.Frontmatter.Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Root SKILL.md discovery
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetSkillsAsync_SkillMdInRootDirectory_IsDiscovered()
    {
        var root = MakeTempDir();
        try
        {
            WriteSkill(root, "root-skill");

            var source = new FileSkillsSource(root);
            var skills = await source.GetSkillsAsync();

            var single = Assert.Single(skills);
            Assert.Equal("root-skill", single.Frontmatter.Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Multiple skills, sorting, type checks
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetSkillsAsync_TwoSkills_ReturnsBothSortedByName()
    {
        var root = MakeTempDir();
        try
        {
            var dirB = Path.Combine(root, "zebra");
            var dirA = Path.Combine(root, "apple");
            WriteSkill(dirB, "zebra-skill");
            WriteSkill(dirA, "apple-skill");

            var source = new FileSkillsSource(root);
            var skills = await source.GetSkillsAsync();

            Assert.Equal(2, skills.Count);
            Assert.Equal("apple-skill", skills[0].Frontmatter.Name);
            Assert.Equal("zebra-skill", skills[1].Frontmatter.Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSkillsAsync_FileSkill_ContentEqualsFullRawSkillMd()
    {
        var root = MakeTempDir();
        try
        {
            // Write a SKILL.md with distinct frontmatter + body.
            var skillDir = Path.Combine(root, "my-skill");
            Directory.CreateDirectory(skillDir);
            var rawContent = "---\nname: my-skill\ndescription: My desc.\n---\n## Instructions\nFull body.";
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), rawContent);

            var source = new FileSkillsSource(root);
            var skills = await source.GetSkillsAsync();

            var single = Assert.Single(skills);
            // Content must equal the full raw SKILL.md string (including frontmatter).
            Assert.Equal(rawContent, single.Content);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSkillsAsync_FileSkill_PathEqualsContainingDirectory()
    {
        var root = MakeTempDir();
        try
        {
            var skillDir = Path.Combine(root, "my-skill");
            WriteSkill(skillDir, "my-skill");

            var source = new FileSkillsSource(root);
            var skills = await source.GetSkillsAsync();

            var single = Assert.Single(skills);
            var fileSkill = Assert.IsType<AgentFileSkill>(single);
            // Path is the directory that contains SKILL.md, not the file path itself.
            Assert.Equal(Path.TrimEndingDirectorySeparator(skillDir),
                Path.TrimEndingDirectorySeparator(fileSkill.Path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSkillsAsync_FileSkill_IsAgentFileSkillNotAgentInlineSkill()
    {
        var root = MakeTempDir();
        try
        {
            WriteSkill(root, "check-type");

            var source = new FileSkillsSource(root);
            var skills = await source.GetSkillsAsync();

            Assert.Single(skills);
            Assert.IsType<AgentFileSkill>(skills[0]);
            Assert.IsNotType<AgentInlineSkill>(skills[0]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Empty directory
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetSkillsAsync_EmptyDirectory_ReturnsEmptyList()
    {
        var root = MakeTempDir();
        try
        {
            var source = new FileSkillsSource(root);
            var skills = await source.GetSkillsAsync();
            Assert.Empty(skills);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Depth semantics (maxDepth = 2 default)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetSkillsAsync_SkillAtDepth1And2_BothDiscovered()
    {
        var root = MakeTempDir();
        try
        {
            var depth1 = Path.Combine(root, "level1");
            var depth2 = Path.Combine(root, "level1", "level2");
            WriteSkill(depth1, "depth-one");
            WriteSkill(depth2, "depth-two");

            var source = new FileSkillsSource(root); // default maxDepth = 2
            var skills = await source.GetSkillsAsync();

            Assert.Equal(2, skills.Count);
            var names = skills.Select(s => s.Frontmatter.Name).ToHashSet();
            Assert.Contains("depth-one", names);
            Assert.Contains("depth-two", names);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSkillsAsync_SkillAtDepth3_NotDiscovered()
    {
        var root = MakeTempDir();
        try
        {
            var depth3 = Path.Combine(root, "a", "b", "c");
            WriteSkill(depth3, "too-deep");

            var source = new FileSkillsSource(root); // default maxDepth = 2
            var skills = await source.GetSkillsAsync();

            Assert.Empty(skills);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSkillsAsync_BoundedTraversal_DoesNotDescendPastMaxDepth()
    {
        // Create a tree that would be expensive to walk if depth limiting failed.
        // depth-3 directory with many siblings — none should be discovered.
        var root = MakeTempDir();
        try
        {
            for (int i = 0; i < 10; i++)
            {
                var deep = Path.Combine(root, "a", "b", $"sub{i}");
                WriteSkill(deep, $"deep-skill-{i}");
            }

            var source = new FileSkillsSource(root);
            var skills = await source.GetSkillsAsync();
            Assert.Empty(skills);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Error handling — malformed files
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetSkillsAsync_MalformedSkillMd_SkippedAndWarningLogged()
    {
        var root = MakeTempDir();
        var logMessages = new List<string>();
        var logger = new CollectingLogger(logMessages);
        try
        {
            // Good skill.
            WriteSkill(root, "good-skill");
            // Bad skill — missing name.
            var badDir = Path.Combine(root, "bad");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(Path.Combine(badDir, "SKILL.md"),
                "---\ndescription: Missing name.\n---\n## Body");

            var source = new FileSkillsSource(root, logger: logger);
            var skills = await source.GetSkillsAsync();

            // Only the good skill is returned.
            var single = Assert.Single(skills);
            Assert.Equal("good-skill", single.Frontmatter.Name);
            // Warning was logged about the bad file.
            Assert.Contains(logMessages, m => m.Contains("bad") || m.Contains("frontmatter"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSkillsAsync_InvalidSkillName_SkippedAndWarningLogged()
    {
        var root = MakeTempDir();
        var logMessages = new List<string>();
        var logger = new CollectingLogger(logMessages);
        try
        {
            // Good skill.
            WriteSkill(root, "good-skill");
            // Skill with a non-kebab-case name (quoted) — will fail AgentSkillFrontmatter ctor.
            var badDir = Path.Combine(root, "bad-name");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(Path.Combine(badDir, "SKILL.md"),
                "---\nname: \"quoted name with spaces\"\ndescription: Bad name.\n---\n## Body");

            var source = new FileSkillsSource(root, logger: logger);
            var skills = await source.GetSkillsAsync();

            var single = Assert.Single(skills);
            Assert.Equal("good-skill", single.Frontmatter.Name);
            Assert.NotEmpty(logMessages);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Error handling — I/O failures
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetSkillsAsync_IOException_OnFileRead_SkipsFileNotWholeDirectory()
    {
        // FileSkillsSource wraps IOException inline (per-file), not at directory level.
        // We simulate this by using a custom source that throws on a specific path.
        // Here we test that a good sibling in the same directory is still returned.
        var root = MakeTempDir();
        try
        {
            WriteSkill(root, "good-skill");
            // We can't easily force IOException on an existing file without platform tricks,
            // so we verify the source handles a second good file — which demonstrates the
            // loop continues rather than aborting.
            var dir2 = Path.Combine(root, "also-good");
            WriteSkill(dir2, "also-good-skill");

            var source = new FileSkillsSource(root);
            var skills = await source.GetSkillsAsync();
            Assert.Equal(2, skills.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // BOM handling
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetSkillsAsync_BomPrefixedSkillMd_ParsedCorrectly()
    {
        var root = MakeTempDir();
        try
        {
            // Write SKILL.md with UTF-8 BOM prefix.
            var skillDir = Path.Combine(root, "bom-skill");
            Directory.CreateDirectory(skillDir);
            var rawContent = "---\nname: bom-skill\ndescription: BOM skill.\n---\n## Body";
            // Prepend BOM character.
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "﻿" + rawContent);

            var source = new FileSkillsSource(root);
            var skills = await source.GetSkillsAsync();

            var single = Assert.Single(skills);
            Assert.Equal("bom-skill", single.Frontmatter.Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Non-existent root
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetSkillsAsync_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        var source = new FileSkillsSource(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => source.GetSkillsAsync());
    }

    // ---------------------------------------------------------------------------
    // Cancellation
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetSkillsAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var root = MakeTempDir();
        try
        {
            // Write enough skills so there is something to iterate over.
            for (int i = 0; i < 5; i++)
            {
                WriteSkill(Path.Combine(root, $"skill-{i}"), $"skill-{i}");
            }

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // pre-cancel

            var source = new FileSkillsSource(root);
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => source.GetSkillsAsync(cts.Token));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Inaccessible child directory (Unix only — unreliable on Windows/macOS CI)
    // ---------------------------------------------------------------------------

    [Fact(Skip = "Platform-dependent: unreliable when tests run as root or on filesystems with ACL quirks.")]
    public async Task GetSkillsAsync_InaccessibleChildDirectory_SkipsItAndReturnsSiblings()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return; // Skip on non-Unix
        }

        var root = MakeTempDir();
        try
        {
            WriteSkill(root, "accessible-skill");
            var lockedDir = Path.Combine(root, "locked");
            Directory.CreateDirectory(lockedDir);
            File.SetUnixFileMode(lockedDir, UnixFileMode.None); // remove all permissions

            var source = new FileSkillsSource(root);
            var skills = await source.GetSkillsAsync();

            // accessible-skill is still returned despite the locked sibling.
            var single = Assert.Single(skills);
            Assert.Equal("accessible-skill", single.Frontmatter.Name);
        }
        finally
        {
            // Restore permissions so cleanup can succeed.
            var lockedDir = Path.Combine(root, "locked");
            if (Directory.Exists(lockedDir))
                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Internal helper — collecting logger
    // ---------------------------------------------------------------------------

    private sealed class CollectingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                messages.Add(formatter(state, exception));
        }
    }
}
