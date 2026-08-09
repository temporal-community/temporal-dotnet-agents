using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents.Internal;
using TemporalCommunity.Extensions.Agents.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Internal;

public class AgentPipelineLifetimeTests
{
    [Fact]
    public async Task Dispose_StopsTelemetryFromExactOwnedOpenTelemetryAgent()
    {
        var sourceName = $"TemporalAgents.Tests.{Guid.NewGuid():N}";
        var started = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => Interlocked.Increment(ref started),
        };
        ActivitySource.AddActivityListener(listener);

        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var lease = AgentPipelineComposer.Compose(
            "telemetry-agent",
            NoOpAgent.Instance,
            builder => builder.UseOpenTelemetry(sourceName),
            services);

        var exactWrapper = AgentChainWalker.FindFirst<OpenTelemetryAgent>(lease.Agent);
        Assert.NotNull(exactWrapper);
        Assert.True(lease.HasOpenTelemetryAgent);

        await lease.Agent.RunAsync([new ChatMessage(ChatRole.User, "before dispose")]);
        var countBeforeDispose = Volatile.Read(ref started);
        Assert.True(countBeforeDispose > 0);

        lease.Dispose();
        lease.Dispose(); // ownership is idempotent

        await lease.Agent.RunAsync([new ChatMessage(ChatRole.User, "after dispose")]);
        Assert.Equal(countBeforeDispose, Volatile.Read(ref started));
    }
}
