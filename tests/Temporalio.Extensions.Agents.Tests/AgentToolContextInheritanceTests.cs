using Temporalio.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Verifies the inheritance relationship between <see cref="AgentToolContext"/> and
/// <see cref="DurableToolContext"/>, and confirms that <c>AgentToolContext</c> satisfies the
/// <see cref="IDurableToolInterceptor{TContext}"/> generic constraint.
/// </summary>
public class AgentToolContextInheritanceTests
{
    [Fact]
    public void AgentToolContext_IsAssignableTo_DurableToolContext()
    {
        Assert.True(typeof(DurableToolContext).IsAssignableFrom(typeof(AgentToolContext)));
    }

    [Fact]
    public void AgentToolContext_SatisfiesGenericConstraint_ForIDurableToolInterceptor()
    {
        // IDurableToolInterceptor<TContext> where TContext : DurableToolContext
        // AgentToolContext : DurableToolContext — so it must satisfy the constraint.
        var interfaceType = typeof(IDurableToolInterceptor<AgentToolContext>);
        Assert.NotNull(interfaceType);
        // Verify IAgentToolInterceptor is that interface (i.e., it extends it correctly).
        Assert.True(typeof(IDurableToolInterceptor<AgentToolContext>)
            .IsAssignableFrom(typeof(IAgentToolInterceptor)));
    }

    [Fact]
    public void AgentName_And_StateBag_AreOnDerivedType_Only()
    {
        // These properties should NOT exist on the base DurableToolContext.
        Assert.Null(typeof(DurableToolContext).GetProperty("AgentName"));
        Assert.Null(typeof(DurableToolContext).GetProperty("StateBag"));

        // But they SHOULD exist on AgentToolContext.
        Assert.NotNull(typeof(AgentToolContext).GetProperty("AgentName"));
        Assert.NotNull(typeof(AgentToolContext).GetProperty("StateBag"));
    }

    [Fact]
    public void BaseProperties_AreReadableFrom_AgentToolContextInstance()
    {
        var ctx = new AgentToolContext
        {
            AgentName = "OrderAgent",
            ToolName = "apply_refund",
            Arguments = new Dictionary<string, object?> { ["amount"] = 29.99 },
            CallId = "call-001",
            SessionId = "ta-orderagent-abc123",
        };

        // Access via base type reference — confirms inheritance, not shadowing.
        DurableToolContext baseCtx = ctx;
        Assert.Equal("apply_refund", baseCtx.ToolName);
        Assert.Equal("call-001", baseCtx.CallId);
        Assert.Equal("ta-orderagent-abc123", baseCtx.SessionId);
        Assert.NotNull(baseCtx.Arguments);
        Assert.Equal(29.99, baseCtx.Arguments["amount"]);
    }

    [Fact]
    public void IDurableToolInterceptor_Contravariance_Allows_BaseContextInterceptor()
    {
        // With 'in TContext', IDurableToolInterceptor<DurableToolContext> is assignable
        // to IDurableToolInterceptor<AgentToolContext> — NOT the other direction.
        // A base-context interceptor handles both the base and all derived contexts.
        var baseInterceptor = new StubBaseInterceptor();

        // Correct contravariance direction: the base-typed interceptor can be assigned
        // to a variable of the more-derived context type.
        IDurableToolInterceptor<AgentToolContext> agentInterceptor = baseInterceptor;
        Assert.NotNull(agentInterceptor);

        // The reverse does NOT hold — an AgentToolContext interceptor is NOT assignable
        // to IDurableToolInterceptor<DurableToolContext>.
        Assert.False(
            typeof(IDurableToolInterceptor<DurableToolContext>)
                .IsAssignableFrom(typeof(IAgentToolInterceptor)));
    }

    [Fact]
    public void BaseContextInterceptor_CanBeRegistered_ViaAddToolInterceptor()
    {
        // Widened AddToolInterceptor overload accepts IDurableToolInterceptor<AgentToolContext>.
        // Since IDurableToolInterceptor<DurableToolContext> IS-A IDurableToolInterceptor<AgentToolContext>
        // via contravariance, a base-context interceptor can be registered directly.
        var builder = new DurableAgentBuilder("TestAgent");
        builder.ChatClient = _ => new StubChatClient();

        // This compiles only because AddToolInterceptor accepts
        // Func<IServiceProvider, IDurableToolInterceptor<AgentToolContext>>,
        // and StubBaseInterceptor (: IDurableToolInterceptor<DurableToolContext>) is
        // assignable to IDurableToolInterceptor<AgentToolContext> via contravariance.
        builder.AddToolInterceptor(_ => new StubBaseInterceptor());

        var registration = builder.ToRegistration();
        Assert.NotNull(registration.ToolInterceptorFactory);
    }

    private sealed class StubBaseInterceptor : IDurableToolInterceptor<DurableToolContext>
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            DurableToolContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(DurableToolDecision.Proceed());
    }

    private sealed class StubChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Microsoft.Extensions.AI.ChatClientMetadata Metadata => new("stub");
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Microsoft.Extensions.AI.ChatResponse([]));
        public System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            System.Linq.AsyncEnumerable.Empty<Microsoft.Extensions.AI.ChatResponseUpdate>();
        public TService? GetService<TService>(object? key = null) where TService : class => null;
        public object? GetService(System.Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
