using Microsoft.Agents.AI;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

public class TemporalAIAgentStreamingTests
{
    [Fact]
    public async Task RunStreamingAsync_ThrowsNotSupportedException()
    {
        var agent = new TemporalAIAgent("TestAgent");

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("Hello"))
            {
            }
        });
    }
}
