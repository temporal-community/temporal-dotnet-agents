using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public class AgentPipelineIntegrationTests
{
    [Fact]
    public async Task AgentPipeline_DelegatingMiddlewareRunsAroundLiveModelStep()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var observation = new PipelineObservation();
        var taskQueue = $"agent-pipeline-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options => options.AddDurableAgent("PipelineAgent", agent =>
            {
                agent.ChatClient = _ => new EchoChatClient();
                agent.ConfigureAgentPipeline = pipeline => pipeline.Use(inner =>
                    new ObservingDelegatingAgent(inner, observation));
            }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("PipelineAgent");
            var session = await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("hello", session);
            var secondResponse = await proxy.RunAsync("again", session);

            Assert.Contains("hello", response.Text);
            Assert.Contains("again", secondResponse.Text);
            Assert.Equal(2, observation.BeforeCount);
            Assert.Equal(2, observation.AfterCount);
            Assert.Equal(3, observation.ConstructionCount); // one startup validation + two activities
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class PipelineObservation
    {
        public int BeforeCount;
        public int AfterCount;
        public int ConstructionCount;
    }

    private sealed class ObservingDelegatingAgent(
        AIAgent inner,
        PipelineObservation observation) : DelegatingAIAgent(inner)
    {
        public int ConstructionOrdinal { get; } =
            Interlocked.Increment(ref observation.ConstructionCount);

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref observation.BeforeCount);
            await foreach (var update in base.RunCoreStreamingAsync(
                messages,
                session,
                options,
                cancellationToken))
            {
                yield return update;
            }
            Interlocked.Increment(ref observation.AfterCount);
        }
    }
}
