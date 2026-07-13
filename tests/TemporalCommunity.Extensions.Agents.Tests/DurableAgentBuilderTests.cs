using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

public class DurableAgentBuilderTests
{
    private static DurableAgentBuilder NewBuilder(string name = "Agent") => new(name);

    private static AIFunction NewTool(string name) => AIFunctionFactory.Create(() => "ok", name);

    [Fact]
    public void Constructor_SetsName()
    {
        var builder = NewBuilder("MyAgent");
        Assert.Equal("MyAgent", builder.Name);
    }

    [Fact]
    public void Constructor_NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DurableAgentBuilder(null!));
    }

    [Fact]
    public void Constructor_WhitespaceName_Throws()
    {
        Assert.Throws<ArgumentException>(() => new DurableAgentBuilder("   "));
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var builder = NewBuilder();
        Assert.Null(builder.Description);
        Assert.Null(builder.Instructions);
        Assert.Null(builder.ChatClient);
        Assert.Null(builder.ChatOptions);
        Assert.Null(builder.TimeToLive);
        Assert.Null(builder.ApprovalTimeout);
        Assert.Null(builder.ActivityTimeout);
        Assert.Null(builder.HeartbeatTimeout);
        Assert.Null(builder.RetryPolicy);
        Assert.Null(builder.MaxEntryCount);
        Assert.Null(builder.HistoryReducer);
        Assert.Equal(20, builder.MaxToolCallsPerTurn);
        Assert.Empty(builder.ToolRegistrations);
        Assert.Empty(builder.ContextProviderFactories);
    }

    // ── Tools ──────────────────────────────────────────────────────────

    [Fact]
    public void AddTool_Concrete_RegistersByName()
    {
        var builder = NewBuilder();
        var tool = NewTool("lookup");
        builder.AddTool(tool);

        Assert.Single(builder.ToolRegistrations);
        Assert.Equal("lookup", builder.ToolRegistrations[0].Name);
    }

    [Fact]
    public void AddTool_Concrete_ReturnsSameBuilder()
    {
        var builder = NewBuilder();
        Assert.Same(builder, builder.AddTool(NewTool("t")));
    }

    [Fact]
    public void AddTool_Concrete_DuplicateName_Throws()
    {
        var builder = NewBuilder("Agent1");
        builder.AddTool(NewTool("dup"));
        var ex = Assert.Throws<ArgumentException>(() => builder.AddTool(NewTool("dup")));
        Assert.Contains("dup", ex.Message);
        Assert.Contains("Agent1", ex.Message);
    }

    [Fact]
    public void AddTool_Concrete_DuplicateName_CaseInsensitive_Throws()
    {
        var builder = NewBuilder();
        builder.AddTool(NewTool("MyTool"));
        Assert.Throws<ArgumentException>(() => builder.AddTool(NewTool("mytool")));
    }

    [Fact]
    public void AddTool_NullTool_Throws()
    {
        var builder = NewBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.AddTool((AIFunction)null!));
    }

    [Fact]
    public void AddTool_Factory_RegistersByExplicitName()
    {
        var builder = NewBuilder();
        builder.AddTool("factory_tool", _ => NewTool("factory_tool"));

        Assert.Single(builder.ToolRegistrations);
        Assert.Equal("factory_tool", builder.ToolRegistrations[0].Name);
    }

    [Fact]
    public void AddTool_Factory_DuplicateName_Throws()
    {
        var builder = NewBuilder();
        builder.AddTool("dup", _ => NewTool("dup"));
        Assert.Throws<ArgumentException>(() =>
            builder.AddTool("dup", _ => NewTool("dup")));
    }

    [Fact]
    public void AddTool_Factory_NullName_Throws()
    {
        var builder = NewBuilder();
        Assert.Throws<ArgumentNullException>(() =>
            builder.AddTool(null!, _ => NewTool("x")));
    }

    [Fact]
    public void AddTool_Factory_EmptyName_Throws()
    {
        var builder = NewBuilder();
        Assert.Throws<ArgumentException>(() =>
            builder.AddTool(string.Empty, _ => NewTool("x")));
    }

    [Fact]
    public void AddTool_Factory_NullFactory_Throws()
    {
        var builder = NewBuilder();
        Assert.Throws<ArgumentNullException>(() =>
            builder.AddTool("name", null!));
    }

    [Fact]
    public void AddTools_Multiple_RegistersAllInOrder()
    {
        var builder = NewBuilder();
        builder.AddTools(NewTool("a"), NewTool("b"), NewTool("c"));

        Assert.Equal(3, builder.ToolRegistrations.Count);
        Assert.Equal("a", builder.ToolRegistrations[0].Name);
        Assert.Equal("b", builder.ToolRegistrations[1].Name);
        Assert.Equal("c", builder.ToolRegistrations[2].Name);
    }

    [Fact]
    public void AddTools_NullArray_Throws()
    {
        var builder = NewBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.AddTools(null!));
    }

    [Fact]
    public void AddTool_ConfigureCallback_Runs()
    {
        var builder = NewBuilder();
        builder.AddTool(NewTool("write"), opts => opts.NoRetry());

        var registration = builder.ToolRegistrations[0];
        Assert.NotNull(registration.Options.RetryPolicy);
        Assert.Equal(1, registration.Options.RetryPolicy!.MaximumAttempts);
    }

    // ── Context providers ───────────────────────────────────────────────

    [Fact]
    public void AddContextProvider_Instance_Registers()
    {
        var builder = NewBuilder();
        var provider = new TestContextProvider();
        builder.AddContextProvider(provider);

        Assert.Single(builder.ContextProviderFactories);
        Assert.Same(provider, builder.ContextProviderFactories[0](null!));
    }

    [Fact]
    public void AddContextProvider_NullInstance_Throws()
    {
        var builder = NewBuilder();
        Assert.Throws<ArgumentNullException>(() =>
            builder.AddContextProvider((AIContextProvider)null!));
    }

    [Fact]
    public void AddContextProvider_Factory_Registers()
    {
        var builder = NewBuilder();
        var provider = new TestContextProvider();
        builder.AddContextProvider(_ => provider);

        Assert.Single(builder.ContextProviderFactories);
        Assert.Same(provider, builder.ContextProviderFactories[0](null!));
    }

    [Fact]
    public void AddContextProvider_NullFactory_Throws()
    {
        var builder = NewBuilder();
        Assert.Throws<ArgumentNullException>(() =>
            builder.AddContextProvider((Func<IServiceProvider, AIContextProvider>)null!));
    }

    [Fact]
    public void AddContextProvider_ReturnsSameBuilder()
    {
        var builder = NewBuilder();
        Assert.Same(builder, builder.AddContextProvider(new TestContextProvider()));
        Assert.Same(builder, builder.AddContextProvider(_ => new TestContextProvider()));
    }

    // ── ToRegistration ──────────────────────────────────────────────────

    [Fact]
    public void ToRegistration_NullChatClient_Throws()
    {
        var builder = NewBuilder("NoClient");
        var ex = Assert.Throws<InvalidOperationException>(() => builder.ToRegistration());
        Assert.Contains("NoClient", ex.Message);
        Assert.Contains("ChatClient", ex.Message);
    }

    [Fact]
    public void ToRegistration_FlattensState()
    {
        var chatClient = new TestChatClient();
        var reducer = new Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>(list => list);
        var provider = new TestContextProvider();

        var builder = NewBuilder("Agent42");
        builder.Description = "desc";
        builder.Instructions = "instr";
        builder.ChatClient = _ => chatClient;
        builder.ChatOptions = new ChatOptions { Temperature = 0.5f };
        builder.TimeToLive = TimeSpan.FromHours(1);
        builder.ApprovalTimeout = TimeSpan.FromMinutes(30);
        builder.ActivityTimeout = TimeSpan.FromMinutes(2);
        builder.HeartbeatTimeout = TimeSpan.FromSeconds(45);
        builder.RetryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 3 };
        builder.MaxEntryCount = 500;
        builder.MaxToolCallsPerTurn = 7;
        builder.HistoryReducer = reducer;
        builder.AddTool(NewTool("t1"));
        builder.AddContextProvider(provider);

        var reg = builder.ToRegistration();

        Assert.Equal("Agent42", reg.Name);
        Assert.Equal("desc", reg.Description);
        Assert.Equal("instr", reg.Instructions);
        Assert.Same(chatClient, reg.ChatClient(null!));
        Assert.Equal(0.5f, reg.ChatOptions!.Temperature);
        Assert.Equal(TimeSpan.FromHours(1), reg.TimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(30), reg.ApprovalTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), reg.ActivityTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), reg.HeartbeatTimeout);
        Assert.Equal(3, reg.RetryPolicy!.MaximumAttempts);
        Assert.Equal(500, reg.MaxEntryCount);
        Assert.Equal(7, reg.MaxToolCallsPerTurn);
        Assert.Same(reducer, reg.HistoryReducer);
        Assert.Single(reg.Tools);
        Assert.Equal("t1", reg.Tools[0].Name);
        Assert.Single(reg.ContextProviderFactories);
        Assert.Same(provider, reg.ContextProviderFactories[0](null!));
    }

    [Fact]
    public void ToRegistration_DefaultValues()
    {
        var builder = NewBuilder();
        builder.ChatClient = _ => new TestChatClient();

        var reg = builder.ToRegistration();
        Assert.Null(reg.Description);
        Assert.Null(reg.Instructions);
        Assert.Null(reg.ChatOptions);
        Assert.Null(reg.TimeToLive);
        Assert.Null(reg.ApprovalTimeout);
        Assert.Null(reg.ActivityTimeout);
        Assert.Null(reg.HeartbeatTimeout);
        Assert.Null(reg.RetryPolicy);
        Assert.Null(reg.MaxEntryCount);
        Assert.Null(reg.HistoryReducer);
        Assert.Equal(20, reg.MaxToolCallsPerTurn);
        Assert.Empty(reg.Tools);
        Assert.Empty(reg.ContextProviderFactories);
    }

    // ── ToRegistration loop / history bound validation (S-T1-1) ──────────

    [Fact]
    public void ToRegistration_ThrowsWhenMaxToolCallsPerTurnIsZero()
    {
        // A non-positive MaxToolCallsPerTurn makes the dispatch loop body never run, so the
        // agent returns "iterations exceeded" without ever calling the LLM. Reject it at build.
        var builder = NewBuilder("ZeroLoop");
        builder.ChatClient = _ => new TestChatClient();
        builder.MaxToolCallsPerTurn = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => builder.ToRegistration());
        Assert.Contains("MaxToolCallsPerTurn", ex.Message);
        Assert.Contains("ZeroLoop", ex.Message);
    }

    [Fact]
    public void ToRegistration_ThrowsWhenMaxToolCallsPerTurnIsNegative()
    {
        var builder = NewBuilder("NegLoop");
        builder.ChatClient = _ => new TestChatClient();
        builder.MaxToolCallsPerTurn = -3;

        var ex = Assert.Throws<InvalidOperationException>(() => builder.ToRegistration());
        Assert.Contains("MaxToolCallsPerTurn", ex.Message);
    }

    [Fact]
    public void ToRegistration_ThrowsWhenMaxEntryCountIsZero()
    {
        // MaxEntryCount is nullable: null = inherit the worker-level default. A non-null,
        // non-positive value is invalid.
        var builder = NewBuilder("ZeroEntries");
        builder.ChatClient = _ => new TestChatClient();
        builder.MaxEntryCount = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => builder.ToRegistration());
        Assert.Contains("MaxEntryCount", ex.Message);
        Assert.Contains("ZeroEntries", ex.Message);
    }

    [Fact]
    public void ToRegistration_ThrowsWhenMaxEntryCountIsNegative()
    {
        var builder = NewBuilder("NegEntries");
        builder.ChatClient = _ => new TestChatClient();
        builder.MaxEntryCount = -10;

        var ex = Assert.Throws<InvalidOperationException>(() => builder.ToRegistration());
        Assert.Contains("MaxEntryCount", ex.Message);
    }

    [Fact]
    public void ToRegistration_AcceptsNullMaxEntryCount_AsInheritSentinel()
    {
        // null MaxEntryCount means "inherit the worker-level DefaultMaxEntryCount" and must NOT
        // be rejected — only a non-null, non-positive value is invalid.
        var builder = NewBuilder("InheritEntries");
        builder.ChatClient = _ => new TestChatClient();
        builder.MaxEntryCount = null;

        var reg = builder.ToRegistration(); // Should not throw.
        Assert.Null(reg.MaxEntryCount);
    }

    [Fact]
    public void ToRegistration_AcceptsPositiveMaxEntryCount()
    {
        var builder = NewBuilder("PosEntries");
        builder.ChatClient = _ => new TestChatClient();
        builder.MaxEntryCount = 1;

        var reg = builder.ToRegistration(); // Should not throw.
        Assert.Equal(1, reg.MaxEntryCount);
    }

    // ── Test helpers ────────────────────────────────────────────────────

    private sealed class TestContextProvider : AIContextProvider
    {
    }

    private sealed class TestChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
