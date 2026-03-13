using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class MarkdownCodeFenceHelperTests
{
    [Fact]
    public void PlainJson_ReturnedUnchanged()
    {
        var json = """{"name":"Alice","age":30}""";
        Assert.Equal(json, MarkdownCodeFenceHelper.StripMarkdownCodeFences(json));
    }

    [Fact]
    public void JsonWrappedInFences_StripsCorrectly()
    {
        var input = """
            ```json
            {"name":"Alice"}
            ```
            """;
        Assert.Equal("""{"name":"Alice"}""", MarkdownCodeFenceHelper.StripMarkdownCodeFences(input));
    }

    [Fact]
    public void FencesWithoutLanguageTag_StripsCorrectly()
    {
        var input = """
            ```
            {"value":42}
            ```
            """;
        Assert.Equal("""{"value":42}""", MarkdownCodeFenceHelper.StripMarkdownCodeFences(input));
    }

    [Fact]
    public void Array_ExtractsCorrectly()
    {
        var input = """
            ```json
            [1, 2, 3]
            ```
            """;
        Assert.Equal("[1, 2, 3]", MarkdownCodeFenceHelper.StripMarkdownCodeFences(input));
    }

    [Fact]
    public void NestedBraces_ExtractsBalanced()
    {
        var json = """{"outer":{"inner":{"deep":true}}}""";
        var input = $"```json\n{json}\n```";
        Assert.Equal(json, MarkdownCodeFenceHelper.StripMarkdownCodeFences(input));
    }

    [Fact]
    public void BracesInsideStrings_HandledCorrectly()
    {
        var json = """{"message":"Use {braces} carefully"}""";
        var input = $"```json\n{json}\n```";
        Assert.Equal(json, MarkdownCodeFenceHelper.StripMarkdownCodeFences(input));
    }

    [Fact]
    public void EscapedQuotesInsideStrings_HandledCorrectly()
    {
        var json = """{"quote":"She said \"hello\""}""";
        var input = $"```json\n{json}\n```";
        Assert.Equal(json, MarkdownCodeFenceHelper.StripMarkdownCodeFences(input));
    }

    [Fact]
    public void JsonWithLeadingText_ExtractsFirstBalancedObject()
    {
        var input = "Here is the result: {\"value\":42} and more text";
        Assert.Equal("""{"value":42}""", MarkdownCodeFenceHelper.StripMarkdownCodeFences(input));
    }

    [Fact]
    public void NullInput_ReturnsNull()
    {
        Assert.Null(MarkdownCodeFenceHelper.StripMarkdownCodeFences(null!));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", MarkdownCodeFenceHelper.StripMarkdownCodeFences(""));
    }

    [Fact]
    public void WhitespaceOnly_ReturnsWhitespace()
    {
        Assert.Equal("   ", MarkdownCodeFenceHelper.StripMarkdownCodeFences("   "));
    }

    [Fact]
    public void NoJsonContent_ReturnsOriginal()
    {
        var input = "This is just plain text with no JSON.";
        Assert.Equal(input, MarkdownCodeFenceHelper.StripMarkdownCodeFences(input));
    }

    [Fact]
    public void MultilineJson_PreservesFormatting()
    {
        var json = "{\n  \"name\": \"Alice\",\n  \"age\": 30\n}";
        var input = $"```json\n{json}\n```";
        Assert.Equal(json, MarkdownCodeFenceHelper.StripMarkdownCodeFences(input));
    }
}
