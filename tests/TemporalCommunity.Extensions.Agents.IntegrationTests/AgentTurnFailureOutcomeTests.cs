using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Client;
using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker.Interceptors;
using Temporalio.Workflows;
using Xunit.Abstractions;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AgentTurnFailureOutcomeTests
{
    private readonly ITestOutputHelper _output;

    public AgentTurnFailureOutcomeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task FailedToolTurnOutcomeResponse_RemainsNullAcrossContinueAsNew()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var model = new ToolThenFailureClient(
            [new FunctionCallContent("response-can-call", "write", new Dictionary<string, object?>())]);
        var tool = AIFunctionFactory.Create(() => "written", "write");
        var taskQueue = $"outcome-response-can-repro-{Guid.NewGuid():N}";
        const string reducerKey = "outcome-response-can-repro";
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> retainLatestTurn = history =>
        {
            var interrupted = history.OfType<DurableSessionResponse>()
                .First(response => response.CompletionReason == DurableTurnCompletionReason.Interrupted);
            var latestRequest = history.OfType<DurableSessionRequest>().Last();
            return history.Where(entry =>
                entry.CorrelationId == interrupted.CorrelationId
                || entry.CorrelationId == latestRequest.CorrelationId).ToList();
        };
        using var host = BuildHost(
            environment.Client, taskQueue, model, new TransactionalProvider(), tool,
            historyReducerKey: reducerKey, historyReducer: retainLatestTurn);
        await host.StartAsync();
        var sessionId = TemporalAgentSessionId.WithRandomKey("FailureAgent");
        var input = CreateInput(taskQueue, 1024 * 1024) with
        {
            MaxEntryCount = 6,
            HistoryReducerKey = reducerKey,
        };
        await environment.Client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(sessionId.WorkflowId, taskQueue));
        var handle = environment.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        const string requestId = "failed-with-tool-response";
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            await Assert.ThrowsAsync<DurableTurnFailedException>(() =>
                proxy.RunAsync(
                    "dispatch then fail",
                    new TemporalAgentSession(sessionId),
                    new TemporalAgentRunOptions { RequestId = requestId }));

            var before = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetTurnOutcomeAsync(sessionId, requestId);
            var terminalBefore = await handle.QueryAsync(wf => wf.GetTerminalTurnOutcome(requestId));
            Assert.Equal(TemporalAgentTurnOutcomeStatus.Failed, before.Status);
            Assert.False(before.DetailsOmitted, "The default 1 MiB record unexpectedly compacted.");
            Assert.Null(before.Response);
            Assert.Null(terminalBefore?.Response);

            var originalRunId = (await handle.DescribeAsync()).RunId;
            var session = new TemporalAgentSession(sessionId);
            await proxy.RunAsync("rollover one", session);
            await proxy.RunAsync("rollover two", session);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((await handle.DescribeAsync()).RunId == originalRunId && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            var currentDescription = await handle.DescribeAsync();
            Assert.True(
                originalRunId != currentDescription.RunId,
                $"Continue-as-new did not start. Status={currentDescription.Status}, " +
                $"History={(await host.Services.GetRequiredService<ITemporalAgentClient>().GetHistoryBudgetAsync(sessionId)).RetainedHistoryEntryCount}.");

            var after = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetTurnOutcomeAsync(sessionId, requestId);
            var terminalAfter = await handle.QueryAsync(wf => wf.GetTerminalTurnOutcome(requestId));

            // This is the external-review assertion: it fails if CAN restores the interrupted entry.
            Assert.Null(after.Response);
            Assert.Null(terminalAfter?.Response);
            Assert.False(after.DetailsOmitted, "The default 1 MiB record compacted after CAN.");
        }
        finally
        {
            try { await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()); }
            catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound) { }
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(1024 * 1024, 0)]
    [InlineData(4096, 12_000)]
    public async Task SuccessfulToolTurnOutcome_RetainsToolCallSummaries(
        int ledgerBytes,
        int finalResponseLength)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        const string callId = "successful-tool-call";
        var model = new ToolThenSuccessClient(
            [new FunctionCallContent(callId, "write", new Dictionary<string, object?>())],
            new string('x', finalResponseLength));
        var tool = AIFunctionFactory.Create(() => "written", "write");
        var taskQueue = $"outcome-success-tool-repro-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, taskQueue, model, new TransactionalProvider(), tool);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            var sessionId = TemporalAgentSessionId.WithRandomKey("FailureAgent");
            await environment.Client.StartWorkflowAsync(
                (AgentWorkflow wf) => wf.RunAsync(CreateInput(taskQueue, ledgerBytes)),
                new WorkflowOptions(sessionId.WorkflowId, taskQueue));
            const string requestId = "successful-tool-outcome";
            await proxy.RunAsync(
                "run the tool",
                new TemporalAgentSession(sessionId),
                new TemporalAgentRunOptions { RequestId = requestId });

            var outcome = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetTurnOutcomeAsync(sessionId, requestId);

            Assert.Equal(new DurableToolCallOutcomeCounts(1, 0, 0), outcome.ToolCallCounts);
            Assert.Contains(outcome.ToolCallSummaries ?? [],
                summary => summary.CallId == callId
                    && summary.Status == DurableToolCallOutcomeStatus.Completed);
            if (ledgerBytes > 4096)
                Assert.False(outcome.DetailsOmitted);
            else
                Assert.True(outcome.DetailsOmitted);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task RestoredTerminalOutcome_CompactionPreservesToolCallSummaries()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var taskQueue = $"outcome-carried-compaction-repro-{Guid.NewGuid():N}";
        const string callId = "carried-completed-call";
        var carried = new DurableTerminalTurnOutcome
        {
            RequestId = "carried-failure",
            AdmissionFingerprint = new string('f', 64),
            Status = "failed",
            Failure = new DurableTerminalTurnFailure
            {
                ErrorType = "PriorFailure",
                Message = new string('m', 5000),
                NonRetryable = false,
            },
            ToolCallSummaries =
            [
                new DurableToolCallOutcomeSummary(
                    callId, "write", DurableToolCallOutcomeStatus.Completed),
            ],
            ToolCallCounts = new DurableToolCallOutcomeCounts(1, 0, 0),
        };
        var input = CreateInput(taskQueue, 4096) with
        {
            CarriedTerminalOutcomes = [carried],
        };
        using var host = BuildHost(
            environment.Client, taskQueue, new ToolThenSuccessClient([], "idle"),
            new TransactionalProvider(), AIFunctionFactory.Create(() => "unused", "write"));
        await host.StartAsync();
        var sessionId = TemporalAgentSessionId.WithRandomKey("FailureAgent");
        try
        {
            await environment.Client.StartWorkflowAsync(
                (AgentWorkflow wf) => wf.RunAsync(input),
                new WorkflowOptions(sessionId.WorkflowId, taskQueue));
            var outcome = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetTurnOutcomeAsync(sessionId, carried.RequestId);

            Assert.True(outcome.DetailsOmitted, "The oversized carried record was not compacted.");
            Assert.Equal(new DurableToolCallOutcomeCounts(1, 0, 0), outcome.ToolCallCounts);
            Assert.Contains(outcome.ToolCallSummaries ?? [],
                summary => summary.CallId == callId);
        }
        finally
        {
            try
            {
                await environment.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId)
                    .ExecuteUpdateAsync(wf => wf.RequestShutdownAsync());
            }
            catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound) { }
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task RestoredCompactedFailure_DoesNotExposePlaceholderClassification()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var taskQueue = $"outcome-carried-placeholder-{Guid.NewGuid():N}";
        const string requestId = "carried-compacted-failure";
        var carried = new DurableTerminalTurnOutcome
        {
            RequestId = requestId,
            AdmissionFingerprint = new string('f', 64),
            Status = "failed",
            OutcomeOmitted = true,
            Failure = new DurableTerminalTurnFailure
            {
                ErrorType = "PriorFailure",
                Message = string.Empty,
                NonRetryable = true,
            },
        };
        var input = CreateInput(taskQueue, 4096) with
        {
            CarriedTerminalOutcomes = [carried],
        };
        using var host = BuildHost(
            environment.Client, taskQueue, new ToolThenSuccessClient([], "idle"),
            new TransactionalProvider(), AIFunctionFactory.Create(() => "unused", "write"));
        await host.StartAsync();
        var sessionId = TemporalAgentSessionId.WithRandomKey("FailureAgent");
        try
        {
            await environment.Client.StartWorkflowAsync(
                (AgentWorkflow wf) => wf.RunAsync(input),
                new WorkflowOptions(sessionId.WorkflowId, taskQueue));

            var outcome = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetTurnOutcomeAsync(sessionId, requestId);

            Assert.Equal(TemporalAgentTurnOutcomeStatus.Failed, outcome.Status);
            Assert.True(outcome.DetailsOmitted);
            Assert.Equal("PriorFailure", outcome.FailureErrorType);
            Assert.Null(outcome.FailureMessage);
            Assert.Null(outcome.FailureNonRetryable);
        }
        finally
        {
            try
            {
                await environment.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId)
                    .ExecuteUpdateAsync(wf => wf.RequestShutdownAsync());
            }
            catch (RpcException) { }
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task MissingWorkflowQuery_ReturnsTypedNotFoundFailure()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        var workflow = environment.Client.GetWorkflowHandle<AgentWorkflow>(
            TemporalAgentSessionId.WithRandomKey("MissingAgent").WorkflowId);
        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            workflow.QueryAsync(wf => wf.GetTurnOutcome("unknown-request")));

        Assert.Equal(RpcException.StatusCode.NotFound, exception.Code);
        var status = Assert.IsType<Temporalio.Api.Common.V1.GrpcStatus>(exception.GrpcStatus.Value);
        var detail = Assert.Single(status.Details);
        Assert.True(
            detail.Is(Temporalio.Api.ErrorDetails.V1.NotFoundFailure.Descriptor),
            $"Unexpected NotFound detail: {detail.TypeUrl}");
    }

    [Fact]
    public async Task TurnOutcomeQuery_ReportsPendingWithoutSubmittingAnotherTurn()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var enteredTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ToolThenFailureClient(
            [new FunctionCallContent("pending-call", "write", new Dictionary<string, object?>())]);
        var tool = AIFunctionFactory.Create(async (CancellationToken cancellationToken) =>
        {
            enteredTool.TrySetResult();
            await releaseTool.Task.WaitAsync(cancellationToken);
            return "finished";
        }, "write");
        var taskQueue = $"pending-outcome-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, taskQueue, model, new TransactionalProvider(), tool);
        await host.StartAsync();
        var sessionId = TemporalAgentSessionId.WithRandomKey("FailureAgent");
        var workflow = await environment.Client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(CreateInput(taskQueue, 1048576)),
            new WorkflowOptions(sessionId.WorkflowId, taskQueue));
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            var session = new TemporalAgentSession(sessionId);
            var run = proxy.RunAsync(
                "write then fail",
                session,
                new TemporalAgentRunOptions { RequestId = "pending-request" });
            await enteredTool.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var outcome = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetTurnOutcomeAsync(sessionId, "pending-request");

            Assert.Equal(TemporalAgentTurnOutcomeStatus.Pending, outcome.Status);
            Assert.Equal(1, model.CallCount);
            var missingWorkflowOutcome = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetTurnOutcomeAsync(
                    TemporalAgentSessionId.WithRandomKey("MissingAgent"),
                    "unknown-request");
            Assert.Equal(TemporalAgentTurnOutcomeStatus.Unavailable, missingWorkflowOutcome.Status);
            var oversizedRequestId = new string('r', 257);
            var missingOversizedOutcome = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetTurnOutcomeAsync(
                    TemporalAgentSessionId.WithRandomKey("MissingAgent"),
                    oversizedRequestId);
            Assert.Equal(TemporalAgentTurnOutcomeStatus.Unavailable, missingOversizedOutcome.Status);
            var activeOversizedQuery = await Assert.ThrowsAsync<WorkflowQueryFailedException>(() =>
                host.Services.GetRequiredService<ITemporalAgentClient>()
                    .GetTurnOutcomeAsync(sessionId, oversizedRequestId));
            _output.WriteLine(
                $"257-char request ID: missing workflow => Unavailable; existing workflow => " +
                $"{activeOversizedQuery.GetType().Name}: {activeOversizedQuery.Message}");

            using var cancelledQuery = new CancellationTokenSource();
            cancelledQuery.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                host.Services.GetRequiredService<ITemporalAgentClient>()
                    .GetTurnOutcomeAsync(sessionId, "pending-request", cancelledQuery.Token));

            releaseTool.TrySetResult();
            await Assert.ThrowsAsync<DurableTurnFailedException>(() => run);
            Assert.Equal(2, model.CallCount);
        }
        finally
        {
            releaseTool.TrySetResult();
            await workflow.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync());
            await host.StopAsync();
        }
    }

    [Fact]
    public void Repro_SecondReceiptCreationThrowsIOException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-recovery-receipt-{Guid.NewGuid():N}.json");
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
            var exception = Assert.Throws<IOException>(() =>
                new FileStream(path, FileMode.CreateNew, FileAccess.Write));
            _output.WriteLine($"Second FileMode.CreateNew at the same receipt path: {exception.GetType().Name}");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Repro_TemporalAgentSessionToStringReturnsWorkflowId()
    {
        var session = new TemporalAgentSession(TemporalAgentSessionId.WithRandomKey("Agent"));
        Assert.Equal(session.SessionId.WorkflowId, session.ToString());
        _output.WriteLine($"TemporalAgentSession.ToString(): {session}");
    }

    [Fact]
    public async Task Repro_RecoveryDemoCancellationExceptionTypesAgainstDevServer()
    {
        if (Environment.GetEnvironmentVariable("TEMPORAL_DEV_SERVER_REPRO") != "1")
        {
            _output.WriteLine("Skipped prototype: set TEMPORAL_DEV_SERVER_REPRO=1 with localhost:7233 available.");
            return;
        }

        var client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions("localhost:7233")
            {
                DataConverter = TemporalAgentDataConverter.Instance,
            });
        foreach (var delayMs in new[] { 1, 50, 200 })
        {
            var taskQueue = $"outcome-cancel-probe-{delayMs}-{Guid.NewGuid():N}";
            var model = new DelayedChatClient(TimeSpan.FromSeconds(2));
            using var host = BuildHost(
                client, taskQueue, model, new TransactionalProvider(),
                AIFunctionFactory.Create(() => "unused", "write"));
            await host.StartAsync();
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var requestId = $"cancel-{delayMs}-{Guid.NewGuid():N}";
            using var timeout = new CancellationTokenSource(delayMs);
            string observed;
            try
            {
                await proxy.RunAsync(
                    "probe cancellation",
                    session,
                    new TemporalAgentRunOptions { RequestId = requestId },
                    timeout.Token);
                observed = "completed";
            }
            catch (Exception exception)
            {
                observed = exception.GetType().FullName ?? exception.GetType().Name;
            }
            _output.WriteLine($"{delayMs} ms cancellation: {observed}");

            try
            {
                await host.Services.GetRequiredService<ITemporalAgentClient>()
                    .ShutdownAsync(session.SessionId);
            }
            catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound) { }
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(1048576, 3, false, false, false)]
    [InlineData(1048576, 60, false, false, false)]
    [InlineData(4096, 3, false, false, false)]
    [InlineData(8192, 60, false, false, false)]
    [InlineData(4096, 24, true, true, true)]
    public async Task FailedTurn_RollsBackStateAndReplaysRetainedFullCompactOrMinimalOutcome(
        int ledgerBytes, int callCount, bool longCallIds, bool compact, bool minimal)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var calls = Enumerable.Range(0, callCount).Select(index =>
            new FunctionCallContent(
                longCallIds ? $"{index:D3}-{new string('c', 250)}" : $"call-{index:D3}",
                "write",
                new Dictionary<string, object?>())).ToArray();
        var model = new ToolThenFailureClient(calls);
        var provider = new TransactionalProvider();
        var effects = 0;
        var tool = AIFunctionFactory.Create(() =>
        {
            Interlocked.Increment(ref effects);
            return new string('r', 8192);
        }, "write");
        var taskQueue = $"failure-outcome-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, taskQueue, model, provider, tool);
        await host.StartAsync();
        var sessionId = TemporalAgentSessionId.WithRandomKey("FailureAgent");
        var workflow = await environment.Client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(CreateInput(taskQueue, ledgerBytes)),
            new WorkflowOptions(sessionId.WorkflowId, taskQueue));
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            var session = new TemporalAgentSession(sessionId);
            var options = new TemporalAgentRunOptions { RequestId = "failed-request" };
            var initial = await Assert.ThrowsAsync<DurableTurnFailedException>(
                () => proxy.RunAsync("write then fail", session, options));

            Assert.Equal(0, initial.CommittedRevision);
            var client = host.Services.GetRequiredService<ITemporalAgentClient>();
            var outcome = await client.GetTurnOutcomeAsync(sessionId, options.RequestId!);
            Assert.Equal(TemporalAgentTurnOutcomeStatus.Failed, outcome.Status);
            Assert.Equal(options.RequestId, outcome.RequestId);
            Assert.Equal(compact || callCount > 32, outcome.DetailsOmitted);
            Assert.Equal(initial.CommittedRevision, outcome.CommittedRevision);
            Assert.Equal(initial.ToolCallCounts, outcome.ToolCallCounts);
            Assert.Equal(minimal || initial.ToolCallSummariesTruncated, outcome.ToolCallSummariesTruncated);
            Assert.Equal(
                minimal ? [] : initial.ToolCallSummaries,
                outcome.ToolCallSummaries ?? []);
            var terminal = await workflow.QueryAsync(wf => wf.GetTerminalTurnOutcome(options.RequestId!));
            Assert.NotNull(terminal);
            Assert.Equal(outcome.FailureNonRetryable, terminal.FailureNonRetryable);
            if (!compact)
            {
                Assert.Equal(initial.ErrorType, outcome.FailureErrorType);
                Assert.NotNull(outcome.FailureNonRetryable);
            }
            else
            {
                Assert.Null(outcome.FailureNonRetryable);
                Assert.Null(outcome.FailureMessage);
                if (minimal)
                    Assert.Null(outcome.FailureErrorType);
                else
                    Assert.Equal(initial.ErrorType, outcome.FailureErrorType);
            }
            var unavailable = await client.GetTurnOutcomeAsync(sessionId, "not-yet-submitted");
            Assert.Equal(TemporalAgentTurnOutcomeStatus.Unavailable, unavailable.Status);
            Assert.Equal(callCount, effects);
            Assert.Equal(new DurableToolCallOutcomeCounts(callCount, 0, 0), initial.ToolCallCounts);
            Assert.All(initial.ToolCallSummaries,
                summary => Assert.Equal(DurableToolCallOutcomeStatus.Completed, summary.Status));
            Assert.Equal(calls.TakeLast(Math.Min(callCount, 32)).Select(call => call.CallId),
                initial.ToolCallSummaries.Select(summary => summary.CallId));
            Assert.Equal(callCount > 32, initial.ToolCallSummariesTruncated);
            Assert.Equal(Math.Max(0, callCount - 32), initial.OmittedToolCallCounts.Completed);
            Assert.Equal(0, provider.StoreCount);
            Assert.Equal(1, provider.FailureCount);

            if (compact)
            {
                var compactOutcome = await client.GetTurnOutcomeAsync(sessionId, options.RequestId!);
                Assert.Equal(TemporalAgentTurnOutcomeStatus.Failed, compactOutcome.Status);
                Assert.True(compactOutcome.DetailsOmitted);

                var replay = await Assert.ThrowsAsync<DurableTurnOutcomeTooLargeException>(
                    () => proxy.RunAsync("write then fail", session, options));
                Assert.Equal(minimal, replay.IsMinimal);
                Assert.Equal("failed", replay.Status);
                Assert.Equal(initial.CommittedRevision, replay.CommittedRevision);
                Assert.Equal(initial.ToolCallCounts, replay.ToolCallCounts);
                if (minimal)
                {
                    Assert.Empty(replay.ToolCallSummaries);
                    Assert.Equal(initial.ToolCallCounts, replay.OmittedToolCallCounts);
                }
                else
                {
                    Assert.Equal(initial.ToolCallSummaries, replay.ToolCallSummaries);
                    Assert.Equal(initial.OmittedToolCallCounts, replay.OmittedToolCallCounts);
                }
            }
            else
            {
                var replay = await Assert.ThrowsAsync<DurableTurnFailedException>(
                    () => proxy.RunAsync("write then fail", session, options));
                Assert.Equal(initial.ToolCallSummaries, replay.ToolCallSummaries);
                Assert.Equal(initial.CommittedRevision, replay.CommittedRevision);
            }

            Assert.Equal(callCount, effects);
            Assert.Equal(2, model.CallCount);
            Assert.Equal(1, provider.FailureCount);

            await proxy.RunAsync("next turn", session);
            Assert.False(provider.NextTurnSawFailedState);
            Assert.Equal(1, provider.StoreCount);
            Assert.Contains(model.LastMessages, message =>
                message.Contents.OfType<FunctionCallContent>().Any(call => call.CallId == calls[0].CallId));
            Assert.Contains(model.LastMessages, message =>
                message.Text?.Contains("Turn interrupted", StringComparison.Ordinal) == true);
        }
        finally
        {
            await workflow.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync());
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(1048576, false, false)]
    [InlineData(4096, false, false)]
    [InlineData(4096, true, true)]
    public async Task TimedOutTool_ReportsUncertainEffectAndSameRequestDoesNotRedispatch(
        int ledgerBytes, bool compact, bool minimal)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var calls = Enumerable.Range(0, compact ? 24 : 1)
            .Select(index => new FunctionCallContent(
                minimal ? $"{index:D3}-{new string('c', 250)}" : $"uncertain-call-{index:D3}",
                "write",
                new Dictionary<string, object?>())).ToArray();
        var model = new ToolThenFailureClient(calls);
        var effects = 0;
        var tool = AIFunctionFactory.Create(async (CancellationToken cancellationToken) =>
        {
            Interlocked.Increment(ref effects);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "not observed";
        }, "write");
        var taskQueue = $"uncertain-outcome-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, taskQueue, model, new TransactionalProvider(), tool);
        await host.StartAsync();
        var sessionId = TemporalAgentSessionId.WithRandomKey("FailureAgent");
        var workflow = await environment.Client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(CreateInput(taskQueue, ledgerBytes, true)),
            new WorkflowOptions(sessionId.WorkflowId, taskQueue));
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            var session = new TemporalAgentSession(sessionId);
            var options = new TemporalAgentRunOptions { RequestId = "uncertain-request" };
            var initial = await Assert.ThrowsAsync<DurableTurnFailedException>(
                () => proxy.RunAsync("write", session, options));
            var outcome = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetTurnOutcomeAsync(sessionId, options.RequestId!);
            Assert.Equal(TemporalAgentTurnOutcomeStatus.Failed, outcome.Status);
            Assert.Equal(initial.CommittedRevision, outcome.CommittedRevision);
            Assert.Equal(initial.ToolCallCounts, outcome.ToolCallCounts);
            Assert.Equal(compact, outcome.DetailsOmitted);
            Assert.Equal(minimal, outcome.ToolCallSummariesTruncated);
            Assert.Equal(minimal ? [] : initial.ToolCallSummaries, outcome.ToolCallSummaries ?? []);
            if (compact)
            {
                Assert.Null(outcome.FailureNonRetryable);
                var replay = await Assert.ThrowsAsync<DurableTurnOutcomeTooLargeException>(
                    () => proxy.RunAsync("write", session, options));
                Assert.Equal(minimal, replay.IsMinimal);
                Assert.Equal(outcome.ToolCallCounts, replay.ToolCallCounts);
            }
            else
            {
                Assert.NotNull(outcome.FailureNonRetryable);
                var replay = await Assert.ThrowsAsync<DurableTurnFailedException>(
                    () => proxy.RunAsync("write", session, options));
                Assert.Equal(initial.ToolCallSummaries, replay.ToolCallSummaries);
            }

            Assert.Equal(1, effects);
            Assert.Equal(1, model.CallCount);
            Assert.Contains(initial.ToolCallSummaries,
                call => call.Status == DurableToolCallOutcomeStatus.OutcomeUncertain);
            Assert.Equal(new DurableToolCallOutcomeCounts(0, calls.Length - 1, 1), outcome.ToolCallCounts);
        }
        finally
        {
            await workflow.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync());
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ClassifiedToolFailure_IsReportedAsFailedNotCompletedOrUncertain()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var model = new ToolThenFailureClient(
            [new FunctionCallContent("safe-failure", "write", new Dictionary<string, object?>())]);
        var attempts = 0;
        string FailSafely()
        {
            Interlocked.Increment(ref attempts);
            throw new TemporalCommunity.Extensions.Agents.Exceptions.DurableToolFailureException("Not performed.");
        }
        var tool = AIFunctionFactory.Create(FailSafely, "write");
        var taskQueue = $"classified-outcome-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, taskQueue, model, new TransactionalProvider(), tool);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            var session = await proxy.CreateSessionAsync();
            var options = new TemporalAgentRunOptions { RequestId = "classified-request" };
            var initial = await Assert.ThrowsAsync<DurableTurnFailedException>(
                () => proxy.RunAsync("fail safely then fail model", session, options));
            var replay = await Assert.ThrowsAsync<DurableTurnFailedException>(
                () => proxy.RunAsync("fail safely then fail model", session, options));

            Assert.Equal(1, attempts);
            Assert.Equal(new DurableToolCallOutcomeCounts(0, 1, 0), initial.ToolCallCounts);
            Assert.Equal(DurableToolCallOutcomeStatus.Failed, Assert.Single(initial.ToolCallSummaries).Status);
            Assert.Equal(initial.ToolCallSummaries, replay.ToolCallSummaries);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task PostHookStateFailure_IsContainedAndLaterProviderCommits()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var faulting = new WritingCompletionProvider("faulting", throws: true);
        var later = new WritingCompletionProvider("later", throws: false);
        var taskQueue = $"post-hook-staging-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, taskQueue, new ToolThenFailureClient([]),
            faulting, AIFunctionFactory.Create(() => "unused", "write"), later);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            var session = await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("complete", session);

            Assert.Equal("done", response.Text);
            Assert.False(session.StateBag.TryGetValue<string>("faulting", out _, JsonSerializerOptions.Default));
            Assert.True(session.StateBag.TryGetValue<string>("later", out var value, JsonSerializerOptions.Default));
            Assert.Equal("written", value);
            Assert.Equal(1, faulting.Attempts);
            Assert.Equal(1, later.Attempts);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task CompletionLostAfterPublication_RetriesHookAndDoesNotPromiseExactlyOnce()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var provider = new TransactionalProvider();
        var taskQueue = $"post-hook-retry-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, taskQueue, new ToolThenFailureClient([]),
            provider, AIFunctionFactory.Create(() => "unused", "write"),
            interceptor: new LostCompletionInterceptor());
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            const string requestId = "completed-request";
            var response = await proxy.RunAsync(
                "complete",
                session,
                new TemporalAgentRunOptions { RequestId = requestId });

            Assert.Equal("done", response.Text);
            var outcome = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetTurnOutcomeAsync(session.SessionId, requestId);
            Assert.Equal(TemporalAgentTurnOutcomeStatus.Succeeded, outcome.Status);
            Assert.NotNull(outcome.Response);
            Assert.False(outcome.DetailsOmitted);
            Assert.Equal(2, provider.StoreCount);
            Assert.Equal(0, provider.FailureCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOutcome_AcrossContinueAsNew_ReplaysWhileRetainedAndRunsAgainAfterEviction(bool evict)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var model = new ToolThenFailureClient(
            [new FunctionCallContent("rollover-call", "write", new Dictionary<string, object?>())]);
        var effects = 0;
        var tool = AIFunctionFactory.Create(() => Interlocked.Increment(ref effects), "write");
        var taskQueue = $"failure-rollover-{Guid.NewGuid():N}";
        const string historyReducerKey = "failure-outcome-retain-protected";
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> retainProtectedTurns = history =>
        {
            var interrupted = history.OfType<DurableSessionResponse>()
                .First(response => response.CompletionReason == DurableTurnCompletionReason.Interrupted);
            var latestRequest = history.OfType<DurableSessionRequest>().Last();
            return history.Where(entry =>
                entry.CorrelationId == interrupted.CorrelationId
                || entry.CorrelationId == latestRequest.CorrelationId).ToList();
        };
        using var host = BuildHost(
            environment.Client, taskQueue, model, new TransactionalProvider(), tool,
            historyReducerKey: historyReducerKey, historyReducer: retainProtectedTurns);
        await host.StartAsync();
        var sessionId = TemporalAgentSessionId.WithRandomKey("FailureAgent");
        var input = CreateInput(taskQueue, 1048576) with
        {
            MaxEntryCount = 6,
            HistoryReducerKey = historyReducerKey,
            MaxTerminalOutcomeCount = evict ? 1 : 128,
        };
        await environment.Client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(sessionId.WorkflowId, taskQueue));
        var workflow = environment.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            var session = new TemporalAgentSession(sessionId);
            var options = new TemporalAgentRunOptions { RequestId = "rollover-request" };
            var initial = await Assert.ThrowsAsync<DurableTurnFailedException>(
                () => proxy.RunAsync("write then fail", session, options));
            var client = host.Services.GetRequiredService<ITemporalAgentClient>();
            var initialOutcome = await client.GetTurnOutcomeAsync(sessionId, options.RequestId!);
            Assert.Equal(TemporalAgentTurnOutcomeStatus.Failed, initialOutcome.Status);
            var originalRun = (await workflow.DescribeAsync()).RunId;
            await proxy.RunAsync("next turn", session);
            await proxy.RunAsync("another next turn", session);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((await workflow.DescribeAsync()).RunId == originalRun && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            var currentDescription = await workflow.DescribeAsync();
            var historyBudget = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetHistoryBudgetAsync(sessionId);
            Assert.True(
                originalRun != currentDescription.RunId,
                $"Continue-as-new did not start. Status={currentDescription.Status}, " +
                $"History={historyBudget.RetainedHistoryEntryCount}, " +
                $"Blocked={historyBudget.IsHistoryBlocked}, " +
                $"BlockReason={historyBudget.HistoryBlockReason}, " +
                $"LastOutcome={historyBudget.HistoryRecoveryLastOutcome}.");

            var callsBeforeRecovery = model.CallCount;
            if (evict)
            {
                var unavailable = await client.GetTurnOutcomeAsync(sessionId, options.RequestId!);
                Assert.Equal(TemporalAgentTurnOutcomeStatus.Unavailable, unavailable.Status);
                var response = await proxy.RunAsync("write then fail", session, options);
                Assert.Equal("recovered", response.Text);
                Assert.Equal(callsBeforeRecovery + 1, model.CallCount);
            }
            else
            {
                var retained = await client.GetTurnOutcomeAsync(sessionId, options.RequestId!);
                Assert.Equal(TemporalAgentTurnOutcomeStatus.Failed, retained.Status);
                var replay = await Assert.ThrowsAsync<DurableTurnFailedException>(
                    () => proxy.RunAsync("write then fail", session, options));
                Assert.Equal(initial.ToolCallSummaries, replay.ToolCallSummaries);
                Assert.Equal(callsBeforeRecovery, model.CallCount);
            }
            Assert.Equal(1, effects);
        }
        finally
        {
            await environment.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId)
                .ExecuteUpdateAsync(wf => wf.RequestShutdownAsync());
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderInvokingSeededTool_FailsBeforeAnyToolOrModelEffect(bool suppressFailure)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var effects = 0;
        var model = new ToolThenFailureClient([]);
        var provider = new InvokingToolProvider(suppressFailure);
        var tool = AIFunctionFactory.Create(() => Interlocked.Increment(ref effects), "write");
        var taskQueue = $"provider-inline-tool-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, taskQueue, model, provider, tool);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailureAgent");
            var session = await proxy.CreateSessionAsync();
            await Assert.ThrowsAsync<DurableTurnFailedException>(() => proxy.RunAsync("prepare", session));

            Assert.Equal(0, effects);
            Assert.Equal(0, model.CallCount);
            Assert.Equal(1, provider.Attempts);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static AgentWorkflowInput CreateInput(string taskQueue, int ledgerBytes, bool timeout = false) =>
        new()
        {
            AgentName = "FailureAgent",
            TaskQueue = taskQueue,
            EnableSearchAttributes = false,
            MaxTerminalOutcomeBytes = ledgerBytes,
            ActivityTimeout = TimeSpan.FromSeconds(15),
            RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
            ResolvedWorkerConfig = new AgentWorkerToolConfig
            {
                MaxToolCallsPerTurn = 20,
                ToolActivityOptions = new Dictionary<string, ActivityOptions>
                {
                    ["write"] = new()
                    {
                        StartToCloseTimeout = timeout ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(15),
                        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                    },
                },
            },
        };

    private static IHost BuildHost(
        ITemporalClient client, string taskQueue, IChatClient model, AIContextProvider provider, AIFunction tool,
        AIContextProvider? trailingProvider = null, IWorkerInterceptor? interceptor = null,
        string? historyReducerKey = null,
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? historyReducer = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton(model);
        if (historyReducerKey is not null && historyReducer is not null)
        {
            builder.Services.AddKeyedSingleton<
                Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
                    historyReducerKey, (_, _) => historyReducer);
        }
        builder.Services.AddHostedTemporalWorker(taskQueue)
            .ConfigureOptions(options => options.Interceptors = interceptor is null ? [] : [interceptor])
            .AddTemporalAgents(agents =>
            agents.Agent("FailureAgent", new ChatClientAgentOptions
            {
                AIContextProviders = trailingProvider is null ? [provider] : [provider, trailingProvider],
                ChatOptions = new ChatOptions { Tools = [tool] },
            }));
        return builder.Build();
    }

    private sealed class ToolThenFailureClient(IReadOnlyList<FunctionCallContent> calls) : IChatClient
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = messages.ToArray();
            if (CallCount == 2)
            {
                throw new ApplicationFailureException("Model failed after tool effects.", nonRetryable: true);
            }
            return Task.FromResult(new ChatResponse(CallCount == 1
                ? calls.Count == 0
                    ? new ChatMessage(ChatRole.Assistant, "done")
                    : new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList())
                : new ChatMessage(ChatRole.Assistant, "recovered")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }

        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }

    private sealed class ToolThenSuccessClient(
        IReadOnlyList<FunctionCallContent> calls,
        string finalText) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var message = CallCount == 1 && calls.Count > 0
                ? new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList())
                : new ChatMessage(ChatRole.Assistant, finalText);
            return Task.FromResult(new ChatResponse(message));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class DelayedChatClient(TimeSpan delay) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "delayed response"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class WritingCompletionProvider(string key, bool throws) : AIContextProvider
    {
        public int Attempts { get; private set; }
        protected override ValueTask InvokedCoreAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            Attempts++;
            context.Session!.StateBag.SetValue(key, "written", JsonSerializerOptions.Default);
            if (throws)
            {
                throw new InvalidOperationException("Failed after staged write.");
            }
            return default;
        }
    }

    private sealed class InvokingToolProvider(bool suppressFailure) : AIContextProvider
    {
        public int Attempts { get; private set; }
        protected override async ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            Attempts++;
            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(context.AIContext.Tools!));
            try
            {
                await function.InvokeAsync(new AIFunctionArguments(), cancellationToken);
            }
            catch (ApplicationFailureException) when (suppressFailure)
            {
            }
            return new AIContext();
        }
    }

    private sealed class LostCompletionInterceptor : IWorkerInterceptor
    {
        public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
            new LostCompletionInbound(nextInterceptor);
    }

    private sealed class LostCompletionInbound(ActivityInboundInterceptor next) : ActivityInboundInterceptor(next)
    {
        public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
        {
            var result = await base.ExecuteActivityAsync(input);
            var info = ActivityExecutionContext.Current.Info;
            if (info.ActivityType == "TemporalCommunity.Extensions.Agents.CompleteAgentTurn" && info.Attempt == 1)
            {
                throw new InvalidOperationException("Injected loss of completion after publication.");
            }
            return result;
        }
    }

    private sealed class TransactionalProvider : AIContextProvider
    {
        public int StoreCount { get; private set; }
        public int FailureCount { get; private set; }
        public bool NextTurnSawFailedState { get; private set; }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            if (context.AIContext.Messages?.LastOrDefault()?.Text == "next turn")
            {
                NextTurnSawFailedState = context.Session!.StateBag.TryGetValue<string>(
                    "failed-state", out _, JsonSerializerOptions.Default);
            }
            context.Session!.StateBag.SetValue("failed-state", "prepared", JsonSerializerOptions.Default);
            return new(new AIContext());
        }

        protected override async ValueTask InvokedCoreAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            if (context.InvokeException is not null)
            {
                FailureCount++;
            }
            context.Session!.StateBag.SetValue("failed-state", "completed-hook", JsonSerializerOptions.Default);
            await base.InvokedCoreAsync(context, cancellationToken);
        }

        protected override ValueTask StoreAIContextAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            StoreCount++;
            return default;
        }
    }
}
