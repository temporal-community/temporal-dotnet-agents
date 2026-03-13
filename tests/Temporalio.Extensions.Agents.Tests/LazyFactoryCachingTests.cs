using Microsoft.Agents.AI;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class LazyFactoryCachingTests
{
    [Fact]
    public void AgentCache_IsInitialized()
    {
        // Verify AgentActivities can be constructed with its ConcurrentDictionary cache.
        // This is a construction-time smoke test.
        var factories = new Dictionary<string, Func<IServiceProvider, AIAgent>>
        {
            ["TestAgent"] = _ => new StubAIAgent("TestAgent")
        };

        var activities = new AgentActivities(factories, null!);
        Assert.NotNull(activities);
    }
}
