using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Api.Enums.V1;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.Tests.Shared;
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
    public async Task SessionClient_RejectsOversizedWorkflowInputBeforeStartingWorkflow()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new TestChatClient();
        var taskQueue = $"payload-start-input-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maximumTransportPayloadSizeBytes: 1);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();

        try
        {
            var exception = await Assert.ThrowsAsync<DurableTransportPayloadTooLargeException>(() =>
                session.SendAsync("oversized-start-input", "small request"));

            Assert.Equal("workflow input", exception.Boundary);
            Assert.Equal(0, model.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SessionClient_RejectsOversizedUpdateInputBeforeDispatch()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new TestChatClient();
        var taskQueue = $"payload-update-input-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maximumTransportPayloadSizeBytes: 4096);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();

        try
        {
            var exception = await Assert.ThrowsAsync<DurableTransportPayloadTooLargeException>(() =>
                session.SendAsync("oversized-update-input", new string('x', 8192)));

            Assert.Equal("workflow update input", exception.Boundary);
            Assert.Equal(0, model.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SendAsync_CallerRpcCancellationDoesNotCancelAdmittedTurn()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new GatedChatClient();
        var taskQueue = $"cancel-rpc-wait-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, model);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"cancel-rpc-wait-{Guid.NewGuid():N}";
        const string requestId = "caller-disconnected-request";
        using var cancellation = new CancellationTokenSource();

        try
        {
            var originalRequest = session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "wait for the model")],
                requestId: requestId,
                cancellationToken: cancellation.Token);
            await model.Entered.WaitAsync(Deadline);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<Exception>(() => originalRequest);

            model.Release();
            var recovered = await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "wait for the model")],
                requestId: requestId).WaitAsync(Deadline);

            Assert.Equal("completed admitted turn", recovered.Text);
            Assert.Equal(1, model.Calls);
        }
        finally
        {
            model.Release();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task CancelTurnAsync_RejectsCompletedTurnBeforeAcceptance()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new TestChatClient();
        var taskQueue = $"cancel-completed-turn-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, model);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"cancel-completed-turn-{Guid.NewGuid():N}";
        const string requestId = "completed-request";

        try
        {
            await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "finish first")],
                requestId: requestId).WaitAsync(Deadline);

            var exception = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.CancelTurnAsync(conversationId, requestId));
            var applicationFailure = Assert.IsType<ApplicationFailureException>(exception.InnerException);
            Assert.Equal("DurableTurnCancellationAlreadyCompleted", applicationFailure.ErrorType);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task OversizedModelActivityResult_FailsBeforeUpdateResultAndRollsBackHistory()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, new string('x', 8192))),
        ]);
        var taskQueue = $"payload-update-result-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maximumTransportPayloadSizeBytes: 4096);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();

        try
        {
            var exception = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(
                    "oversized-update-result",
                    "small request",
                    requestId: "oversized-update-result"));

            var applicationFailure = Assert.IsType<ApplicationFailureException>(exception.InnerException);
            Assert.Equal("DurableModelResultTooLarge", applicationFailure.ErrorType);
            Assert.Equal(1, model.CallCount);
            Assert.Empty(await session.GetHistoryAsync("oversized-update-result"));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ContinueAsNew_MissingStrategyBlocksUntilExplicitTrim()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "first response")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "second response")),
        ]);
        var taskQueue = $"payload-can-input-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maxEntryCount: 4);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var workflowId = session.GetWorkflowId("trim-recovery");
        var workflow = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);

        try
        {
            await session.SendAsync("trim-recovery", "first").WaitAsync(Deadline);
            var originalRunId = (await workflow.DescribeAsync()).RunId;
            await session.SendAsync("trim-recovery", "second").WaitAsync(Deadline);

            var status = await workflow.QueryAsync<DurableChatWorkflow, DurableChatHistoryBudgetStatus>(
                wf => wf.GetHistoryBudget());

            Assert.True(status.IsHistoryBlocked);
            Assert.Equal(4, status.RetainedHistoryEntryCount);
            Assert.Equal("compaction-blocked", status.HistoryRecoveryLastOutcome);

            var recovery = await session.TrimBlockedHistoryAsync(
                "trim-recovery",
                "explicit-trim-1").WaitAsync(Deadline);
            Assert.False(recovery.IsBlocked);
            Assert.Equal("trim-succeeded", recovery.Outcome);

            await WaitForRunChangeAsync(workflow, originalRunId);

            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                status = await workflow.QueryAsync<DurableChatWorkflow, DurableChatHistoryBudgetStatus>(
                    wf => wf.GetHistoryBudget());
                if (!status.IsHistoryBlocked)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }

            Assert.False(status.IsHistoryBlocked);
            var recoveredHistory = await session.GetHistoryAsync("trim-recovery");
            Assert.Contains(recoveredHistory, entry => entry is TruncationMarkerEntry);
            Assert.Equal(3, recoveredHistory.Count);
            await AssertChatReplayAsync(
                env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId, originalRunId));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SummarizingCompaction_IsProjectedIntoLaterModelContext()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer one")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer two")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary old turn")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer three")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary next old turn")),
        ]);
        var taskQueue = $"history-summary-projection-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maxEntryCount: 4,
            historyCompactionStrategy: DurableHistoryCompactionStrategy.SummarizeOldestTurns);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();

        try
        {
            const string conversationId = "summary-context-projection";
            await session.SendAsync(conversationId, "turn one").WaitAsync(Deadline);
            var workflowId = session.GetWorkflowId(conversationId);
            var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
            var firstRunId = (await handle.DescribeAsync()).RunId;

            await session.SendAsync(conversationId, "turn two").WaitAsync(Deadline);
            await WaitForRunChangeAsync(handle, firstRunId);

            await session.SendAsync(conversationId, "turn three").WaitAsync(Deadline);

            var thirdModelCall = model.Calls[3];
            Assert.Contains(
                thirdModelCall.Messages,
                message => message.Contents.OfType<TextContent>().Any(content =>
                    content.Text.Contains("summary old turn", StringComparison.Ordinal)));
            Assert.Contains(
                thirdModelCall.Messages,
                message => message.Contents.OfType<TextContent>().Any(content =>
                    content.Text.Contains("answer two", StringComparison.Ordinal)));

            var history = await session.GetHistoryAsync(conversationId);
            Assert.Contains(history, entry => entry is CompactionMarkerEntry);
            Assert.True(model.CallCount >= 4);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SummarizingCompaction_DoesNotResummarizeMarkerWithoutAnotherEligibleTurn()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new TestChatClient();
        var taskQueue = $"summary-marker-no-progress-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maxEntryCount: 4,
            historyEventSoftLimit: 1,
            historyEventReserveLimit: 12,
            historyCompactionStrategy: DurableHistoryCompactionStrategy.SummarizeOldestTurns);
        await host.StartAsync();
        var factory = host.Services.GetRequiredService<IDurableChatWorkflowInputFactory>();
        var now = DateTimeOffset.UtcNow;
        var input = factory.Create() with
        {
            CarriedHistory =
            [
                new TruncationMarkerEntry
                {
                    EntryId = "trimmed-range",
                    TurnId = "trimmed-range",
                    CorrelationId = "trimmed-range",
                    CreatedAt = now,
                    FirstRemovedEntryId = "removed-1",
                    LastRemovedEntryId = "removed-2",
                    RemovedEntryCount = 2,
                    FirstRemovedTurnId = "removed-turn-1",
                    LastRemovedTurnId = "removed-turn-1",
                    RemovedTurnCount = 1,
                },
                new CompactionMarkerEntry
                {
                    EntryId = "existing-summary",
                    TurnId = "existing-summary",
                    CorrelationId = "existing-summary",
                    CreatedAt = now,
                    FirstReplacedEntryId = "summary-source-1",
                    LastReplacedEntryId = "summary-source-2",
                    ReplacedEntryCount = 2,
                    FirstReplacedTurnId = "summary-turn-1",
                    LastReplacedTurnId = "summary-turn-1",
                    ReplacedTurnCount = 1,
                    Strategy = "test",
                    ModelId = "test-model",
                    Summary = new DurableSessionSummary { Text = "prior summary" },
                },
                DurableSessionRequest.FromMessages(
                    [new ChatMessage(ChatRole.User, "latest question")],
                    "latest-turn"),
                DurableSessionResponse.FromChatResponse(
                    "latest-turn",
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, "latest answer")),
                    now,
                    DurableTurnCompletionReason.FinalResponse),
            ],
        };
        var workflowId = $"summary-marker-no-progress-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (DurableChatWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions(workflowId, taskQueue));

        try
        {
            var status = await WaitForHistoryBlockedAsync(handle);
            Assert.True(status.IsHistoryBlocked);
            Assert.Equal(0, model.CallCount);
            await handle.ExecuteUpdateAsync(workflow => workflow.RequestShutdownAsync());
            await handle.GetResultAsync().WaitAsync(Deadline);
            await AssertChatReplayAsync(handle);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryRecovery_RejectsFullContinueAsNewCandidateEvenWhenProjectionFits(bool retry)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new TestChatClient();
        var taskQueue = $"recovery-full-candidate-{Guid.NewGuid():N}";
        const int payloadLimit = 16_000;
        const string reducerKey = "recovery-last-turn";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maxEntryCount: 4,
            reducerKey: retry ? reducerKey : null,
            reducer: retry ? entries => entries.TakeLast(2).ToList() : null,
            maximumTransportPayloadSizeBytes: payloadLimit);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"recovery-full-candidate-{Guid.NewGuid():N}";
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));

        try
        {
            await session.SendAsync(conversationId, new string('a', 3_000)).WaitAsync(Deadline);
            var originalRunId = (await handle.DescribeAsync()).RunId;
            await session.SendAsync(conversationId, new string('b', 3_000)).WaitAsync(Deadline);
            Assert.True((await WaitForHistoryBlockedAsync(handle)).IsHistoryBlocked);
            var retained = await handle.QueryAsync(workflow => workflow.GetHistory());
            var projection = retained.TakeLast(2).ToList();
            Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                DurableAIDataConverter.Instance.PayloadConverter,
                projection, payloadLimit, 0, "test retained projection");

            var recovery = retry
                ? await session.RetryHistoryCompactionAsync(conversationId, "retry-full-candidate")
                    .WaitAsync(Deadline)
                : await session.TrimBlockedHistoryAsync(conversationId, "trim-full-candidate")
                    .WaitAsync(Deadline);

            Assert.True(recovery.IsBlocked);
            Assert.Equal(retry ? "retry-failed" : "trim-failed", recovery.Outcome);
            var status = await handle.QueryAsync(workflow => workflow.GetHistoryBudget());
            Assert.True(status.IsHistoryBlocked);
            Assert.Equal("transport-payload-too-large", status.HistoryBlockReason);
            Assert.Equal(originalRunId, (await handle.DescribeAsync()).RunId);
            Assert.Equal(4, status.RetainedHistoryEntryCount);
            Assert.Equal(2, model.CallCount);
            await handle.ExecuteUpdateAsync(workflow => workflow.RequestShutdownAsync());
            await handle.GetResultAsync().WaitAsync(Deadline);
            await AssertChatReplayAsync(handle);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task EventBudgetContinueAsNew_AppliesConfiguredTrimBelowEntryCountTrigger()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var taskQueue = $"event-budget-compaction-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client, taskQueue, new TestChatClient(),
            maxEntryCount: 1000,
            historyEventSoftLimit: 20,
            historyEventReserveLimit: 1000,
            historyCompactionStrategy: DurableHistoryCompactionStrategy.TrimRecentTurns);
        await host.StartAsync();
        var input = host.Services.GetRequiredService<IDurableChatWorkflowInputFactory>().Create() with
        {
            CarriedHistory =
            [
                DurableSessionRequest.FromMessages([new ChatMessage(ChatRole.User, "old 1")], "old-1"),
                DurableSessionResponse.FromChatResponse("old-1",
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer 1")),
                    DateTimeOffset.UtcNow, DurableTurnCompletionReason.FinalResponse),
                DurableSessionRequest.FromMessages([new ChatMessage(ChatRole.User, "old 2")], "old-2"),
                DurableSessionResponse.FromChatResponse("old-2",
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer 2")),
                    DateTimeOffset.UtcNow, DurableTurnCompletionReason.FinalResponse),
            ],
        };
        var workflowId = $"event-budget-compaction-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (DurableChatWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions(workflowId, taskQueue));
        var originalRunId = (await handle.DescribeAsync()).RunId;

        try
        {
            for (var i = 0; i < 10 && (await handle.DescribeAsync()).RunId == originalRunId; i++)
            {
                try
                {
                    await handle.ExecuteUpdateAsync(workflow => workflow.ChatAsync(new DurableChatInput
                    {
                        Messages = new List<ChatMessage> { new(ChatRole.User, "new turn") },
                    })).WaitAsync(Deadline);
                }
                catch (WorkflowUpdateFailedException)
                {
                    // Soft-budget admission can close just before the transition.
                    break;
                }
            }
            await WaitForRunChangeAsync(handle, originalRunId);
            var oldRun = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId, originalRunId);
            var history = await oldRun.FetchHistoryAsync();
            var transition = Assert.Single(history.Events,
                ev => ev.WorkflowExecutionContinuedAsNewEventAttributes is not null);
            var carried = Assert.IsType<DurableChatWorkflowInput>(
                DurableAIDataConverter.Instance.PayloadConverter.ToValue(
                    Assert.Single(transition.WorkflowExecutionContinuedAsNewEventAttributes.Input.Payloads_),
                    typeof(DurableChatWorkflowInput)));
            Assert.True(carried.CarriedHistory!.Count < 1000);
            Assert.Contains(carried.CarriedHistory, entry => entry is TruncationMarkerEntry);
            Assert.DoesNotContain(carried.CarriedHistory, entry => entry.CorrelationId == "old-1");
            await AssertChatReplayAsync(oldRun);
            await handle.ExecuteUpdateAsync(workflow => workflow.RequestShutdownAsync());
            await handle.GetResultAsync().WaitAsync(Deadline);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task HistoryRecovery_EnforcesDuplicateAndIncidentRetryLimitsAcrossContinueAsNew()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var taskQueue = $"history-recovery-limits-{Guid.NewGuid():N}";
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> noProgress =
            entries => entries.ToList();
        using var host = BuildHost(
            env.Client,
            taskQueue,
            new TestChatClient(),
            maxEntryCount: 4,
            reducerKey: "no-progress-reducer",
            reducer: noProgress);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        const string conversationId = "history-recovery-limits";
        var workflow = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));

        try
        {
            await session.SendAsync(conversationId, "first").WaitAsync(Deadline);
            await session.SendAsync(conversationId, "second").WaitAsync(Deadline);
            var status = await WaitForHistoryBlockedAsync(workflow);
            Assert.True(status.IsHistoryBlocked);

            var operationNumber = 0;
            string? lastRunId = (await workflow.DescribeAsync()).RunId;
            for (var incidentRetry = 1; incidentRetry <= 9; incidentRetry++)
            {
                var operationId = $"retry-{++operationNumber}";
                var result = await session.RetryHistoryCompactionAsync(
                    conversationId,
                    operationId).WaitAsync(Deadline);
                Assert.True(result.IsBlocked);
                Assert.Equal("retry-failed", result.Outcome);

                if (incidentRetry == 1)
                {
                    var acceptedBeforeDuplicate = await CountAcceptedUpdatesAsync(workflow);
                    var duplicate = await Record.ExceptionAsync(() =>
                        session.RetryHistoryCompactionAsync(
                            conversationId,
                            operationId).WaitAsync(Deadline));
                    Assert.IsType<Temporalio.Exceptions.WorkflowUpdateFailedException>(duplicate);
                    Assert.Equal(acceptedBeforeDuplicate, await CountAcceptedUpdatesAsync(workflow));
                }

                if (incidentRetry is 3 or 6)
                {
                    await WaitForRunChangeAsync(workflow, lastRunId!);
                    lastRunId = (await workflow.DescribeAsync()).RunId;
                    status = await workflow.QueryAsync<DurableChatWorkflow, DurableChatHistoryBudgetStatus>(
                        wf => wf.GetHistoryBudget());
                    Assert.Equal(incidentRetry, status.HistoryRecoveryRetriesThisIncident);
                    Assert.Equal(0, status.HistoryRecoveryRetriesThisRun);
                    Assert.True(status.IsHistoryBlocked);
                }
            }

            status = await workflow.QueryAsync<DurableChatWorkflow, DurableChatHistoryBudgetStatus>(
                wf => wf.GetHistoryBudget());
            Assert.Equal(9, status.HistoryRecoveryRetriesThisIncident);
            Assert.Equal(3, status.HistoryRecoveryRetriesThisRun);

            var acceptedBeforeLimit = await CountAcceptedUpdatesAsync(workflow);
            var overLimit = await Record.ExceptionAsync(() =>
                session.RetryHistoryCompactionAsync(
                    conversationId,
                    "retry-over-limit").WaitAsync(Deadline));
            Assert.IsType<Temporalio.Exceptions.WorkflowUpdateFailedException>(overLimit);
            Assert.Equal(acceptedBeforeLimit, await CountAcceptedUpdatesAsync(workflow));

            var trim = await session.TrimBlockedHistoryAsync(
                conversationId,
                "trim-after-retries").WaitAsync(Deadline);
            Assert.False(trim.IsBlocked);
            Assert.Equal("trim-succeeded", trim.Outcome);
            await WaitForRunChangeAsync(workflow, lastRunId!);

            var recoveredHistory = await session.GetHistoryAsync(conversationId);
            Assert.Contains(recoveredHistory, entry => entry is TruncationMarkerEntry);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task HistoryRecovery_DrainsPendingTurnAndApprovalBeforeRestart(
        bool explicitTrim, bool completeDuringRecovery)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var reducerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryReducerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecoveryReducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reducerCalls = 0;
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> reducer = entries =>
        {
            var call = Interlocked.Increment(ref reducerCalls);
            if (call == 1)
            {
                reducerEntered.TrySetResult();
                releaseReducer.Task.GetAwaiter().GetResult();
                return entries.ToList();
            }
            if (call == 2 && completeDuringRecovery)
            {
                recoveryReducerEntered.TrySetResult();
                releaseRecoveryReducer.Task.GetAwaiter().GetResult();
            }
            return entries.TakeLast(entries.Last() is DurableSessionRequest ? 3 : 2).ToList();
        };
        var model = new GatedChatClient(gatedCall: 5);
        using var host = BuildHost(
            env.Client, $"recovery-handler-drain-{Guid.NewGuid():N}", model,
            maxEntryCount: 8, reducerKey: "recovery-handler-drain", reducer: reducer);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        const string conversationId = "recovery-handler-drain";
        var workflow = env.Client.GetWorkflowHandle<DurableChatWorkflow>(session.GetWorkflowId(conversationId));
        var request = new DurableApprovalRequest
        {
            RequestId = "recovery-approval",
            CallId = "recovery-call",
            FunctionName = "review",
        };
        try
        {
            await session.SendAsync(conversationId, "first").WaitAsync(Deadline);
            await session.SendAsync(conversationId, "second").WaitAsync(Deadline);
            await session.SendAsync(conversationId, "third").WaitAsync(Deadline);
            var oldRunId = (await workflow.DescribeAsync()).RunId;
            var approval = workflow.ExecuteUpdateAsync(wf => wf.RequestApprovalAsync(request));
            await WaitForPendingApprovalAsync(workflow);
            await session.SendAsync(conversationId, "fourth").WaitAsync(Deadline);
            await reducerEntered.Task.WaitAsync(Deadline);

            var turn = session.SendAsync(conversationId, "fifth", requestId: "recovery-fifth");
            await model.Entered.WaitAsync(Deadline);
            releaseReducer.TrySetResult();
            await WaitForHistoryBlockedAsync(workflow);
            var recoveryTask = explicitTrim
                ? session.TrimBlockedHistoryAsync(conversationId, "recover-drain")
                : session.RetryHistoryCompactionAsync(conversationId, "recover-drain");
            if (completeDuringRecovery)
            {
                await recoveryReducerEntered.Task.WaitAsync(Deadline);
                model.Release();
                await turn.WaitAsync(Deadline);
                releaseRecoveryReducer.TrySetResult();
            }
            var recovery = await recoveryTask.WaitAsync(Deadline);
            Assert.False(recovery.IsBlocked);

            // A query round trip makes this independent of client-side update completion timing.
            var pendingApproval = await workflow.QueryAsync<DurableChatWorkflow, DurableApprovalRequest?>(
                wf => wf.GetPendingApproval());
            if (pendingApproval is null)
            {
                var approvalFailure = await Record.ExceptionAsync(() => approval.WaitAsync(Deadline));
                var turnFailure = await Record.ExceptionAsync(() => turn.WaitAsync(Deadline));
                var carried = await session.GetHistoryAsync(conversationId);
                Assert.Fail($"Recovery abandoned handlers. Approval caller: {approvalFailure}; " +
                    $"chat caller: {turnFailure}; carried entries: {carried.Count}; " +
                    $"completed turns: {await workflow.QueryAsync<DurableChatWorkflow, int>(wf => wf.GetCompletedTurnCount())}.");
            }
            Assert.NotNull(pendingApproval);
            Assert.Equal(oldRunId, (await workflow.DescribeAsync()).RunId);
            Assert.False(approval.IsCompleted);
            Assert.Equal(completeDuringRecovery, turn.IsCompleted);

            model.Release();
            Assert.Equal("completed admitted turn", (await turn.WaitAsync(Deadline)).Text);
            await session.ResolveApprovalAsync(conversationId, new DurableApprovalDecision
            {
                RequestId = request.RequestId,
                CallId = request.CallId,
                FunctionName = request.FunctionName,
                Approved = true,
            }).WaitAsync(Deadline);
            Assert.True((await approval.WaitAsync(Deadline)).Approved);
            await WaitForRunChangeAsync(workflow, oldRunId);
            Assert.Equal(5, await workflow.QueryAsync<DurableChatWorkflow, int>(wf => wf.GetCompletedTurnCount()));
            var retained = await session.GetHistoryAsync(conversationId);
            var lastRequest = Assert.IsType<DurableSessionRequest>(retained[^2]);
            var lastResponse = Assert.IsType<DurableSessionResponse>(retained[^1]);
            Assert.Equal("recovery-fifth", lastRequest.RequestId);
            Assert.Equal(lastRequest.RequestId, lastResponse.RequestId);
            var retried = await session.SendAsync(conversationId, "fifth", requestId: "recovery-fifth")
                .WaitAsync(Deadline);
            Assert.Equal((await turn).Text, retried.Text);
            Assert.Equal(5, model.Calls);
            var duplicateDecision = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.ResolveApprovalAsync(conversationId, new DurableApprovalDecision
                {
                    RequestId = request.RequestId,
                    CallId = request.CallId,
                    FunctionName = request.FunctionName,
                    Approved = true,
                }));
            Assert.Contains("already resolved", duplicateDecision.ToString(), StringComparison.OrdinalIgnoreCase);
            await AssertChatReplayAsync(
                env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflow.Id, oldRunId));
            await workflow.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
            await workflow.GetResultAsync().WaitAsync(Deadline);
            await AssertChatReplayAsync(workflow);
        }
        finally
        {
            releaseReducer.TrySetResult();
            releaseRecoveryReducer.TrySetResult();
            model.Release();
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryRecovery_MissingStrategyDrainsApprovalTimeoutDespiteExpiredIdleDeadline(
        bool requestShutdown)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        using var host = BuildHost(
            env.Client, $"recovery-approval-timeout-{Guid.NewGuid():N}", new TestChatClient(),
            maxEntryCount: 4, sessionTimeToLive: TimeSpan.FromSeconds(5),
            approvalTimeout: TimeSpan.FromSeconds(8));
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        const string conversationId = "recovery-approval-timeout";
        var workflow = env.Client.GetWorkflowHandle<DurableChatWorkflow>(session.GetWorkflowId(conversationId));
        try
        {
            await session.SendAsync(conversationId, "first").WaitAsync(Deadline);
            var oldRunId = (await workflow.DescribeAsync()).RunId;
            var request = new DurableApprovalRequest
            {
                RequestId = "expires-during-recovery",
                CallId = "timeout-call",
                FunctionName = "review",
            };
            var approval = workflow.ExecuteUpdateAsync(wf => wf.RequestApprovalAsync(request));
            await WaitForPendingApprovalAsync(workflow);
            await session.SendAsync(conversationId, "second").WaitAsync(Deadline);
            await WaitForHistoryBlockedAsync(workflow);
            Assert.False((await session.TrimBlockedHistoryAsync(conversationId, "trim-timeout")
                .WaitAsync(Deadline)).IsBlocked);
            Assert.Equal(oldRunId, (await workflow.DescribeAsync()).RunId);
            if (requestShutdown)
            {
                await workflow.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
                Assert.False(approval.IsCompleted);
            }
            var decision = await approval.WaitAsync(Deadline);
            Assert.False(decision.Approved);
            Assert.Equal(DurableApprovalExpirationReason.Timeout, decision.ExpirationReason);
            await workflow.GetResultAsync().WaitAsync(Deadline);
            var oldRun = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflow.Id, oldRunId);
            var history = await oldRun.FetchHistoryAsync();
            Assert.Equal(!requestShutdown,
                history.Events.Any(ev => ev.WorkflowExecutionContinuedAsNewEventAttributes is not null));
            await AssertChatReplayAsync(oldRun);
            await AssertChatReplayAsync(workflow);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task HistoryRecovery_RejectsConcurrentRecoveryBeforeAcceptingUpdate()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var reducerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reducerCalls = 0;
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> gatedNoProgress = entries =>
        {
            if (Interlocked.Increment(ref reducerCalls) > 1)
            {
                reducerEntered.TrySetResult();
                releaseReducer.Task.GetAwaiter().GetResult();
            }

            return entries.ToList();
        };
        var taskQueue = $"history-recovery-concurrency-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            new TestChatClient(),
            maxEntryCount: 4,
            reducerKey: "gated-no-progress-reducer",
            reducer: gatedNoProgress);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        const string conversationId = "history-recovery-concurrency";
        var workflow = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));

        try
        {
            await session.SendAsync(conversationId, "first").WaitAsync(Deadline);
            await session.SendAsync(conversationId, "second").WaitAsync(Deadline);
            var status = await WaitForHistoryBlockedAsync(workflow);
            Assert.True(status.IsHistoryBlocked);

            var firstRecovery = session.RetryHistoryCompactionAsync(
                conversationId,
                "retry-in-flight");
            await reducerEntered.Task.WaitAsync(Deadline);

            var acceptedBeforeConcurrentRequest = await CountAcceptedUpdatesAsync(workflow);
            var concurrent = await Record.ExceptionAsync(() =>
                session.RetryHistoryCompactionAsync(
                    conversationId,
                    "retry-concurrent").WaitAsync(Deadline));
            Assert.IsType<Temporalio.Exceptions.WorkflowUpdateFailedException>(concurrent);
            Assert.Equal(
                acceptedBeforeConcurrentRequest,
                await CountAcceptedUpdatesAsync(workflow));

            releaseReducer.TrySetResult();
            var result = await firstRecovery.WaitAsync(Deadline);
            Assert.True(result.IsBlocked);
            Assert.Equal("retry-failed", result.Outcome);
        }
        finally
        {
            releaseReducer.TrySetResult();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task HistorySummarizerPayloadGuard_RejectsOversizedActivityResult()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer one")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer two")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, new string('s', 6000))),
        ]);
        var taskQueue = $"history-summary-result-guard-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maxEntryCount: 4,
            maximumTransportPayloadSizeBytes: 4096,
            historyCompactionStrategy: DurableHistoryCompactionStrategy.SummarizeOldestTurns);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        const string conversationId = "history-summary-result-guard";
        var workflow = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));

        try
        {
            await session.SendAsync(conversationId, "first").WaitAsync(Deadline);
            await session.SendAsync(conversationId, "second").WaitAsync(Deadline);

            var status = await WaitForHistoryBlockedAsync(workflow);

            Assert.Equal("DurableHistorySummaryResultTooLarge", status.HistoryBlockReason);
            Assert.Equal(4, status.RetainedHistoryEntryCount);
            Assert.Equal(3, model.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task HistoryQuery_RejectsOversizedQueryResult()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new TestChatClient();
        var taskQueue = $"payload-history-query-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maximumTransportPayloadSizeBytes: 4096);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();

        try
        {
            var conversationId = $"payload-history-query-{Guid.NewGuid():N}";
            await session.SendAsync(conversationId, new string('x', 1000)).WaitAsync(Deadline);
            await session.SendAsync(conversationId, new string('y', 1000)).WaitAsync(Deadline);

            var exception = await Record.ExceptionAsync(() =>
                session.GetHistoryAsync(conversationId).WaitAsync(Deadline));

            Assert.NotNull(exception);
            Assert.Contains("workflow query result", exception.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, model.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

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
                await newRun.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
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
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maxEntryCount: 4,
            historyCompactionStrategy: DurableHistoryCompactionStrategy.TrimRecentTurns);
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
            var newRun = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId, newRunId);
            var start = Assert.Single((await newRun.FetchHistoryAsync()).Events,
                ev => ev.WorkflowExecutionStartedEventAttributes is not null);
            var carriedInput = Assert.IsType<DurableChatWorkflowInput>(
                DurableAIDataConverter.Instance.PayloadConverter.ToValue(
                    Assert.Single(start.WorkflowExecutionStartedEventAttributes.Input.Payloads_),
                    typeof(DurableChatWorkflowInput)));
            var carriedFailure = Assert.Single(carriedInput.CarriedTerminalOutcomes!,
                outcome => outcome.RequestId == "retained-failure");
            Assert.NotNull(carriedFailure.Failure);

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
                await run.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
                await run.GetResultAsync().WaitAsync(Deadline);
            }
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task HistoryBudget_BlocksNewTurnsAndExposesUsage()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new GatedChatClient();
        var taskQueue = $"history-budget-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            historyEventSoftLimit: 1,
            historyEventReserveLimit: 2);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"history-budget-{Guid.NewGuid():N}";
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));

        try
        {
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(
                    conversationId,
                    [new ChatMessage(ChatRole.User, "must not dispatch")],
                    requestId: "history-budget-first"));
            Assert.Equal(0, model.Calls);

            var status = await session.GetHistoryBudgetAsync(conversationId).WaitAsync(Deadline);
            Assert.True(
                status.IsAdmissionRestricted,
                $"History events {status.HistoryEventCount}/{status.HistoryEventSoftLimit}; " +
                $"size {status.HistorySizeBytes}/{status.HistorySizeSoftLimitBytes}.");
            Assert.True(status.HistoryEventCount >= status.HistoryEventSoftLimit);

            var acceptedUpdatesBeforeRetry = await CountAcceptedUpdatesAsync(handle);
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(
                    conversationId,
                    [new ChatMessage(ChatRole.User, "must not dispatch")],
                    requestId: "history-budget-retry"));
            Assert.Equal(acceptedUpdatesBeforeRetry, await CountAcceptedUpdatesAsync(handle));
            Assert.Equal(0, model.Calls);
        }
        finally
        {
            try
            {
                await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
                await handle.GetResultAsync().WaitAsync(Deadline);
            }
            catch (Exception) { /* Preserve the original assertion failure. */ }
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TerminalOutcomeQueryPreservesAmbiguousResultWithoutAdmittingRetry()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var taskQueue = $"terminal-outcome-budget-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            new TestChatClient(),
            maxEntryCount: 4);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"terminal-outcome-budget-{Guid.NewGuid():N}";
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));
        const string requestId = "terminal-outcome-request";
        var messages = new[] { new ChatMessage(ChatRole.User, "retained answer") };

        try
        {
            var response = await session.SendAsync(conversationId, messages, requestId: requestId);
            Assert.Equal(requestId, response.RequestId);

            await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "complete history before recovery")],
                requestId: "terminal-outcome-budget-filler");
            var status = await WaitForHistoryBlockedAsync(handle);
            Assert.True(
                status.IsHistoryBlocked,
                $"History recovery did not block admission: {status.HistoryBlockReason}.");
            var acceptedUpdatesBeforeRetry = await CountAcceptedUpdatesAsync(handle);
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(conversationId, messages, requestId: requestId));
            Assert.Equal(acceptedUpdatesBeforeRetry, await CountAcceptedUpdatesAsync(handle));

            var terminal = await session.GetTerminalTurnOutcomeAsync(conversationId, requestId)
                .WaitAsync(Deadline);
            Assert.NotNull(terminal);
            Assert.Equal(requestId, terminal.RequestId);
            Assert.Equal(response.Text, terminal.Response!.Text);
            Assert.Null(terminal.FailureErrorType);
        }
        finally
        {
            try
            {
                await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
                await handle.GetResultAsync().WaitAsync(Deadline);
            }
            catch (Exception) { /* Preserve the original assertion failure. */ }
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task UnhandledRawSignal_IsStillRecordedOutsideLibraryAdmissionBudget()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var taskQueue = $"unhandled-signal-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, new TestChatClient());
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"unhandled-signal-{Guid.NewGuid():N}";
        var workflowId = session.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
        var rawHandle = env.Client.GetWorkflowHandle(workflowId);

        try
        {
            await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "Start a session")]);
            var before = await session.GetHistoryBudgetAsync(conversationId).WaitAsync(Deadline);

            await rawHandle.SignalAsync("ExternalUnmatchedSignal", Array.Empty<object?>());
            await WaitForHistoryEventAsync(
                rawHandle,
                historyEvent => historyEvent.WorkflowExecutionSignaledEventAttributes?.SignalName
                                == "ExternalUnmatchedSignal");

            var after = await session.GetHistoryBudgetAsync(conversationId).WaitAsync(Deadline);
            Assert.True(after.HistoryEventCount > before.HistoryEventCount);
        }
        finally
        {
            await handle.ExecuteUpdateAsync(workflow => workflow.RequestShutdownAsync()).WaitAsync(Deadline);
            await handle.GetResultAsync().WaitAsync(Deadline);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task PendingApproval_ExpiresWhenHistoryReserveIsReached()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new GatedChatClient();
        var taskQueue = $"approval-history-reserve-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            historyEventSoftLimit: 12,
            historyEventReserveLimit: 18,
            maxConcurrentTurnJoiners: 16);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"approval-history-reserve-{Guid.NewGuid():N}";
        var workflowId = session.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
        var approvalRequest = new DurableApprovalRequest
        {
            RequestId = $"approval-{Guid.NewGuid():N}",
            FunctionName = "review",
            Description = "Wait for history reserve test",
        };
        Task<DurableApprovalDecision>? approvalTask = null;
        Task<DurableSessionResponse>? owner = null;
        var joiners = new List<Task<DurableSessionResponse>>();

        try
        {
            var input = new DurableChatWorkflowInput
            {
                TimeToLive = TimeSpan.FromMinutes(5),
                ActivityTimeout = TimeSpan.FromSeconds(30),
                HeartbeatTimeout = TimeSpan.FromSeconds(10),
                ApprovalTimeout = TimeSpan.FromMinutes(1),
                HistoryEventSoftLimit = 12,
                HistoryEventReserveLimit = 18,
                ToolActivityOptions = new Dictionary<string, Temporalio.Workflows.ActivityOptions>(),
            };
            var start = WithStartWorkflowOperation.Create(
                (DurableChatWorkflow workflow) => workflow.RunAsync(input),
                new WorkflowOptions(workflowId, taskQueue)
                {
                    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                });
            approvalTask = env.Client.ExecuteUpdateWithStartWorkflowAsync<
                DurableChatWorkflow, DurableApprovalDecision>(
                workflow => workflow.RequestApprovalAsync(approvalRequest),
                new WorkflowUpdateWithStartOptions(start));

            var pending = await WaitForPendingApprovalAsync(handle);
            Assert.Equal(approvalRequest.RequestId, pending.RequestId);
            var initialStatus = await session.GetHistoryBudgetAsync(conversationId).WaitAsync(Deadline);
            Assert.True(initialStatus.HistoryEventCount < initialStatus.HistoryEventSoftLimit);

            owner = session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "Reserve history")],
                requestId: "reserve-trigger");
            var turnStart = await Task.WhenAny(model.Entered, owner).WaitAsync(Deadline);
            if (turnStart == owner)
            {
                await owner;
            }
            await model.Entered.WaitAsync(Deadline);
            for (var i = 0; i < 12; i++)
            {
                joiners.Add(session.SendAsync(
                    conversationId,
                    [new ChatMessage(ChatRole.User, "Reserve history")],
                    requestId: "reserve-trigger"));
            }

            var reserveStatus = await WaitForHistoryReserveAsync(session, conversationId);
            Assert.True(reserveStatus.IsReserveReached);

            var decision = await approvalTask.WaitAsync(Deadline);
            Assert.False(decision.Approved);
            Assert.Equal(DurableApprovalExpirationReason.HistoryBudget, decision.ExpirationReason);
            model.Release();
            await owner.WaitAsync(Deadline);
            foreach (var joiner in joiners)
            {
                try { await joiner.WaitAsync(Deadline); }
                catch (WorkflowUpdateFailedException) { /* Duplicate updates can lose admission races. */ }
            }
        }
        finally
        {
            model.Release();
            if (approvalTask is { IsCompleted: false })
            {
                try
                {
                    await handle.ExecuteUpdateAsync(workflow => workflow.ResolveApprovalAsync(
                        new DurableApprovalDecision
                        {
                            RequestId = approvalRequest.RequestId,
                            Approved = false,
                            Reason = "Test cleanup",
                        })).WaitAsync(Deadline);
                }
                catch (Exception) { /* Preserve the original assertion failure. */ }
                try { await approvalTask.WaitAsync(Deadline); }
                catch (Exception) { /* Drain the pending update. */ }
            }
            try
            {
                await handle.ExecuteUpdateAsync(workflow => workflow.RequestShutdownAsync()).WaitAsync(Deadline);
                await handle.GetResultAsync().WaitAsync(Deadline);
            }
            catch (Exception) { /* Preserve the original assertion failure. */ }
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task MatchingTurnJoiners_AreCappedBeforeUpdateAcceptance()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new GatedChatClient();
        var taskQueue = $"turn-joiner-cap-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            maxConcurrentTurnJoiners: 1);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"turn-joiner-cap-{Guid.NewGuid():N}";
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));
        Task<DurableSessionResponse>? joined = null;
        Task<DurableSessionResponse>? owner = null;

        try
        {
            owner = session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "one logical turn")],
                requestId: "bounded-joiner");
            await model.Entered.WaitAsync(Deadline);
            joined = session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "one logical turn")],
                requestId: "bounded-joiner");

            var joiners = await WaitForJoinerCountAsync(session, conversationId, 1);
            Assert.Equal(1, joiners);
            var acceptedUpdatesBeforeRejection = await CountAcceptedUpdatesAsync(handle);
            var rejected = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(
                    conversationId,
                    [new ChatMessage(ChatRole.User, "one logical turn")],
                    requestId: "bounded-joiner"));
            Assert.Contains("joiner limit", rejected.InnerException?.Message ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(acceptedUpdatesBeforeRejection, await CountAcceptedUpdatesAsync(handle));

            model.Release();
            var results = await Task.WhenAll(owner, joined).WaitAsync(Deadline);
            Assert.Equal(results[0].RequestId, results[1].RequestId);
            Assert.Equal(1, model.Calls);
        }
        finally
        {
            model.Release();
            if (owner is not null)
            {
                try { await owner.WaitAsync(Deadline); } catch (Exception) { }
            }
            if (joined is not null)
            {
                try { await joined.WaitAsync(Deadline); } catch (Exception) { }
            }
            try
            {
                await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
                await handle.GetResultAsync().WaitAsync(Deadline);
            }
            catch (Exception) { /* Preserve the original assertion failure. */ }
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task CancelledTurn_DoesNotRenewExpiredIdleDeadline_AndReplays()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new GatedChatClient(heartbeatWhileWaiting: true);
        var taskQueue = $"cancel-idle-deadline-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client, taskQueue, model, sessionTimeToLive: TimeSpan.FromSeconds(5),
            heartbeatTimeout: TimeSpan.FromSeconds(2));
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"cancel-idle-deadline-{Guid.NewGuid():N}";
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));
        Task<DurableSessionResponse>? turn = null;
        try
        {
            turn = session.SendAsync(conversationId,
                [new ChatMessage(ChatRole.User, "cancel after idle expiry")],
                requestId: "cancel-idle-turn");
            await model.Entered.WaitAsync(Deadline);
            await Task.Delay(TimeSpan.FromMilliseconds(5200));
            await session.CancelTurnAsync(conversationId, "cancel-idle-turn").WaitAsync(Deadline);
            var failure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                turn.WaitAsync(Deadline));
            Assert.Equal("DurableTurnCancelled",
                Assert.IsType<ApplicationFailureException>(failure.InnerException).ErrorType);

            // A cancellation must not buy another five-second idle window after the expired
            // deadline has already closed admission and drained this turn.
            await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, model.Calls);
            await AssertChatReplayAsync(handle);
        }
        finally
        {
            model.Release();
            if (turn is not null)
            {
                try { await turn.WaitAsync(Deadline); } catch (WorkflowUpdateFailedException) { }
            }
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovalResolution_CompletedHistoryReplays(bool approved)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new TestChatClient();
        var taskQueue = $"approval-replay-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, model);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"approval-replay-{Guid.NewGuid():N}";
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));
        Task<DurableApprovalDecision>? approval = null;
        var request = new DurableApprovalRequest
        {
            RequestId = "review-request",
            CallId = "review-call",
            FunctionName = "review-tool",
            Description = "Review before continuing",
        };
        try
        {
            await session.SendAsync(conversationId, "start approval session").WaitAsync(Deadline);
            approval = handle.ExecuteUpdateAsync(workflow => workflow.RequestApprovalAsync(request));
            Assert.Equal(request.RequestId, (await WaitForPendingApprovalAsync(handle)).RequestId);
            var resolution = await session.ResolveApprovalAsync(conversationId, new DurableApprovalDecision
            {
                RequestId = request.RequestId,
                CallId = request.CallId,
                FunctionName = request.FunctionName,
                Approved = approved,
            }).WaitAsync(Deadline);
            Assert.Equal(DurableApprovalResolutionStatus.Accepted, resolution.Status);
            Assert.Equal(approved, (await approval.WaitAsync(Deadline)).Approved);
            await handle.ExecuteUpdateAsync(workflow => workflow.RequestShutdownAsync());
            await handle.GetResultAsync().WaitAsync(Deadline);
            await AssertChatReplayAsync(handle);
            Assert.Equal(1, model.CallCount);
        }
        finally
        {
            if (approval is { IsCompleted: false })
            {
                await session.ResolveApprovalAsync(conversationId, new DurableApprovalDecision
                {
                    RequestId = request.RequestId,
                    CallId = request.CallId,
                    FunctionName = request.FunctionName,
                    Approved = false,
                }).WaitAsync(Deadline);
                await approval.WaitAsync(Deadline);
            }
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SessionIdleDeadline_RenewsWhenLongRunningTurnCompletes()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new GatedChatClient();
        var taskQueue = $"session-idle-renewal-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            sessionTimeToLive: TimeSpan.FromSeconds(1));
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"session-idle-renewal-{Guid.NewGuid():N}";
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));
        Task<DurableSessionResponse>? pending = null;

        try
        {
            pending = session.SendAsync(conversationId, "long-running turn");
            await model.Entered.WaitAsync(Deadline);
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            model.Release();

            var completed = await pending.WaitAsync(Deadline);
            Assert.Equal("completed admitted turn", completed.Text);

            var followUp = await session.SendAsync(conversationId, "after completion")
                .WaitAsync(Deadline);
            Assert.Equal("completed admitted turn", followUp.Text);
            Assert.NotNull(await handle.QueryAsync<DurableChatWorkflow, string>(
                wf => wf.GetSessionEpoch()).WaitAsync(Deadline));

            await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
            await handle.GetResultAsync().WaitAsync(Deadline);
        }
        finally
        {
            model.Release();
            if (pending is not null)
            {
                try { await pending.WaitAsync(Deadline); }
                catch (Exception) { /* Preserve the original assertion failure. */ }
            }
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ExpiredSession_RestartsWithNewEpochAndRejectsStaleExpectedEpoch()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new TestChatClient();
        var taskQueue = $"session-epoch-restart-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            sessionTimeToLive: TimeSpan.FromSeconds(1));
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"session-epoch-restart-{Guid.NewGuid():N}";
        var workflowId = session.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
        var currentRunStarted = false;
        var currentRunCompleted = false;

        try
        {
            await session.SendAsync(conversationId, "first lineage").WaitAsync(Deadline);
            var originalEpoch = await session.GetSessionEpochAsync(conversationId).WaitAsync(Deadline);
            await handle.GetResultAsync().WaitAsync(Deadline);

            var callsBeforeStaleRequest = model.CallCount;
            currentRunStarted = true;
            await Assert.ThrowsAsync<DurableChatSessionEpochMismatchException>(() =>
                session.SendAsync(
                    conversationId,
                    "must not run in a new lineage",
                    requestId: "stale-epoch-request",
                    expectedSessionEpoch: originalEpoch).WaitAsync(Deadline));

            var newEpoch = await session.GetSessionEpochAsync(conversationId).WaitAsync(Deadline);
            Assert.NotEqual(originalEpoch, newEpoch);
            Assert.Equal(callsBeforeStaleRequest, model.CallCount);

            await session.SendAsync(
                conversationId,
                "new lineage",
                requestId: "stale-epoch-request",
                expectedSessionEpoch: newEpoch).WaitAsync(Deadline);
            Assert.Equal(callsBeforeStaleRequest + 1, model.CallCount);

            await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
            await handle.GetResultAsync().WaitAsync(Deadline);
            currentRunCompleted = true;
        }
        finally
        {
            if (currentRunStarted && !currentRunCompleted)
            {
                try
                {
                    await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
                    await handle.GetResultAsync().WaitAsync(Deadline);
                }
                catch (Exception) { /* Preserve the original assertion failure. */ }
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

            await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
            var acceptedUpdatesBeforeRetry = await CountAcceptedUpdatesAsync(handle);
            var repeatedShutdown = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()));
            Assert.Contains("already been requested",
                repeatedShutdown.InnerException?.Message ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(acceptedUpdatesBeforeRetry, await CountAcceptedUpdatesAsync(handle));
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

    [Theory]
    [InlineData("system", DurableHistoryCompactionStrategy.None)]
    [InlineData("developer", DurableHistoryCompactionStrategy.None)]
    [InlineData("system", DurableHistoryCompactionStrategy.TrimRecentTurns)]
    [InlineData("developer", DurableHistoryCompactionStrategy.TrimRecentTurns)]
    public async Task HistoryTrim_RefusesToDiscardInstructionBearingTurn(
        string instructionRole,
        DurableHistoryCompactionStrategy strategy)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var taskQueue = $"protected-instruction-trim-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            new TestChatClient(),
            maxEntryCount: 4,
            historyCompactionStrategy: strategy);
        await host.StartAsync();
        var session = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"protected-instruction-trim-{Guid.NewGuid():N}";
        var workflow = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
            session.GetWorkflowId(conversationId));

        try
        {
            await session.SendAsync(conversationId,
            [
                new ChatMessage(new ChatRole(instructionRole), "instructions must survive"),
                new ChatMessage(ChatRole.User, "first"),
            ]).WaitAsync(Deadline);
            await session.SendAsync(conversationId, "second").WaitAsync(Deadline);
            await WaitForHistoryBlockedAsync(workflow);
            var before = await session.GetHistoryAsync(conversationId);
            var runId = (await workflow.DescribeAsync()).RunId;

            var recovery = await session.TrimBlockedHistoryAsync(
                conversationId, "refuse-instruction-trim").WaitAsync(Deadline);

            Assert.True(recovery.IsBlocked);
            Assert.Equal("trim-failed", recovery.Outcome);
            var after = await session.GetHistoryAsync(conversationId);
            Assert.Equal(4, after.Count);
            Assert.Equal(before.Select(entry => entry.EntryId), after.Select(entry => entry.EntryId));
            var request = Assert.IsType<DurableSessionRequest>(after[0]);
            Assert.Equal(new ChatRole(instructionRole), request.Messages[0].Role);
            Assert.Equal("instructions must survive", request.Messages[0].Text);
            Assert.DoesNotContain(after, entry => entry is TruncationMarkerEntry);
            Assert.Equal(runId, (await workflow.DescribeAsync()).RunId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task AssertChatReplayAsync(WorkflowHandle<DurableChatWorkflow> handle)
    {
        var options = new WorkflowReplayerOptions { DataConverter = DurableAIDataConverter.Instance };
        options.AddWorkflow<DurableChatWorkflow>();
        var replay = await new WorkflowReplayer(options).ReplayWorkflowAsync(
            await handle.FetchHistoryAsync().WaitAsync(Deadline), throwOnReplayFailure: false);
        Assert.Null(replay.ReplayFailure);
    }

    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient model,
        int maxEntryCount = 100,
        string? reducerKey = null,
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? reducer = null,
        int maxTerminalOutcomeCount = 128,
        int maxTerminalOutcomeBytes = 1024 * 1024,
        TimeSpan? sessionTimeToLive = null,
        int maximumTransportPayloadSizeBytes = 1024 * 1024,
        int historyEventSoftLimit = 40_000,
        int historyEventReserveLimit = 45_000,
        int maxConcurrentTurnJoiners = 32,
        DurableHistoryCompactionStrategy historyCompactionStrategy = DurableHistoryCompactionStrategy.None,
        TimeSpan? heartbeatTimeout = null,
        TimeSpan? approvalTimeout = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(model).Build();
        builder.Services.AddHostedTemporalWorker(taskQueue).AddDurableAI(options =>
        {
            options.MaxEntryCount = maxEntryCount;
            options.MaxTerminalOutcomeCount = maxTerminalOutcomeCount;
            options.MaxTerminalOutcomeBytes = maxTerminalOutcomeBytes;
            options.MaximumTransportPayloadSizeBytes = maximumTransportPayloadSizeBytes;
            options.HistoryEventSoftLimit = historyEventSoftLimit;
            options.HistoryEventReserveLimit = historyEventReserveLimit;
            options.MaxConcurrentTurnJoiners = maxConcurrentTurnJoiners;
            options.DefaultHistoryCompactionStrategy = historyCompactionStrategy;
            options.ActivityTimeout = TimeSpan.FromSeconds(60);
            if (approvalTimeout is { } approval)
            {
                options.ApprovalTimeout = approval;
            }
            if (heartbeatTimeout is { } heartbeat)
            {
                options.HeartbeatTimeout = heartbeat;
            }
            options.SessionTimeToLive = sessionTimeToLive ?? TimeSpan.FromMinutes(5);
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

    private static async Task WaitForRunChangeAsync(WorkflowHandle handle, string originalRunId)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            if ((await handle.DescribeAsync()).RunId is { } runId && runId != originalRunId)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        var description = await handle.DescribeAsync();
        var historyBudget = await handle.QueryAsync<DurableChatWorkflow, DurableChatHistoryBudgetStatus>(
            wf => wf.GetHistoryBudget());
        throw new TimeoutException(
            $"Expected continue-as-new to start a new workflow run. Current run {description.RunId} " +
            $"is {description.Status}; blocked={historyBudget.IsHistoryBlocked}; " +
            $"reason={historyBudget.HistoryBlockReason}; recovery={historyBudget.HistoryRecoveryLastOutcome}; " +
            $"entries={historyBudget.RetainedHistoryEntryCount}; events={historyBudget.HistoryEventCount}; " +
            $"size={historyBudget.HistorySizeBytes}.");
    }

    private static async Task<DurableChatHistoryBudgetStatus> WaitForHistoryBlockedAsync(
        WorkflowHandle handle)
    {
        var deadline = DateTime.UtcNow + Deadline;
        DurableChatHistoryBudgetStatus? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = await handle.QueryAsync<DurableChatWorkflow, DurableChatHistoryBudgetStatus>(
                wf => wf.GetHistoryBudget());
            if (status.IsHistoryBlocked)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException(
            $"History did not become blocked before the test deadline. Last state: {status}");
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

    private static async Task<DurableApprovalRequest> WaitForPendingApprovalAsync(
        WorkflowHandle<DurableChatWorkflow> handle)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            try
            {
                var pending = await handle.QueryAsync(workflow => workflow.GetPendingApproval())
                    .WaitAsync(Deadline);
                if (pending is not null)
                    return pending;
            }
            catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
            {
                // Update-with-start may not have created the workflow yet.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        throw new TimeoutException("Approval request did not become pending.");
    }

    private static async Task<int> WaitForJoinerCountAsync(
        DurableChatSessionClient session,
        string conversationId,
        int expectedCount)
    {
        var until = DateTime.UtcNow + Deadline;
        DurableChatHistoryBudgetStatus? latest = null;
        while (DateTime.UtcNow < until)
        {
            latest = await session.GetHistoryBudgetAsync(conversationId).WaitAsync(Deadline);
            if (latest.ConcurrentTurnJoiners == expectedCount)
                return latest.ConcurrentTurnJoiners;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        throw new TimeoutException(
            $"Expected {expectedCount} active turn joiner(s); observed {latest?.ConcurrentTurnJoiners}. " +
            $"History events: {latest?.HistoryEventCount}; reserve: {latest?.HistoryEventReserveLimit}.");
    }

    private static async Task<DurableChatHistoryBudgetStatus> WaitForHistoryReserveAsync(
        DurableChatSessionClient session,
        string conversationId)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            var status = await session.GetHistoryBudgetAsync(conversationId).WaitAsync(Deadline);
            if (status.IsReserveReached)
                return status;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        throw new TimeoutException("Workflow history did not reach the approval reserve.");
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

    private sealed class GatedChatClient(bool heartbeatWhileWaiting = false, int gatedCall = 1) : IChatClient
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
            if (Interlocked.Increment(ref _calls) < gatedCall)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "completed admitted turn"));
            }
            _entered.TrySetResult();
            if (heartbeatWhileWaiting)
            {
                while (!_release.Task.IsCompleted)
                {
                    Temporalio.Activities.ActivityExecutionContext.Current.Heartbeat();
                    await Task.WhenAny(_release.Task, Task.Delay(100, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
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
