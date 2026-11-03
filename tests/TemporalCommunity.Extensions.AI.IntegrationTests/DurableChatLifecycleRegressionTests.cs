using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
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
        string? sessionEpoch = null;

        try
        {
            await session.SendAsync(conversationId, [new ChatMessage(ChatRole.User, "turn 1")])
                .WaitAsync(Deadline);
            sessionEpoch = await handle.QueryAsync<DurableChatWorkflow, string>(
                wf => wf.GetSessionEpoch()).WaitAsync(Deadline);
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

            Assert.Equal(sessionEpoch, await newRun.QueryAsync<DurableChatWorkflow, string>(
                wf => wf.GetSessionEpoch()).WaitAsync(Deadline));
            Assert.Equal(3, await newRun.QueryAsync<DurableChatWorkflow, int>(
                wf => wf.GetCompletedTurnCount()).WaitAsync(Deadline));

            var callsBeforeRetry = model.CallCount;
            var retried = await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn 3")],
                correlationId: "different-correlation",
                requestId: later.RequestId).WaitAsync(Deadline);
            Assert.Equal(later.RequestId, retried.RequestId);
            Assert.Equal(later.Text, retried.Text);
            Assert.Equal(callsBeforeRetry, model.CallCount);

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

    [Fact]
    public async Task TurnAdmission_RejectsBusyAndConflictBeforeAccepting_AndJoinsMatchingRequest()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new GatedChatClient();
        var taskQueue = $"turn-admission-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, model);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"turn-admission-{Guid.NewGuid():N}";
        var workflowId = session.GetWorkflowId(conversationId);
        var workflow = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
        Task<DurableSessionResponse>? firstTask = null;
        WorkflowUpdateHandle<DurableSessionResponse>? duplicate = null;

        try
        {
            firstTask = session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "same input")],
                requestId: "request-1");
            await model.Entered.WaitAsync(Deadline);
            await WaitForHistoryEventAsync(
                workflow,
                e => e.WorkflowExecutionUpdateAcceptedEventAttributes is not null);
            var acceptedBeforeRejections = await CountAcceptedUpdatesAsync(workflow);

            var busy = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                workflow.ExecuteUpdateAsync(wf => wf.ChatAsync(new DurableChatInput
                {
                    ConversationId = conversationId,
                    RequestId = "request-2",
                    Messages = new List<ChatMessage> { new(ChatRole.User, "different request") },
                })));
            Assert.Equal(
                "DurableTurnBusy",
                Assert.IsType<ApplicationFailureException>(busy.InnerException).ErrorType);

            var conflict = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                workflow.ExecuteUpdateAsync(wf => wf.ChatAsync(new DurableChatInput
                {
                    ConversationId = conversationId,
                    RequestId = "request-1",
                    Messages = new List<ChatMessage> { new(ChatRole.User, "changed input") },
                })));
            Assert.Equal(
                "DurableTurnConflict",
                Assert.IsType<ApplicationFailureException>(conflict.InnerException).ErrorType);
            Assert.Equal(acceptedBeforeRejections, await CountAcceptedUpdatesAsync(workflow));

            duplicate = await workflow.StartUpdateAsync<DurableChatWorkflow, DurableSessionResponse>(
                wf => wf.ChatAsync(new DurableChatInput
                {
                    ConversationId = conversationId,
                    RequestId = "request-1",
                    Messages = new List<ChatMessage> { new(ChatRole.User, "same input") },
                }),
                new WorkflowUpdateStartOptions(WorkflowUpdateStage.Accepted));

            model.Release();
            var first = await firstTask.WaitAsync(Deadline);
            var joined = await duplicate.GetResultAsync().WaitAsync(Deadline);
            Assert.Equal(first.RequestId, joined.RequestId);
            Assert.Equal(first.Text, joined.Text);
            Assert.Equal(1, model.Calls);
            Assert.Equal(2, await CountAcceptedUpdatesAsync(workflow));
        }
        finally
        {
            model.Release();
            if (firstTask is not null)
            {
                try { await firstTask.WaitAsync(Deadline); }
                catch (Exception) { /* Preserve the original assertion failure. */ }
            }
            if (duplicate is not null)
            {
                try { await duplicate.GetResultAsync().WaitAsync(Deadline); }
                catch (Exception) { /* Preserve the original assertion failure. */ }
            }
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TurnAdmission_ReplaysTerminalFailureWithoutRedispatchingModelActivity()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new FailingChatClient();
        var taskQueue = $"turn-failure-ledger-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, model);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"turn-failure-ledger-{Guid.NewGuid():N}";
        var messages = new List<ChatMessage> { new(ChatRole.User, "fail once") };

        try
        {
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(conversationId, messages, requestId: "terminal-failure"));
            var attemptsAfterFailure = model.Calls;
            Assert.True(attemptsAfterFailure > 0);

            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(conversationId, messages, requestId: "terminal-failure"));

            Assert.Equal(attemptsAfterFailure, model.Calls);
            Assert.Empty(await session.GetHistoryAsync(conversationId));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TerminalOutcomeLedger_EvictsOldestOutcomeAtConfiguredCount()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new TestChatClient();
        var taskQueue = $"turn-ledger-bound-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maxTerminalOutcomeCount: 1);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"turn-ledger-bound-{Guid.NewGuid():N}";

        try
        {
            await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "first")],
                requestId: "first-request").WaitAsync(Deadline);
            await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "second")],
                requestId: "second-request").WaitAsync(Deadline);

            await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "first")],
                requestId: "first-request").WaitAsync(Deadline);

            Assert.Equal(3, model.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TerminalOutcomeLedger_DoesNotRetainAnEntryLargerThanByteLimit()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new TestChatClient();
        var taskQueue = $"turn-ledger-bytes-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maxTerminalOutcomeBytes: 1);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"turn-ledger-bytes-{Guid.NewGuid():N}";
        var messages = new[] { new ChatMessage(ChatRole.User, "retry") };

        try
        {
            await session.SendAsync(conversationId, messages, requestId: "oversized-request")
                .WaitAsync(Deadline);
            await session.SendAsync(conversationId, messages, requestId: "oversized-request")
                .WaitAsync(Deadline);

            Assert.Equal(2, model.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TerminalFailureLedger_SurvivesContinueAsNew()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new SelectiveFailureChatClient();
        var taskQueue = $"turn-failure-ledger-can-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, model, maxEntryCount: 4);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"turn-failure-ledger-can-{Guid.NewGuid():N}";
        var workflowId = session.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
        string? newRunId = null;
        string? oldRunId = null;

        try
        {
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(
                    conversationId,
                    [new ChatMessage(ChatRole.User, "fail")],
                    requestId: "retained-failure"));
            oldRunId = (await handle.DescribeAsync()).RunId;

            await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "success-1")],
                requestId: "success-1");
            await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "success-2")],
                requestId: "success-2");
            newRunId = await WaitForNewRunAsync(handle, oldRunId);

            var callsBeforeRetry = model.Calls;
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(
                    conversationId,
                    [new ChatMessage(ChatRole.User, "fail")],
                    requestId: "retained-failure"));

            Assert.Equal(callsBeforeRetry, model.Calls);
        }
        finally
        {
            if (oldRunId is not null)
            {
                var run = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
                    workflowId, newRunId ?? oldRunId);
                await run.SignalAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
                await run.GetResultAsync().WaitAsync(Deadline);
            }
            await host.StopAsync();
        }
    }

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
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? reducer = null,
        int maxTerminalOutcomeCount = 128,
        int maxTerminalOutcomeBytes = 1024 * 1024)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(model).Build();
        builder.Services.AddHostedTemporalWorker(taskQueue).AddDurableAI(options =>
        {
            options.MaxEntryCount = maxEntryCount;
            options.MaxTerminalOutcomeCount = maxTerminalOutcomeCount;
            options.MaxTerminalOutcomeBytes = maxTerminalOutcomeBytes;
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

    private static async Task<int> CountAcceptedUpdatesAsync(WorkflowHandle handle)
    {
        var count = 0;
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.WorkflowExecutionUpdateAcceptedEventAttributes is not null)
            {
                count++;
            }
        }
        return count;
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
        private int _calls;

        public Task Entered => _entered.Task;
        public int Calls => _calls;
        public void Release() => _release.TrySetResult();

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
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

    private sealed class FailingChatClient : IChatClient
    {
        private int _calls;

        public int Calls => _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromException<ChatResponse>(
                new ApplicationFailureException(
                    "Expected model failure.",
                    errorType: "ExpectedModelFailure",
                    nonRetryable: true));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SelectiveFailureChatClient : IChatClient
    {
        private int _calls;

        public int Calls => _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var lastUserMessage = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text;
            if (lastUserMessage == "fail")
            {
                return Task.FromException<ChatResponse>(
                    new ApplicationFailureException(
                        "Expected model failure.",
                        errorType: "ExpectedModelFailure",
                        nonRetryable: true));
            }

            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"Response: {lastUserMessage}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
