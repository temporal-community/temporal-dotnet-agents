using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public sealed class DurableActivityRoutingIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private const string ModelActivity = "TemporalCommunity.Extensions.AI.GetChatStep";
    private const string ToolActivity = "TemporalCommunity.Extensions.AI.InvokeFunction";
    private const string ResolverActivity = "TemporalCommunity.Extensions.AI.ResolveDurableToolsets";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManagedRoutes_DefaultOrSeparate_FreezeAcrossContinueAsNew_AndReplay(
        bool separate, bool callerOwned)
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var prefix = $"routing-{Guid.NewGuid():N}";
        var workflowQueue = $"{prefix}-session";
        var modelQueue = separate ? $"{prefix}-model" : workflowQueue;
        var toolQueue = separate ? $"{prefix}-tool" : workflowQueue;
        var observations = new Observations();
        using var sessionHost = BuildWorkerHost(
            environment.Client, workflowQueue, observations, toolQueue, workflows: true);
        using var modelHost = separate
            ? BuildWorkerHost(environment.Client, modelQueue, observations, toolQueue, workflows: false)
            : null;
        using var toolHost = separate
            ? BuildWorkerHost(environment.Client, toolQueue, observations, toolQueue, workflows: false)
            : null;
        var clientServices = new ServiceCollection();
        clientServices.AddSingleton(environment.Client);
        clientServices.AddDurableAIClient(workflowQueue, options =>
        {
            options.ModelTaskQueue = separate ? modelQueue : null;
            options.MaxEntryCount = 4;
            options.DefaultHistoryCompactionStrategy = DurableHistoryCompactionStrategy.TrimRecentTurns;
            options.RetryPolicy = new RetryPolicy { MaximumAttempts = 1 };
        });
        if (callerOwned)
        {
            foreach (var function in CreateTools(observations))
            {
                clientServices.AddDurableToolDeclaration(function.AsDeclarationOnly(), options =>
                    options.TaskQueue = function.Name == "routed_tool" ? toolQueue : null);
            }
        }
        using var clientProvider = clientServices.BuildServiceProvider();
        var sessions = clientProvider.GetRequiredService<DurableChatSessionClient>();
        var conversation = $"{prefix}-conversation";
        var workflow = environment.Client.GetWorkflowHandle<DurableChatWorkflow>(
            sessions.GetWorkflowId(conversation));
        await sessionHost.StartAsync();
        if (modelHost is not null) await modelHost.StartAsync();
        if (toolHost is not null) await toolHost.StartAsync();
        try
        {
            Assert.Equal("finished", (await sessions.SendAsync(
                conversation, "first", requestId: "first").WaitAsync(Deadline)).Text);
            var firstRun = (await workflow.DescribeAsync()).RunId!;

            // Both the starter and resolver now advertise different routing. The existing
            // session must retain its recorded choices, including after continue-as-new.
            clientProvider.GetRequiredService<DurableExecutionOptions>().ModelTaskQueue = "unpolled-changed-model";
            foreach (var options in sessionHost.Services
                .GetRequiredService<DurableChatToolOptionsRegistry>().Values)
                options.TaskQueue = "unpolled-changed-tool";

            Assert.Equal("finished", (await sessions.SendAsync(
                conversation, "second", requestId: "second").WaitAsync(Deadline)).Text);
            using (var timeout = new CancellationTokenSource(Deadline))
            {
                while ((await workflow.DescribeAsync(
                    new() { Rpc = new() { CancellationToken = timeout.Token } })).RunId == firstRun)
                    await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
            var secondRun = (await workflow.DescribeAsync()).RunId!;
            Assert.Equal("finished", (await sessions.SendAsync(
                conversation, "third", requestId: "third").WaitAsync(Deadline)).Text);
            await sessions.ShutdownAsync(conversation).WaitAsync(Deadline);
            await workflow.GetResultAsync().WaitAsync(Deadline);

            Assert.Equal(6, observations.ModelQueues.Count);
            Assert.All(observations.ModelQueues, queue => Assert.Equal(modelQueue, queue));
            Assert.Equal(3, observations.ToolQueues.Count(entry => entry.Tool == "routed_tool"));
            Assert.Equal(3, observations.ToolQueues.Count(entry => entry.Tool == "default_tool"));
            Assert.All(observations.ToolQueues, entry =>
                Assert.Equal(entry.Tool == "routed_tool" ? toolQueue : workflowQueue, entry.Queue));

            var resolverCount = 0;
            foreach (var runId in new[] { firstRun, secondRun })
            {
                var handle = environment.Client.GetWorkflowHandle<DurableChatWorkflow>(
                    workflow.Id, runId);
                var history = await handle.FetchHistoryAsync();
                foreach (var ev in history.Events)
                {
                    if (ev.ActivityTaskScheduledEventAttributes is not { } scheduled) continue;
                    if (scheduled.ActivityType.Name == ModelActivity)
                        Assert.Equal(modelQueue, scheduled.TaskQueue.Name);
                    else if (scheduled.ActivityType.Name == ResolverActivity)
                    {
                        resolverCount++;
                        Assert.Equal(workflowQueue, scheduled.TaskQueue.Name);
                    }
                    else if (scheduled.ActivityType.Name == ToolActivity)
                    {
                        var input = (DurableFunctionInput)DurableAIDataConverter.Instance.PayloadConverter
                            .ToValue(scheduled.Input.Payloads_[0], typeof(DurableFunctionInput))!;
                        Assert.Equal(input.FunctionName == "routed_tool" ? toolQueue : workflowQueue,
                            scheduled.TaskQueue.Name);
                    }
                }
                var start = history.Events.Single(ev => ev.WorkflowExecutionStartedEventAttributes is not null)
                    .WorkflowExecutionStartedEventAttributes;
                var frozenInput = (DurableChatWorkflowInput)DurableAIDataConverter.Instance.PayloadConverter
                    .ToValue(start.Input.Payloads_[0], typeof(DurableChatWorkflowInput))!;
                Assert.Equal(separate ? modelQueue : null, frozenInput.ModelTaskQueue);
                if (runId == secondRun && !callerOwned)
                    Assert.Equal(toolQueue, frozenInput.ToolsetManifest!.Members.Single(
                        member => member.Declaration.Name == "routed_tool").ToolActivityOptions.TaskQueue);
                var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
                {
                    DataConverter = DurableAIDataConverter.Instance,
                }.AddWorkflow<DurableChatWorkflow>());
                Assert.Null((await replayer.ReplayWorkflowAsync(
                    history, throwOnReplayFailure: false)).ReplayFailure);
            }
            Assert.Equal(callerOwned ? 0 : 1, resolverCount);
            Assert.Equal(6, observations.ModelQueues.Count);
            Assert.Equal(6, observations.ToolQueues.Count);
        }
        finally
        {
            if (toolHost is not null) await toolHost.StopAsync();
            if (modelHost is not null) await modelHost.StopAsync();
            await sessionHost.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnpolledQueue_DoesNotStartStartToCloseTimer_AndTurnCanBeCancelled(bool tool)
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var queue = $"unpolled-routing-{Guid.NewGuid():N}";
        var observations = new Observations();
        using var host = BuildWorkerHost(
            environment.Client, queue, observations, tool ? "unpolled-tool" : null, workflows: true);
        using var clientProvider = new ServiceCollection()
            .AddSingleton(environment.Client)
            .AddDurableAIClient(queue, options =>
            {
                options.ModelTaskQueue = tool ? null : "unpolled-model";
                options.ActivityTimeout = TimeSpan.FromMilliseconds(200);
                options.RetryPolicy = new RetryPolicy { MaximumAttempts = 1 };
                options.MaximumConsecutiveErrorsPerRequest = 0;
            })
            .BuildServiceProvider();
        var sessions = clientProvider.GetRequiredService<DurableChatSessionClient>();
        var conversation = $"missing-{Guid.NewGuid():N}";
        var handle = environment.Client.GetWorkflowHandle<DurableChatWorkflow>(
            sessions.GetWorkflowId(conversation));
        await host.StartAsync();
        var turn = sessions.SendAsync(conversation, "missing worker", requestId: "cancel-me");
        try
        {
            var activity = tool ? ToolActivity : ModelActivity;
            var missingQueue = tool ? "unpolled-tool" : "unpolled-model";
            using (var timeout = new CancellationTokenSource(Deadline))
            {
                var scheduled = false;
                while (!scheduled)
                {
                    try
                    {
                        scheduled = (await handle.FetchHistoryAsync()).Events.Any(ev =>
                            ev.ActivityTaskScheduledEventAttributes is { } attributes
                            && attributes.ActivityType.Name == activity
                            && attributes.TaskQueue.Name == missingQueue);
                    }
                    catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
                    {
                        // The update-with-start RPC may not have reached the server yet.
                    }
                    if (turn.IsCompleted && !scheduled)
                        await turn;
                    await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.False(turn.IsCompleted);
            var history = await handle.FetchHistoryAsync();
            var queued = history.Events.Single(ev =>
                ev.ActivityTaskScheduledEventAttributes is { } attributes
                && attributes.ActivityType.Name == activity
                && attributes.TaskQueue.Name == missingQueue);
            Assert.DoesNotContain(history.Events, ev =>
                ev.ActivityTaskStartedEventAttributes?.ScheduledEventId == queued.EventId);
            Assert.DoesNotContain(history.Events, ev =>
                ev.ActivityTaskTimedOutEventAttributes?.ScheduledEventId == queued.EventId);
            await sessions.CancelTurnAsync(conversation, "cancel-me").WaitAsync(Deadline);
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => turn.WaitAsync(Deadline));
            Assert.Equal(0, observations.ToolQueues.Count(entry => entry.Tool == "routed_tool"));
            Assert.Equal(tool ? 1 : 0, observations.ModelQueues.Count);
            await sessions.ShutdownAsync(conversation).WaitAsync(Deadline);
            await handle.GetResultAsync().WaitAsync(Deadline);
            var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
            {
                DataConverter = DurableAIDataConverter.Instance,
            }.AddWorkflow<DurableChatWorkflow>());
            Assert.Null((await replayer.ReplayWorkflowAsync(
                await handle.FetchHistoryAsync(), throwOnReplayFailure: false)).ReplayFailure);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHost BuildWorkerHost(
        ITemporalClient client, string queue, Observations observations, string? routedToolQueue,
        bool workflows)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<IChatClient>(new RoutingChatClient(observations));
        var worker = builder.Services.AddHostedTemporalWorker(queue).AddDurableAIWorker(options =>
        {
            options.RegisterDefaultWorkflow = workflows;
            options.ModelTaskQueue = "worker-options-do-not-override-client";
        });
        foreach (var function in CreateTools(observations))
            worker.AddDurableTool(function, options =>
            {
                options.TaskQueue = function.Name == "routed_tool" ? routedToolQueue : null;
                options.StartToCloseTimeout = TimeSpan.FromMilliseconds(200);
            });
        return builder.Build();
    }

    private static AIFunction[] CreateTools(Observations observations) =>
    [
        AIFunctionFactory.Create(() => RecordTool(observations, "routed_tool"), "routed_tool"),
        AIFunctionFactory.Create(() => RecordTool(observations, "default_tool"), "default_tool"),
    ];

    private static string RecordTool(Observations observations, string name)
    {
        observations.ToolQueues.Enqueue((name, ActivityExecutionContext.Current.Info.TaskQueue));
        return "ok";
    }

    private sealed class Observations
    {
        public ConcurrentQueue<string> ModelQueues { get; } = new();
        public ConcurrentQueue<(string Tool, string Queue)> ToolQueues { get; } = new();
    }

    private sealed class RoutingChatClient(Observations observations) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            observations.ModelQueues.Enqueue(ActivityExecutionContext.Current.Info.TaskQueue);
            await Task.CompletedTask;
            if (messages.Last().Role == ChatRole.Tool)
            {
                yield return new(ChatRole.Assistant, "finished") { FinishReason = ChatFinishReason.Stop };
            }
            else
            {
                var user = messages.Last(message => message.Role == ChatRole.User).Text;
                yield return new(ChatRole.Assistant,
                [
                    new FunctionCallContent($"call-routed-{user}", "routed_tool"),
                    new FunctionCallContent($"call-default-{user}", "default_tool"),
                ]) { FinishReason = ChatFinishReason.ToolCalls };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
