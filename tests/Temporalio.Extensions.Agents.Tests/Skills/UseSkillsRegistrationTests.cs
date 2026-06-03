using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Skills;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests.Skills;

/// <summary>
/// Tests that <see cref="DurableAgentBuilder.UseSkills"/> registers the correct tools
/// with the correct <see cref="DurableToolOptions"/> flags.
/// </summary>
public class UseSkillsRegistrationTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static AgentInlineSkill MakeSkill(string name, string description = "desc") =>
        new AgentInlineSkill(
            name: name,
            description: description,
            instructions: $"## {name}\nInstructions.");

    private static DurableAgentRegistration BuildRegistration(
        Action<DurableAgentBuilder> configure)
    {
        var builder = new DurableAgentBuilder("TestAgent");
        builder.ChatClient = _ => new StubChatClient();
        configure(builder);
        return builder.ToRegistration();
    }

    // ---------------------------------------------------------------------------
    // UseSkills registers load_skill and read_skill_resource
    // ---------------------------------------------------------------------------

    [Fact]
    public void UseSkills_RegistersLoadSkill_AndReadSkillResource()
    {
        var reg = BuildRegistration(a =>
            a.UseSkills(s => s.AddSkill(MakeSkill("my-skill"))));

        var names = reg.Tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("load_skill", names);
        Assert.Contains("read_skill_resource", names);
    }

    // ---------------------------------------------------------------------------
    // load_skill has SkipInterceptorFlag; read_skill_resource does NOT
    // ---------------------------------------------------------------------------

    [Fact]
    public void UseSkills_LoadSkill_HasSkipInterceptorFlag()
    {
        var reg = BuildRegistration(a =>
            a.UseSkills(s => s.AddSkill(MakeSkill("my-skill"))));

        var loadSkill = reg.Tools.Single(t => t.Name == "load_skill");
        Assert.True(loadSkill.Options.SkipInterceptorFlag,
            "load_skill should have SkipInterceptorFlag=true");
    }

    [Fact]
    public void UseSkills_ReadSkillResource_DoesNotHaveSkipInterceptorFlag()
    {
        var reg = BuildRegistration(a =>
            a.UseSkills(s => s.AddSkill(MakeSkill("my-skill"))));

        var readResource = reg.Tools.Single(t => t.Name == "read_skill_resource");
        Assert.False(readResource.Options.SkipInterceptorFlag,
            "read_skill_resource should NOT have SkipInterceptorFlag");
    }

    // ---------------------------------------------------------------------------
    // run_skill_script absent without EnableScriptExecution
    // ---------------------------------------------------------------------------

    [Fact]
    public void UseSkills_WithoutEnableScriptExecution_NoRunSkillScript()
    {
        var reg = BuildRegistration(a =>
            a.UseSkills(s => s.AddSkill(MakeSkill("my-skill"))));

        var names = reg.Tools.Select(t => t.Name).ToList();
        Assert.DoesNotContain("run_skill_script", names, StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // run_skill_script present with RequireApprovalFlag when EnableScriptExecution called
    // ---------------------------------------------------------------------------

    [Fact]
    public void UseSkills_WithEnableScriptExecution_RegistersRunSkillScript()
    {
        var reg = BuildRegistration(a =>
            a.UseSkills(s =>
            {
                s.AddSkill(MakeSkill("my-skill"));
                s.EnableScriptExecution();
            }));

        var names = reg.Tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("run_skill_script", names);
    }

    [Fact]
    public void UseSkills_RunSkillScript_HasRequireApprovalFlag()
    {
        var reg = BuildRegistration(a =>
            a.UseSkills(s =>
            {
                s.AddSkill(MakeSkill("my-skill"));
                s.EnableScriptExecution();
            }));

        var runScript = reg.Tools.Single(t => t.Name == "run_skill_script");
        Assert.True(runScript.Options.RequireApprovalFlag,
            "run_skill_script should have RequireApprovalFlag=true");
    }

    [Fact]
    public void UseSkills_RunSkillScript_HasNoRetry()
    {
        var reg = BuildRegistration(a =>
            a.UseSkills(s =>
            {
                s.AddSkill(MakeSkill("my-skill"));
                s.EnableScriptExecution();
            }));

        var runScript = reg.Tools.Single(t => t.Name == "run_skill_script");
        Assert.NotNull(runScript.Options.RetryPolicy);
        Assert.Equal(1, runScript.Options.RetryPolicy!.MaximumAttempts);
    }

    // ---------------------------------------------------------------------------
    // UseSkills registers a SkillsContextProvider as a context provider
    // ---------------------------------------------------------------------------

    [Fact]
    public void UseSkills_RegistersSkillsContextProvider()
    {
        var reg = BuildRegistration(a =>
            a.UseSkills(s => s.AddSkill(MakeSkill("my-skill"))));

        // The SkillsContextProvider should be present in ContextProviderFactories.
        var stubProvider = new StubServiceProvider();
        var providers = reg.ContextProviderFactories
            .Select(f => f(stubProvider))
            .ToList();

        Assert.Contains(providers, p => p is SkillsContextProvider);
    }

    // ---------------------------------------------------------------------------
    // UseSkills — null configure throws
    // ---------------------------------------------------------------------------

    [Fact]
    public void UseSkills_NullConfigure_Throws()
    {
        var builder = new DurableAgentBuilder("TestAgent");
        builder.ChatClient = _ => new StubChatClient();
        Assert.Throws<ArgumentNullException>(() => builder.UseSkills(null!));
    }

    // ---------------------------------------------------------------------------
    // UseSkills returns builder for fluent chaining
    // ---------------------------------------------------------------------------

    [Fact]
    public void UseSkills_ReturnsSameBuilder_ForFluency()
    {
        var builder = new DurableAgentBuilder("TestAgent");
        builder.ChatClient = _ => new StubChatClient();
        var returned = builder.UseSkills(_ => { });
        Assert.Same(builder, returned);
    }

    // ---------------------------------------------------------------------------
    // load_skill tool returns "not found" for unknown name
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task LoadSkillTool_UnknownSkillName_ReturnsNotFoundMessage()
    {
        var reg = BuildRegistration(a =>
            a.UseSkills(s => s.AddSkill(MakeSkill("my-skill"))));

        var sp = new StubServiceProvider();
        var loadSkillTool = reg.Tools.Single(t => t.Name == "load_skill").Factory(sp);

        var result = await loadSkillTool.InvokeAsync(
            new AIFunctionArguments { ["name"] = "unknown-skill" });

        Assert.NotNull(result);
        var text = result.ToString()!;
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // load_skill tool strips scripts when scriptsEnabled=false
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task LoadSkillTool_ScriptsDisabled_StripsScriptsBlock()
    {
        // Use an inline skill with a script added to it — AgentInlineSkill synthesizes XML
        // that includes a <scripts> block when scripts are present.
        var skill = new AgentInlineSkill(
            name: "scripted-skill",
            description: "Skill with script.",
            instructions: "## Instructions\nTest.");
        skill.AddScript(
            name: "my-script",
            method: () => "result",
            description: "A test script.");

        var reg = BuildRegistration(a =>
            a.UseSkills(s => s.AddSkill(skill)));

        var sp = new StubServiceProvider();
        var loadSkillTool = reg.Tools.Single(t => t.Name == "load_skill").Factory(sp);

        var result = await loadSkillTool.InvokeAsync(
            new AIFunctionArguments { ["name"] = "scripted-skill" });

        var content = result?.ToString() ?? string.Empty;

        // AgentInlineSkill synthesizes XML; the <scripts> block should be removed when
        // scriptsEnabled=false.
        Assert.DoesNotContain("<scripts>", content, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // read_skill_resource returns "not found" for unknown skill
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ReadSkillResourceTool_UnknownSkill_ReturnsNotFoundMessage()
    {
        var reg = BuildRegistration(a =>
            a.UseSkills(s => s.AddSkill(MakeSkill("my-skill"))));

        var sp = new StubServiceProvider();
        var readTool = reg.Tools.Single(t => t.Name == "read_skill_resource").Factory(sp);

        var result = await readTool.InvokeAsync(new AIFunctionArguments
        {
            ["skillName"] = "no-such-skill",
            ["resourceName"] = "some-resource",
        });

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // Stubs
    // ---------------------------------------------------------------------------

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose() { }
    }

    private sealed class StubServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
