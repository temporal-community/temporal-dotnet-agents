// HARNESS for docs/how-to/MAF/testing-agents.md § "Test custom middleware leaf preservation".
//
// This compiles the wrapper identity assertion; it is not discovered or executed as a test.
using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Testing;
using Xunit;

namespace DocSnippets.TestingAgents;

// BEGIN SNIPPET docs/how-to/MAF/testing-agents.md#test-custom-middleware-leaf-preservation (lines 148-167)
public sealed class MiddlewareTests
{
    private sealed class TestMiddleware(AIAgent inner) : DelegatingAIAgent(inner)
    {
        internal AIAgent ForwardedAgent => InnerAgent;
    }

    [Fact]
    public void Middleware_PreservesSuppliedInnerAgent()
    {
        var inner = NoOpAgent.Instance;
        var middleware = new TestMiddleware(inner);

        Assert.Same(inner, middleware.ForwardedAgent);
    }
}
// END SNIPPET docs/how-to/MAF/testing-agents.md#test-custom-middleware-leaf-preservation
