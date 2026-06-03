using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Skills;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests.Skills;

/// <summary>
/// Unit tests for <see cref="SkillsContextProvider"/>.
/// </summary>
public class SkillsContextProviderTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static AgentInlineSkill MakeSkill(string name, string description = "A test skill.") =>
        new AgentInlineSkill(
            name: name,
            description: description,
            instructions: $"## {name}\nInstructions for {name}.");

    private static SkillResolver MakeResolverDirect(params AgentSkill[] skills) =>
        new SkillResolver(skills, []);

    private static AIContextProvider.InvokingContext MakeContext() =>
        new AIContextProvider.InvokingContext(
            new StubAgent(),
            new StubAgentSession(),
            new AIContext { Messages = [] });

    private static string GetSystemText(AIContext ctx)
    {
        if (ctx.Messages is null)
        {
            return string.Empty;
        }

        return string.Join("",
            ctx.Messages
               .Where(m => m.Role == ChatRole.System)
               .SelectMany(m => m.Contents.OfType<TextContent>())
               .Select(t => t.Text));
    }

    // ---------------------------------------------------------------------------
    // Index injection — first call
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ProvideAIContextAsync_FirstCall_InjectsIndexAsSystemMessage()
    {
        var skill = MakeSkill("expense-report", "File expense reports.");
        var resolver = MakeResolverDirect(skill);
        var provider = new SkillsContextProvider(resolver, scriptsEnabled: false);

        var aiContext = await provider.InvokingAsync(MakeContext(), CancellationToken.None);

        Assert.NotNull(aiContext.Messages);
        var msgList = aiContext.Messages.ToList();
        Assert.Single(msgList);
        var msg = msgList[0];
        Assert.Equal(ChatRole.System, msg.Role);
        var text = string.Join("", msg.Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("<skills>", text);
        Assert.Contains("<name>expense-report</name>", text);
        Assert.Contains("File expense reports.", text);
    }

    // ---------------------------------------------------------------------------
    // StateBag carry-forward — second call does not re-scan sources
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ProvideAIContextAsync_SecondCall_DoesNotRescanSources()
    {
        var skill = MakeSkill("summarize");
        var scanCount = 0;
        var source = new CountingSkillsSource(() => scanCount++, [skill]);
        var resolver = new SkillResolver([], [source]);
        var provider = new SkillsContextProvider(resolver, scriptsEnabled: false);

        var ctx = MakeContext();

        await provider.InvokingAsync(ctx, CancellationToken.None);
        await provider.InvokingAsync(ctx, CancellationToken.None);

        // Source should only be scanned once — the resolver is shared and loaded lazily.
        Assert.Equal(1, scanCount);
    }

    // ---------------------------------------------------------------------------
    // Index is sorted alphabetically
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ProvideAIContextAsync_IndexIsSortedAlphabetically()
    {
        var skills = new AgentSkill[]
        {
            MakeSkill("zebra"),
            MakeSkill("apple"),
            MakeSkill("mango"),
        };

        var resolver = MakeResolverDirect(skills);
        var provider = new SkillsContextProvider(resolver, scriptsEnabled: false);

        var aiContext = await provider.InvokingAsync(MakeContext(), CancellationToken.None);
        var text = GetSystemText(aiContext);

        var applePos = text.IndexOf("apple", StringComparison.Ordinal);
        var mangoPos = text.IndexOf("mango", StringComparison.Ordinal);
        var zebraPos = text.IndexOf("zebra", StringComparison.Ordinal);

        Assert.True(applePos < mangoPos, "apple should appear before mango");
        Assert.True(mangoPos < zebraPos, "mango should appear before zebra");
    }

    // ---------------------------------------------------------------------------
    // StripScriptsSection — standalone helper tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void StripScriptsSection_BlockPresent_Removed()
    {
        var content = "before\n<scripts>\n<script>x</script>\n</scripts>\nafter";
        var result = SkillsContextProvider.StripScriptsSection(content);
        Assert.DoesNotContain("<scripts>", result);
        Assert.DoesNotContain("</scripts>", result);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public void StripScriptsSection_NoBlock_Unchanged()
    {
        var content = "<skills><name>foo</name></skills>";
        var result = SkillsContextProvider.StripScriptsSection(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void StripScriptsSection_MissingCloseTag_Unchanged()
    {
        var content = "text <scripts> no close tag here";
        var result = SkillsContextProvider.StripScriptsSection(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void StripScriptsSection_MissingOpenTag_Unchanged()
    {
        var content = "text </scripts> orphan close";
        var result = SkillsContextProvider.StripScriptsSection(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void StripScriptsSection_MultipleOpenTags_Unchanged()
    {
        var content = "<scripts>a</scripts><scripts>b</scripts>";
        var result = SkillsContextProvider.StripScriptsSection(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void StripScriptsSection_MultipleCloseTags_Unchanged()
    {
        var content = "<scripts>text</scripts> extra </scripts>";
        var result = SkillsContextProvider.StripScriptsSection(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void StripScriptsSection_CloseBeforeOpen_Unchanged()
    {
        // Craft a string where </scripts> appears before <scripts>.
        var content = "</scripts> then <scripts>content";
        var result = SkillsContextProvider.StripScriptsSection(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void StripScriptsSection_EmptyBlock_Removed()
    {
        var content = "before<scripts></scripts>after";
        var result = SkillsContextProvider.StripScriptsSection(content);
        Assert.DoesNotContain("<scripts>", result);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public void StripScriptsSection_TrimsOneLeadingNewline()
    {
        var content = "before\n<scripts>x</scripts>after";
        var result = SkillsContextProvider.StripScriptsSection(content);
        Assert.DoesNotContain("<scripts>", result);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    // ---------------------------------------------------------------------------
    // StateBag key
    // ---------------------------------------------------------------------------

    [Fact]
    public void StateBagKey_IsCorrectConstant()
    {
        Assert.Equal("temporal.skills_index", SkillsContextProvider.StateBagKey);
    }

    // ---------------------------------------------------------------------------
    // Stubs
    // ---------------------------------------------------------------------------

    private sealed class StubAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentSession>(new StubAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse());

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseUpdate();
            await Task.CompletedTask;
        }
    }

    private sealed class StubAgentSession : AgentSession
    {
        internal StubAgentSession()
            : base(new AgentSessionStateBag())
        {
        }
    }

    private sealed class CountingSkillsSource : AgentSkillsSource
    {
        private readonly Action _onScan;
        private readonly IList<AgentSkill> _skills;

        internal CountingSkillsSource(Action onScan, IList<AgentSkill> skills)
        {
            _onScan = onScan;
            _skills = skills;
        }

        public override Task<IList<AgentSkill>> GetSkillsAsync(
            CancellationToken cancellationToken = default)
        {
            _onScan();
            return Task.FromResult(_skills);
        }
    }
}
