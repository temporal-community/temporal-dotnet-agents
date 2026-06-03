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
        // Due to the 'in TContext' variance, IDurableToolInterceptor<DurableToolContext>
        // is assignable to IDurableToolInterceptor<AgentToolContext>.
        // This is the contravariance bonus from the 'in' annotation.
        var baseInterceptor = new StubBaseInterceptor();

        // Assign IDurableToolInterceptor<DurableToolContext> to IDurableToolInterceptor<AgentToolContext>
        IDurableToolInterceptor<AgentToolContext> agentInterceptor = baseInterceptor;
        Assert.NotNull(agentInterceptor);
    }

    private sealed class StubBaseInterceptor : IDurableToolInterceptor<DurableToolContext>
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            DurableToolContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(DurableToolDecision.Proceed());
    }
}
