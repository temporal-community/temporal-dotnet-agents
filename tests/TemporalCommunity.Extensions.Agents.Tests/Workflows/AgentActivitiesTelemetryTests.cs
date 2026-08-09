using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

public class AgentActivitiesTelemetryTests
{
    private const string OperationNameAttribute = "gen_ai.operation.name";
    private const string InvokeAgentOperation = "invoke_agent";

    [Fact]
    public async Task OpenTelemetryAgent_EnrichesActualInvokeAgentAncestorAndKeepsTemporalTurn()
    {
        var mafSourceName = $"TemporalAgents.Tests.Maf.{Guid.NewGuid():N}";
        using var customSource = new ActivitySource($"TemporalAgents.Tests.Custom.{Guid.NewGuid():N}");
        var stopped = new ConcurrentBag<Activity>();
        using var listener = CreateListener(
            source => source.Name is TemporalAgentTelemetry.ActivitySourceName
                || source.Name == mafSourceName
                || source.Name == customSource.Name,
            stopped);

        var activities = BuildActivities(opts =>
            opts.AddDurableAgent("TelemetryAgent", agent =>
            {
                agent.ChatClient = _ => new UsageReportingChatClient();
                agent.ConfigureAgentPipeline = pipeline =>
                {
                    pipeline.UseOpenTelemetry(mafSourceName);
                    pipeline.Use(inner => new ActivityCreatingAgent(inner, customSource));
                };
            }));

        await RunActivityAsync(activities, "TelemetryAgent", "corr-sampled");

        var turn = Assert.Single(stopped, a =>
            a.Source.Name == TemporalAgentTelemetry.ActivitySourceName
            && a.OperationName == TemporalAgentTelemetry.AgentTurnSpanName
            && Equals(a.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute), "corr-sampled"));
        var invokeAgent = Assert.Single(stopped, a =>
            a.Source.Name == mafSourceName
            && Equals(a.GetTagItem(OperationNameAttribute), InvokeAgentOperation));
        var custom = Assert.Single(stopped, a => a.Source.Name == customSource.Name);

