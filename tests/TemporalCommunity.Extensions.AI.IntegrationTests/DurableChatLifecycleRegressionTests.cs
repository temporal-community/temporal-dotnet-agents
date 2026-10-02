using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Worker;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>Reproductions of admitted-turn loss at continue-as-new and shutdown.</summary>
public class DurableChatLifecycleRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(40);

    [Fact]
    public Task KeyedReducer_ContinueAsNew_RetainsLaterSuccessfullyCompletedTurn() =>
        RunContinueAsNewAsync();

    internal static async Task RunContinueAsNewAsync(Func<WorkflowHistory, Task>? capture = null)
    {
        const string reducerKey = "gated-last-complete-turn";
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new TestChatClient();
        var taskQueue = $"lifecycle-can-{Guid.NewGuid():N}";

        // The activity receives a serialized snapshot at schedule time. Holding it here
        // lets turn 3 complete in the old run before CAN consumes that snapshot.
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> reducer = entries =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return entries.TakeLast(2).ToList();
        };
        using var host = BuildHost(env.Client, taskQueue, model, maxEntryCount: 4, reducerKey, reducer);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"lifecycle-can-{Guid.NewGuid():N}";
        var workflowId = session.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
        Task<DurableSessionResponse>? laterTask = null;
        string? newRunId = null;

        try
        {
            await session.SendAsync(conversationId, [new ChatMessage(ChatRole.User, "turn 1")])
                .WaitAsync(Deadline);
            var oldRunId = (await handle.DescribeAsync().WaitAsync(Deadline)).RunId;
            await session.SendAsync(conversationId, [new ChatMessage(ChatRole.User, "turn 2")])
                .WaitAsync(Deadline);
            await entered.Task.WaitAsync(Deadline);

            // Turn 3 must finish BEFORE the reducer is released, rather than merely be
            // submitted. Its successful reply is the data that must survive the transition.
            laterTask = session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn 3")],
                correlationId: "later-successful-turn");
            var later = await laterTask.WaitAsync(Deadline);
            Assert.Equal("Response: turn 3", later.Text);
            Assert.Equal("later-successful-turn", later.CorrelationId);
            var oldRun = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId, oldRunId);
            await WaitForHistoryEventAsync(
                oldRun,
                e => e.WorkflowExecutionUpdateCompletedEventAttributes is not null,
                minimumCount: 3);

            release.TrySetResult();
            newRunId = await WaitForNewRunAsync(handle, oldRunId);
            var newRun = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId, newRunId);
            var carried = await newRun.QueryAsync<DurableChatWorkflow, IReadOnlyList<DurableSessionEntry>>(
                wf => wf.GetHistory()).WaitAsync(Deadline);

            var request = Assert.IsType<DurableSessionRequest>(carried[0]);
            var response = Assert.IsType<DurableSessionResponse>(carried[1]);
            Assert.Equal(2, carried.Count);
            Assert.Equal("later-successful-turn", request.CorrelationId);
            Assert.Equal("turn 3", Assert.Single(request.Messages).Text);
            Assert.Equal(request.CorrelationId, response.CorrelationId);
            Assert.Equal("Response: turn 3", response.Text);

            var replayOptions = new WorkflowReplayerOptions
            {
                DataConverter = DurableAIDataConverter.Instance,
            };
            replayOptions.AddWorkflow<DurableChatWorkflow>();
            var history = await oldRun.FetchHistoryAsync().WaitAsync(Deadline);
            var replay = await new WorkflowReplayer(replayOptions)
                .ReplayWorkflowAsync(history, throwOnReplayFailure: false);
            Assert.Null(replay.ReplayFailure);
            if (capture is not null)
                await capture(history);
        }
        finally
        {
            release.TrySetResult();
            if (laterTask is not null)
            {
                try { await laterTask.WaitAsync(Deadline); }
                catch (Exception) { /* Preserve original failure after releasing the reducer. */ }
            }
            if (newRunId is not null)
            {
                var newRun = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId, newRunId);
                await newRun.SignalAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
                await newRun.GetResultAsync().WaitAsync(Deadline);
            }
            await host.StopAsync();
        }
    }

    [Fact]
    public Task Shutdown_DrainsAdmittedModelUpdate_BeforeWorkflowCompletes() =>
        RunShutdownAsync();

    internal static async Task RunShutdownAsync(Func<WorkflowHistory, Task>? capture = null)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new GatedChatClient();
        var taskQueue = $"lifecycle-shutdown-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, model);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"lifecycle-shutdown-{Guid.NewGuid():N}";
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));

        var pending = session.SendAsync(conversationId,
            [new ChatMessage(ChatRole.User, "admitted turn")]);
        try
        {
            await model.Entered.WaitAsync(Deadline);
            await WaitForHistoryEventAsync(handle,
                e => e.WorkflowExecutionUpdateAcceptedEventAttributes is not null);

            await handle.SignalAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
            // A signal-written event alone does not prove the workflow has processed it:
            // require a subsequently completed workflow task while the model stays gated.
            await WaitForProcessedShutdownAsync(handle);
            model.Release();

            var response = await pending.WaitAsync(Deadline);
            Assert.Equal("completed admitted turn", response.Text);
            await handle.GetResultAsync().WaitAsync(Deadline);
            await WaitForHistoryEventAsync(handle,
                e => e.WorkflowExecutionUpdateCompletedEventAttributes is not null);
            await WaitForHistoryEventAsync(handle,
                e => e.WorkflowExecutionCompletedEventAttributes is not null);
            if (capture is not null)
                await capture(await handle.FetchHistoryAsync().WaitAsync(Deadline));
        }
        finally
        {
            model.Release();
            try { await pending.WaitAsync(Deadline); }
            catch (Exception) { /* Preserve the original red assertion; activity is released. */ }
            await host.StopAsync();
        }
    }

    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient model,
        int maxEntryCount = 100,
        string? reducerKey = null,
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? reducer = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(model).Build();
        builder.Services.AddHostedTemporalWorker(taskQueue).AddDurableAI(options =>
        {
            options.MaxEntryCount = maxEntryCount;
            options.ActivityTimeout = TimeSpan.FromSeconds(60);
            options.SessionTimeToLive = TimeSpan.FromMinutes(5);
            options.DefaultHistoryReducerKey = reducerKey;
        });
        if (reducerKey is not null && reducer is not null)
        {
            builder.Services.AddKeyedSingleton<Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
                reducerKey, (_, _) => reducer);
        }
        return builder.Build();
    }

    private static async Task WaitForHistoryEventAsync(
        WorkflowHandle handle,
        Func<Temporalio.Api.History.V1.HistoryEvent, bool> matches,
        int minimumCount = 1)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            var count = 0;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (matches(ev)) count++;
            }
            if (count >= minimumCount) return;
            await Task.Delay(TimeSpan.FromMilliseconds(75));
        }
        throw new TimeoutException($"Expected {minimumCount} matching workflow history event(s).");
    }

    private static async Task<string> WaitForNewRunAsync(
        WorkflowHandle handle, string oldRunId)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            var runId = (await handle.DescribeAsync().WaitAsync(Deadline)).RunId;
            if (runId != oldRunId) return runId;
            await Task.Delay(TimeSpan.FromMilliseconds(75));
        }
        throw new TimeoutException("Continue-as-new did not create a new run.");
    }

    private static async Task WaitForProcessedShutdownAsync(WorkflowHandle handle)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            long signalEventId = 0;
            long completedTaskAfterSignal = 0;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.WorkflowExecutionSignaledEventAttributes is not null)
                    signalEventId = ev.EventId;
                if (signalEventId > 0 &&
                    ev.EventId > signalEventId &&
                    ev.WorkflowTaskCompletedEventAttributes is not null)
                    completedTaskAfterSignal = ev.EventId;
            }
            if (completedTaskAfterSignal > signalEventId && signalEventId > 0)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(75));
        }
        throw new TimeoutException("Shutdown signal was not processed by a completed workflow task.");
    }

    private sealed class GatedChatClient : IChatClient
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "completed admitted turn"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
