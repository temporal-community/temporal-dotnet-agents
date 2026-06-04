using Temporalio.Extensions.Agents.Skills;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests.Skills;

/// <summary>
/// Unit tests for <see cref="SkillsMarkdownParser"/>.
/// </summary>
public class SkillsMarkdownParserTests
{
    // ---------------------------------------------------------------------------
    // Happy path
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryParse_ValidSkill_ReturnsNameAndDescription()
    {
        var content = "---\nname: expense-report\ndescription: File expense reports.\n---\n## Instructions\nDo stuff.";
        var result = SkillsMarkdownParser.TryParse(content);

        Assert.NotNull(result);
        Assert.Equal("expense-report", result.Name);
        Assert.Equal("File expense reports.", result.Description);
        Assert.Null(result.License);
        Assert.Null(result.Compatibility);
    }

    [Fact]
    public void TryParse_AllOptionalFields_CapturedCorrectly()
    {
        var content = "---\nname: billing-tool\ndescription: Manage billing.\nlicense: Apache-2.0\ncompatibility: Requires net8\n---\n## Body";
        var result = SkillsMarkdownParser.TryParse(content);

        Assert.NotNull(result);
        Assert.Equal("billing-tool", result.Name);
        Assert.Equal("Manage billing.", result.Description);
        Assert.Equal("Apache-2.0", result.License);
        Assert.Equal("Requires net8", result.Compatibility);
    }

    [Fact]
    public void TryParse_DescriptionWithEmbeddedColon_SplitsOnFirstColonOnly()
    {
        // "description: Use for: billing" — the value should be "Use for: billing"
        var content = "---\nname: billing-tool\ndescription: Use for: billing\n---\n";
        var result = SkillsMarkdownParser.TryParse(content);

        Assert.NotNull(result);
        Assert.Equal("Use for: billing", result.Description);
    }

    [Fact]
    public void TryParse_CrlfLineEndings_ParsesCorrectly()
    {
        var content = "---\r\nname: crlf-skill\r\ndescription: A CRLF skill.\r\n---\r\n## Body\r\n";
        var result = SkillsMarkdownParser.TryParse(content);

        Assert.NotNull(result);
        Assert.Equal("crlf-skill", result.Name);
        Assert.Equal("A CRLF skill.", result.Description);
    }

    // ---------------------------------------------------------------------------
    // Missing required fields → null
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryParse_MissingName_ReturnsNull()
    {
        var content = "---\ndescription: Some description.\n---\n";
        Assert.Null(SkillsMarkdownParser.TryParse(content));
    }

    [Fact]
    public void TryParse_MissingDescription_ReturnsNull()
    {
        var content = "---\nname: some-skill\n---\n";
        Assert.Null(SkillsMarkdownParser.TryParse(content));
    }

    // ---------------------------------------------------------------------------
    // Opening delimiter rules
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryParse_FirstLineNotDash_ReturnsNull()
    {
        var content = "name: skill\n---\ndescription: A skill.\n---\n";
        Assert.Null(SkillsMarkdownParser.TryParse(content));
    }

    [Fact]
    public void TryParse_LeadingBlankLineBeforeDash_ReturnsNull()
    {
        // MAF uses \A anchor — blank lines before --- are rejected.
        var content = "\n---\nname: skill\ndescription: A skill.\n---\n";
        Assert.Null(SkillsMarkdownParser.TryParse(content));
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsNull()
    {
        Assert.Null(SkillsMarkdownParser.TryParse(string.Empty));
    }

    // ---------------------------------------------------------------------------
    // Unclosed frontmatter
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryParse_UnclosedFrontmatter_ReturnsNull()
    {
        // Only one --- delimiter, no closing one.
        var content = "---\nname: skill\ndescription: A skill.\n## Body without closing delimiter";
        Assert.Null(SkillsMarkdownParser.TryParse(content));
    }

    // ---------------------------------------------------------------------------
    // Frontmatter grammar edge cases
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryParse_UnknownFrontmatterKeys_SilentlyIgnored()
    {
        var content = "---\nname: skill\ndescription: A skill.\nauthor: someone\nversion: 1.2.3\n---\n";
        var result = SkillsMarkdownParser.TryParse(content);

        Assert.NotNull(result);
        Assert.Equal("skill", result.Name);
    }

    [Fact]
    public void TryParse_ColonlessFrontmatterLine_ReturnsNull()
    {
        // "name expense-report" — no colon — is malformed; matches MAF YAML rejection.
        var content = "---\nname expense-report\ndescription: A skill.\n---\n";
        Assert.Null(SkillsMarkdownParser.TryParse(content));
    }

    [Fact]
    public void TryParse_DuplicateKey_FirstWins()
    {
        var content = "---\nname: first-name\ndescription: A skill.\nname: second-name\n---\n";
        var result = SkillsMarkdownParser.TryParse(content);

        Assert.NotNull(result);
        Assert.Equal("first-name", result.Name);
    }

    [Fact]
    public void TryParse_QuotedNameValue_PassesThroughToCallerRaw()
    {
        // Parser accepts quoted strings as-is; MAF validation (ArgumentException on
        // AgentSkillFrontmatter construction) is the grammar boundary, not the parser.
        var content = "---\nname: \"expense-report\"\ndescription: A skill.\n---\n";
        var result = SkillsMarkdownParser.TryParse(content);

        Assert.NotNull(result);
        // Value includes the literal quote characters — caller (FileSkillsSource) handles MAF validation.
        Assert.Equal("\"expense-report\"", result.Name);
    }

    [Fact]
    public void TryParse_BlankLineInsideFrontmatter_IsSkipped()
    {
        var content = "---\nname: skill\n\ndescription: A skill.\n---\n";
        var result = SkillsMarkdownParser.TryParse(content);

        Assert.NotNull(result);
        Assert.Equal("skill", result.Name);
        Assert.Equal("A skill.", result.Description);
    }
}