        Assert.Equal("corr-sampled", turn.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute));
        Assert.Equal("corr-sampled", invokeAgent.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute));
        Assert.Equal(turn.TraceId, invokeAgent.TraceId);
        Assert.Equal(invokeAgent.TraceId, custom.TraceId);
        Assert.True(IsAncestor(turn, invokeAgent));
        Assert.True(IsAncestor(invokeAgent, custom));

        Assert.Null(turn.GetTagItem(TemporalAgentTelemetry.InputTokensAttribute));
        Assert.NotNull(invokeAgent.GetTagItem(TemporalAgentTelemetry.InputTokensAttribute));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsampledOpenTelemetryAgent_IsSafeNoOpAndDoesNotTagUnrelatedActivity(bool failProvider)
    {
        var mafSourceName = $"TemporalAgents.Tests.UnsampledMaf.{Guid.NewGuid():N}";
        using var unrelatedSource = new ActivitySource($"TemporalAgents.Tests.Unrelated.{Guid.NewGuid():N}");
        var stopped = new ConcurrentBag<Activity>();
        using var listener = CreateListener(
            source => source.Name is TemporalAgentTelemetry.ActivitySourceName
                || source.Name == unrelatedSource.Name,
            stopped);
        using var unrelated = unrelatedSource.StartActivity("unrelated");

        var activities = BuildActivities(opts =>
            opts.AddDurableAgent("UnsampledAgent", agent =>
            {
                agent.ChatClient = _ => new UsageReportingChatClient(failProvider);
                agent.ConfigureAgentPipeline = pipeline => pipeline.UseOpenTelemetry(mafSourceName);
            }));

        if (failProvider)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunActivityAsync(activities, "UnsampledAgent", "corr-unsampled"));
            Assert.Equal("provider failure", ex.Message);
        }
        else
        {
            await RunActivityAsync(activities, "UnsampledAgent", "corr-unsampled");
        }

        var turn = Assert.Single(stopped, a =>
            a.Source.Name == TemporalAgentTelemetry.ActivitySourceName
            && a.OperationName == TemporalAgentTelemetry.AgentTurnSpanName
            && Equals(a.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute), "corr-unsampled"));
        Assert.Equal("corr-unsampled", turn.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute));
        Assert.Null(unrelated?.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute));
        Assert.DoesNotContain(stopped, a => a.Source.Name == mafSourceName);
    }

    [Fact]
    public async Task NoUpstreamTelemetry_TemporalTurnOwnsUsageAttributes()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = CreateListener(
            source => source.Name == TemporalAgentTelemetry.ActivitySourceName,
            stopped);
        var activities = BuildActivities(opts =>
            opts.AddDurableAgent("FallbackAgent", agent =>
                agent.ChatClient = _ => new UsageReportingChatClient()));

        await RunActivityAsync(activities, "FallbackAgent", "corr-fallback");

        var turn = Assert.Single(stopped, a =>
            a.OperationName == TemporalAgentTelemetry.AgentTurnSpanName
            && Equals(a.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute), "corr-fallback"));
        Assert.Equal(11L, Convert.ToInt64(
            turn.GetTagItem(TemporalAgentTelemetry.InputTokensAttribute)));
        Assert.Equal(7L, Convert.ToInt64(
            turn.GetTagItem(TemporalAgentTelemetry.OutputTokensAttribute)));
        Assert.Equal(18L, Convert.ToInt64(
            turn.GetTagItem(TemporalAgentTelemetry.TotalTokensAttribute)));
    }

    [Fact]
    public async Task StandaloneOpenTelemetryChatClient_CorrelatesByTraceAndOwnsUsage()
    {
        var chatSourceName = $"TemporalAgents.Tests.Meai.{Guid.NewGuid():N}";
        using var chatClient = new OpenTelemetryChatClient(
            new UsageReportingChatClient(),
            sourceName: chatSourceName);
        var stopped = new ConcurrentBag<Activity>();
        using var listener = CreateListener(
            source => source.Name is TemporalAgentTelemetry.ActivitySourceName
                || source.Name == chatSourceName,
            stopped);
        var activities = BuildActivities(opts =>
            opts.AddDurableAgent("ChatTelemetryAgent", agent =>
                agent.ChatClient = _ => chatClient));

        await RunActivityAsync(activities, "ChatTelemetryAgent", "corr-chat");

        var turn = Assert.Single(stopped, a =>
            a.Source.Name == TemporalAgentTelemetry.ActivitySourceName
            && a.OperationName == TemporalAgentTelemetry.AgentTurnSpanName
            && Equals(a.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute), "corr-chat"));
        var chat = Assert.Single(stopped, a => a.Source.Name == chatSourceName);
        Assert.Equal("corr-chat", turn.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute));
        Assert.Null(chat.GetTagItem(TemporalAgentTelemetry.AgentCorrelationIdAttribute));
        Assert.Equal(turn.TraceId, chat.TraceId);
        Assert.True(IsAncestor(turn, chat));
        Assert.Null(turn.GetTagItem(TemporalAgentTelemetry.InputTokensAttribute));
        Assert.NotNull(chat.GetTagItem(TemporalAgentTelemetry.InputTokensAttribute));
    }

    private static AgentActivities BuildActivities(Action<TemporalAgentsOptions> configure)
    {
        var options = new TemporalAgentsOptions();
        configure(options);
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        return new AgentActivities(
            provider,
            provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static Task<AgentStepResult> RunActivityAsync(
        AgentActivities activities,
        string agentName,
        string correlationId)
    {
        var environment = new ActivityEnvironment
        {
            TemporalClient = A.Fake<ITemporalClient>(),
        };
        return environment.RunAsync(() => activities.RunDurableAgentStepAsync(new AgentStepInput
        {
            AgentName = agentName,
            Request = new RunRequest("hello") { CorrelationId = correlationId },
            AccumulatedMessages = [new ChatMessage(ChatRole.User, "hello")],
            SessionId = TemporalAgentSessionId.WithRandomKey(agentName),
        }));
    }

    private static ActivityListener CreateListener(
        Func<ActivitySource, bool> shouldListen,
        ConcurrentBag<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = shouldListen,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static bool IsAncestor(Activity ancestor, Activity descendant)
    {
        for (var current = descendant.Parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
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

    private sealed class UsageReportingChatClient(bool fail = false) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("activities use streaming");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (fail)
            {
                throw new InvalidOperationException("provider failure");
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello");
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new UsageContent(new UsageDetails
                    {
                        InputTokenCount = 11,
                        OutputTokenCount = 7,
                        TotalTokenCount = 18,
                    }),
                ],
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
