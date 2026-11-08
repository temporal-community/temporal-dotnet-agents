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
using TemporalCommunity.Extensions.Tests.Shared;
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
            // Startup validation plus preparation, model, and completion for each turn.
            Assert.Equal(7, observation.ConstructionCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelMiddleware_MultistepTurnRunsOnlyAtEachModelBoundary(bool retryFirstAttempt)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var observation = new PipelineObservation();
        var events = new ConcurrentQueue<string>();
        var failuresRemaining = retryFirstAttempt ? 1 : 0;
        var client = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("boundary-call", "lookup", new Dictionary<string, object?>())],
            "done");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services
            .AddHostedTemporalWorker($"middleware-boundary-{Guid.NewGuid():N}")
            .AddTemporalAgents(options => options.AddDurableAgent("BoundaryAgent", agent =>
            {
                agent.ChatClient = _ => new ObservingChatClient(
                    client, events, () => Interlocked.Exchange(ref failuresRemaining, 0) == 1);
                agent.ConfigureAgentPipeline = pipeline => pipeline.Use(inner =>
                    new ObservingDelegatingAgent(inner, observation, events));
                agent.AddTool(AIFunctionFactory.Create(() =>
                {
                    events.Enqueue("tool");
                    return "found";
                }, "lookup"));
            }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("BoundaryAgent");
            var session = await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("lookup", session);

            Assert.Equal("done", response.Text);
            Assert.Equal(2, client.CallCount);
            Assert.Equal(retryFirstAttempt ? 3 : 2, observation.BeforeCount);
            Assert.Equal(2, observation.AfterCount);
            Assert.Equal(retryFirstAttempt ? 6 : 5, observation.ConstructionCount);
            string[] successfulTurn =
                ["agent-before", "transport-before", "transport-after", "agent-after", "tool",
                 "agent-before", "transport-before", "transport-after", "agent-after"];
            Assert.Equal(retryFirstAttempt
                ? new[] { "agent-before", "transport-before", "transport-failed" }.Concat(successfulTurn)
                : successfulTurn,
                events.ToArray());
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
            Assert.Equal(2, chatClient.CallCount);
            // Startup, preparation, two model attempts, and completion.
            Assert.Equal(5, wrappers.Count);
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
        PipelineObservation observation,
        ConcurrentQueue<string>? events = null) : DelegatingAIAgent(inner)
    {
        public int ConstructionOrdinal { get; } =
            Interlocked.Increment(ref observation.ConstructionCount);

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref observation.BeforeCount);
            events?.Enqueue("agent-before");
            var response = await base.RunCoreAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false);
            events?.Enqueue("agent-after");
            Interlocked.Increment(ref observation.AfterCount);
            return response;
        }
    }

    private sealed class ObservingChatClient(
        IChatClient inner,
        ConcurrentQueue<string> events,
        Func<bool> shouldFail) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            events.Enqueue("transport-before");
            if (shouldFail())
            {
                events.Enqueue("transport-failed");
                throw new InvalidOperationException("Transient model transport failure.");
            }
            var response = await base.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
            events.Enqueue("transport-after");
            return response;
        }
    }

    private sealed class ActivityCreatingAgent(
        AIAgent inner,
        ActivitySource source) : DelegatingAIAgent(inner)
    {
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            using var activity = source.StartActivity("custom.middleware");
            return await base.RunCoreAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
