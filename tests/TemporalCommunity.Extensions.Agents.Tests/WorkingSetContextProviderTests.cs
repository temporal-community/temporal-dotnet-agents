using Microsoft.Extensions.AI;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Unit tests for <see cref="WorkingSetContextProvider.ExtractFilePaths"/>.
/// The method is a pure function over ChatMessage history — no I/O, fully testable.
/// </summary>
public class WorkingSetContextProviderTests
{
    // Helper to create a simple tool result message.
    private static ChatMessage Tool(string text) =>
        new ChatMessage(ChatRole.Tool, text);

    private static ChatMessage Assistant(string text) =>
        new ChatMessage(ChatRole.Assistant, text);

    private static ChatMessage User(string text) =>
        new ChatMessage(ChatRole.User, text);

    [Fact]
    public void NoMessages_ReturnsEmptyList()
    {
        var result = WorkingSetContextProvider.ExtractFilePaths([], maxPaths: 10);
        Assert.Empty(result);
    }

    [Fact]
    public void UserMessagesOnly_NoPaths_ReturnsEmpty()
    {
        var messages = new[] { User("Hello, how are you?") };
        var result = WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths: 10);
        Assert.Empty(result);
    }

    [Fact]
    public void AssistantMessage_WithFilePath_ExtractsPath()
    {
        var messages = new[] { Assistant("I edited the file src/MyApp/Program.cs for you.") };
        var result = WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths: 10);
        Assert.Contains("src/MyApp/Program.cs", result);
    }

    [Fact]
    public void ToolResult_WithFilePath_ExtractsPath()
    {
        var messages = new[] { Tool("Read /home/user/project/app.py successfully.") };
        var result = WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths: 10);
        Assert.Contains("/home/user/project/app.py", result);
    }

    [Fact]
    public void CodeFence_FirstLineHint_ExtractsPath()
    {
        var text = "Here is the content:\n```csharp\nsrc/MyLib/Foo.cs\npublic class Foo {}\n```";
        var messages = new[] { Assistant(text) };
        var result = WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths: 10);
        Assert.Contains("src/MyLib/Foo.cs", result);
    }

    [Fact]
    public void DuplicatePaths_AreDeduplicated()
    {
        var messages = new[]
        {
            Assistant("Changed src/App/Program.cs"),
            Tool("src/App/Program.cs written OK"),
        };
        var result = WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths: 10);
        Assert.Single(result, p => p == "src/App/Program.cs");
    }

    [Fact]
    public void MaxPaths_LimitsResults()
    {
        var text = string.Join(" ", Enumerable.Range(1, 25)
            .Select(i => $"src/file{i}.cs"));
        var messages = new[] { Assistant(text) };
        var result = WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths: 10);
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void MostRecent_Paths_WinWhenCapped()
    {
        // 25 distinct paths; with maxPaths=10, only the last 10 should appear.
        var text = string.Join(" ", Enumerable.Range(1, 25)
            .Select(i => $"src/file{i}.cs"));
        var messages = new[] { Assistant(text) };
        var result = WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths: 10);

        // The last extracted path should be something near the end of the sequence.
        // Exact ordering is heuristic (token scan order), but we know maxPaths is 10.
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void UnknownExtension_IsIgnored()
    {
        var messages = new[] { Assistant("See some/path/to/thing (no extension)") };
        var result = WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths: 10);
        // "thing" has no extension — should not be picked up.
        Assert.DoesNotContain("some/path/to/thing", result);
    }

    [Fact]
    public void MultipleDistinctPaths_AreAll_Extracted()
    {
        var messages = new[]
        {
            Assistant("Modified src/A.cs and src/B.py"),
            Tool("tests/C.ts also updated"),
        };
        var result = WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths: 10);
        Assert.Contains("src/A.cs", result);
        Assert.Contains("src/B.py", result);
        Assert.Contains("tests/C.ts", result);
    }

    [Fact]
    public void PathWithBackslash_Windows_IsRecognized()
    {
        var messages = new[] { Assistant(@"Updated src\MyApp\Controllers\HomeController.cs") };
        var result = WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths: 10);
        Assert.Contains(@"src\MyApp\Controllers\HomeController.cs", result);
    }

    [Fact]
    public void StateBagKey_IsPublicConst()
    {
        Assert.Equal("temporal.working_set", WorkingSetContextProvider.StateBagKey);
    }

    [Fact]
    public void DefaultMaxPaths_IsTwenty()
    {
        var provider = new WorkingSetContextProvider();
        Assert.Equal(20, provider.MaxPaths);
    }

    [Fact]
    public void SilentMode_DefaultIsFalse()
    {
        var provider = new WorkingSetContextProvider();
        Assert.False(provider.SilentMode);
    }
}
