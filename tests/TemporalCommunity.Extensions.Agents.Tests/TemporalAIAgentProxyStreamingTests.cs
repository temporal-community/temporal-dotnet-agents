using FakeItEasy;
using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Session;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

public class TemporalAIAgentProxyStreamingTests
{
    [Fact]
    public async Task RunStreamingAsync_ThrowsNotSupportedException()
    {
        var agent = new TemporalAIAgentProxy("TestAgent", A.Fake<ITemporalAgentClient>());

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("Hello"))
            {
            }
        });
    }

}
