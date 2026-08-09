using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    public async Task OpenTelemetryAgent_LiveWorkerEnrichesItsActualInvokeAgentSpan()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var mafSourceName = $"TemporalAgents.Integration.Maf.{Guid.NewGuid():N}";
        using var interveningSource = new ActivitySource(
            $"TemporalAgents.Integration.Middle.{Guid.NewGuid():N}");
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is TemporalAgentTelemetry.ActivitySourceName
                || source.Name == mafSourceName
                || source.Name == interveningSource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var taskQueue = $"agent-telemetry-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options => options.AddDurableAgent("TelemetryAgent", agent =>
            {
                agent.ChatClient = _ => new EchoChatClient();
                agent.ConfigureAgentPipeline = pipeline =>
                {
                    pipeline.UseOpenTelemetry(mafSourceName);
                    pipeline.Use(inner => new ActivityCreatingAgent(inner, interveningSource));
                };
            }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("TelemetryAgent");
            var session = await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync(
                "hello",
                session,
                new TemporalAgentRunOptions { CorrelationId = "live-worker-correlation" });
            Assert.Contains("hello", response.Text);

            var turn = Assert.Single(stopped, activity =>
                activity.Source.Name == TemporalAgentTelemetry.ActivitySourceName
                && activity.OperationName == TemporalAgentTelemetry.AgentTurnSpanName
                && Equals(
                    activity.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute),
                    "live-worker-correlation"));
            var invoke = Assert.Single(stopped, activity =>
                activity.Source.Name == mafSourceName
                && Equals(activity.GetTagItem("gen_ai.operation.name"), "invoke_agent"));
            Assert.Equal(
                "live-worker-correlation",
                turn.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute));
            Assert.Equal(
                "live-worker-correlation",
                invoke.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute));
            Assert.Equal(turn.TraceId, invoke.TraceId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

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

    [Fact]
    public async Task OpenTelemetryAgent_RetryAttemptsOwnAndDisposeDistinctWrappers()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var sourceName = $"TemporalAgents.Integration.Retry.{Guid.NewGuid():N}";
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var wrappers = new ConcurrentBag<OpenTelemetryAgent>();
        var chatClient = new FailThenSucceedChatClient(failCount: 1);
        var taskQueue = $"agent-pipeline-retry-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.EnableSearchAttributes = false;
                options.AddDurableAgent("RetryPipelineAgent", agent =>
                {
                    agent.ChatClient = _ => chatClient;
                    agent.ConfigureAgentPipeline = pipeline =>
                        pipeline.UseOpenTelemetry(sourceName, wrappers.Add);
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("RetryPipelineAgent");
            var session = await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("retry", session);

            Assert.Contains("retry", response.Text);
            Assert.True(chatClient.CallCount >= 2);
            Assert.True(wrappers.Count >= 3); // startup validation plus at least two attempts
            Assert.Equal(wrappers.Count, wrappers.Distinct(ReferenceEqualityComparer.Instance).Count());

            var countAfterAttempts = stopped.Count;
            Assert.True(countAfterAttempts > 0);
            foreach (var wrapper in wrappers)
            {
                try
                {
                    await wrapper.RunAsync([new ChatMessage(ChatRole.User, "after attempt")]);
                }
                catch (Exception)
                {
                    // Live-attempt wrappers retain their session boundary, which rejects this
                    // synthetic sessionless probe. Disposal is asserted through telemetry below.
                }
            }

            Assert.Equal(countAfterAttempts, stopped.Count);
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

    private sealed class ActivityCreatingAgent(
        AIAgent inner,
        ActivitySource source) : DelegatingAIAgent(inner)
    {
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var activity = source.StartActivity("custom.middleware");
            await foreach (var update in base.RunCoreStreamingAsync(
                messages,
                session,
                options,
                cancellationToken))
            {
                yield return update;
            }
        }
    }
}
