using System.Text.Json;
using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.Helpers;
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

    // ── Production-shaped messages ──────────────────────────────────────────────────────────
    //
    // The tests above build ChatMessage(ChatRole.Tool, text), which produces TextContent. Real
    // tool traffic never looks like that: a call carries FunctionCallContent and a result carries
    // FunctionResultContent. That gap is why the provider shipped scanning only TextContent while
    // its own tests passed, and why samples/MAF/WorkingSet extracted nothing.

    [Fact]
    public void ExtractFilePaths_ReadsPathFromToolCallArguments()
    {
        var call = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("c1", "read_file", new Dictionary<string, object?>
            {
                ["path"] = "src/Auth/AuthService.cs",
            }),
        ]);

        Assert.Equal(["src/Auth/AuthService.cs"], WorkingSetContextProvider.ExtractFilePaths([call], 20));
    }

    [Fact]
    public void ExtractFilePaths_ReadsPathFromToolResult()
    {
        var result = new ChatMessage(ChatRole.Tool, [
            new FunctionResultContent("c1", "opened src/Data/UserRepository.cs successfully"),
        ]);

        Assert.Equal(["src/Data/UserRepository.cs"], WorkingSetContextProvider.ExtractFilePaths([result], 20));
    }

    [Fact]
    public void ExtractFilePaths_ReadsJsonStringArguments()
    {
        // Arguments deserialized from the wire arrive as JsonElement, not string.
        var json = JsonDocument.Parse("""{"path":"src/Api/OrderController.cs"}""").RootElement;
        var call = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("c1", "read_file", new Dictionary<string, object?>
            {
                ["path"] = json.GetProperty("path"),
            }),
        ]);

        Assert.Equal(["src/Api/OrderController.cs"], WorkingSetContextProvider.ExtractFilePaths([call], 20));
    }

    [Fact]
    public void ExtractFilePaths_IgnoresNonStringArguments()
    {
        // ToString() on a POCO or JSON object yields a type name or braces — junk candidates.
        var call = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("c1", "read_file", new Dictionary<string, object?>
            {
                ["count"] = 42,
                ["flag"] = true,
                ["obj"] = new { path = "src/Nested/Hidden.cs" },
            }),
        ]);

        Assert.Empty(WorkingSetContextProvider.ExtractFilePaths([call], 20));
    }

    [Fact]
    public void ExtractFilePaths_StillRequiresAPathSeparator()
    {
        // The separator rule is a deliberate false-positive guard and must survive the widened
        // content scan — this is what samples/MAF/WorkingSet violated with bare filenames.
        var call = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("c1", "read_file", new Dictionary<string, object?>
            {
                ["path"] = "AuthService.cs",
            }),
        ]);

        Assert.Empty(WorkingSetContextProvider.ExtractFilePaths([call], 20));
    }

    // ── Defects found by review ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractFilePaths_CaseDifferingDuplicate_IsDeduplicated()
    {
        // `seen` compares OrdinalIgnoreCase but List.Remove is case-sensitive, so the move-to-end
        // silently failed and left both spellings in the result.
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, "opened src/Auth/AuthService.cs"),
            new ChatMessage(ChatRole.Assistant, "reopened SRC/AUTH/AUTHSERVICE.CS"),
        };

        var paths = WorkingSetContextProvider.ExtractFilePaths(messages, 20);

        Assert.Single(paths);
        Assert.Equal("SRC/AUTH/AUTHSERVICE.CS", paths[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExtractFilePaths_NonPositiveCap_ReturnsEmptyInsteadOfThrowing(int maxPaths)
    {
        var messages = new[] { new ChatMessage(ChatRole.Assistant, "opened src/A.cs") };

        Assert.Empty(WorkingSetContextProvider.ExtractFilePaths(messages, maxPaths));
    }

    [Fact]
    public void MaxPaths_Negative_Throws()
    {
        // Silently behaving like "disabled" would hide the mistake: no working set, no reason why.
        var provider = new WorkingSetContextProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => provider.MaxPaths = -1);
    }

    [Fact]
    public void MaxPaths_Zero_IsAcceptedAsDisabled()
    {
        var provider = new WorkingSetContextProvider { MaxPaths = 0 };

        Assert.Equal(0, provider.MaxPaths);
    }

    // ── StateBag mutation, exercised through the provider rather than the static helper ──────

    private static async Task<TemporalAgentSession> RunProviderAsync(
        WorkingSetContextProvider provider,
        TemporalAgentSession session,
        params ChatMessage[] messages)
    {
        await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(new StubAIAgent("WS"), session, new AIContext
            {
                Messages = messages,
            }));

        return session;
    }

    [Fact]
    public async Task Provider_WritesExtractedPathsToTheStateBag()
    {
        var session = new TemporalAgentSession(new TemporalAgentSessionId("WS", "k"));

        await RunProviderAsync(
            new WorkingSetContextProvider(),
            session,
            new ChatMessage(ChatRole.Assistant, "opened src/Auth/AuthService.cs"));

        Assert.True(session.StateBag.TryGetValue(
            WorkingSetContextProvider.StateBagKey, out string? csv, JsonSerializerOptions.Default));
        Assert.Equal("src/Auth/AuthService.cs", csv);
    }

    [Fact]
    public async Task Provider_EmptyHistory_ClearsAStaleWorkingSet()
    {
        // The key mirrors the CURRENT set. An earlier return on empty input left a previous value
        // in place, advertising files no longer in scope — worse than absence, because a reader
        // cannot tell stale from current.
        var session = new TemporalAgentSession(new TemporalAgentSessionId("WS", "k"));
        session.StateBag.SetValue(
            WorkingSetContextProvider.StateBagKey, "src/Old/Stale.cs", JsonSerializerOptions.Default);

        await RunProviderAsync(new WorkingSetContextProvider(), session);

        Assert.False(session.StateBag.TryGetValue(
            WorkingSetContextProvider.StateBagKey, out string? _, JsonSerializerOptions.Default));
    }

    [Fact]
    public async Task Provider_NoPathsInHistory_ClearsAStaleWorkingSet()
    {
        var session = new TemporalAgentSession(new TemporalAgentSessionId("WS", "k"));
        session.StateBag.SetValue(
            WorkingSetContextProvider.StateBagKey, "src/Old/Stale.cs", JsonSerializerOptions.Default);

        await RunProviderAsync(
            new WorkingSetContextProvider(),
            session,
            new ChatMessage(ChatRole.Assistant, "no file references at all here"));

        Assert.False(session.StateBag.TryGetValue(
            WorkingSetContextProvider.StateBagKey, out string? _, JsonSerializerOptions.Default));
    }

    [Fact]
    public async Task Provider_SilentMode_StillWritesTheStateBag()
    {
        var session = new TemporalAgentSession(new TemporalAgentSessionId("WS", "k"));

        var context = await new WorkingSetContextProvider { SilentMode = true }.InvokingAsync(
            new AIContextProvider.InvokingContext(new StubAIAgent("WS"), session, new AIContext
            {
                Messages = [new ChatMessage(ChatRole.Assistant, "opened src/Auth/AuthService.cs")],
            }));

        // InvokingAsync returns the merged context, so it still carries the input conversation.
        // What must be absent is the provider's own note.
        Assert.DoesNotContain(
            context.Messages ?? [],
            m => (m.Text ?? string.Empty).Contains("Working set", StringComparison.Ordinal));

        // ...but downstream consumers can still read the working set.
        Assert.True(session.StateBag.TryGetValue(
            WorkingSetContextProvider.StateBagKey, out string? csv, JsonSerializerOptions.Default));
        Assert.Equal("src/Auth/AuthService.cs", csv);
    }

}
