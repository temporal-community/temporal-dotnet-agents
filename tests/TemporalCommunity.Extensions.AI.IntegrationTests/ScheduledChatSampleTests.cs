using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScheduledChat;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public sealed class ScheduledChatSampleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task RunnableSample_PrintsTwoTurns_AndDeletesItsSchedule()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(
            $"../../../../../samples/MEAI/ScheduledChat/bin/{configuration}/net10.0/ScheduledChat.dll",
            AppContext.BaseDirectory));
        process.StartInfo.Environment["TEMPORAL_ADDRESS"] =
            environment.Client.Connection.Options.TargetHost;
        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(Deadline);
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(process.ExitCode == 0, $"Exit {process.ExitCode}:\n{output}\n{error}");
            Assert.Contains("Scheduled turns retained: 2", output);
            Assert.Equal(2, output.Split('\n').Count(line => line.StartsWith("Occurrence ")));
            var schedules = new List<string>();
            await foreach (var schedule in environment.Client.ListSchedulesAsync())
                schedules.Add(schedule.Id);
            Assert.Empty(schedules);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public void OccurrenceIdentity_NormalizesUtc_AndDistinguishesScheduleAndTime()
    {
        var time = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var first = new ScheduledTurnOccurrence("opaque-schedule", time, "session", "fixed prompt");
        Assert.Equal(first.RequestId,
            (first with { ScheduledAt = time.ToOffset(TimeSpan.FromHours(-5)) }).RequestId);
        Assert.NotEqual(first.RequestId, (first with { ScheduledAt = time.AddHours(1) }).RequestId);
        Assert.NotEqual(first.RequestId, (first with { ScheduleId = "other-schedule" }).RequestId);
    }

    [Theory]
    [InlineData(ScheduleOverlapPolicy.Skip, 1)]
    [InlineData(ScheduleOverlapPolicy.BufferOne, 2)]
    public async Task ScheduleOverlap_TracksWholeTurn_NotOnlySubmission(
        ScheduleOverlapPolicy overlap, int expectedTurns)
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new RecordingModel(blockFirst: true);
        var queue = $"schedule-overlap-{Guid.NewGuid():N}";
        using var host = BuildHost(environment, queue, model);
        await host.StartAsync();
        var sessions = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversation = $"conversation-{Guid.NewGuid():N}";
        var schedule = await CreateScheduleAsync(environment.Client, queue, conversation, overlap);
        try
        {
            await schedule.TriggerAsync();
            await model.Entered.Task.WaitAsync(Deadline);
            await WaitForDescriptionAsync(schedule, description =>
                description.Info.NumActions == 1 && description.Info.RunningActions.Count == 1);
            // Manual triggers use second-resolution nominal times on the pinned server.
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            await schedule.TriggerAsync();
            await schedule.TriggerAsync();

            if (overlap == ScheduleOverlapPolicy.BufferOne)
                await WaitForBufferAsync(environment.Client, schedule);
            var duringTurn = await WaitForDescriptionAsync(schedule, description =>
                overlap == ScheduleOverlapPolicy.BufferOne
                    || description.Info.NumActionsSkippedOverlap >= 2);
            Assert.Equal(1, duringTurn.Info.NumActions);
            Assert.Equal(1, model.Calls);
            model.Release.TrySetResult();

            var finished = await WaitForDescriptionAsync(schedule, description =>
                description.Info.NumActions == expectedTurns
                && description.Info.RunningActions.Count == 0);
            var receipts = await GetReceiptsAndReplayAsync(environment.Client, finished);
            Assert.Equal(expectedTurns, receipts.Count);
            Assert.Equal(expectedTurns, receipts.Select(receipt => receipt.RequestId).Distinct().Count());
            Assert.Equal(expectedTurns, model.Calls);
            var history = await sessions.GetHistoryAsync(conversation).WaitAsync(Deadline);
            Assert.Equal(expectedTurns, history.OfType<DurableSessionResponse>().Count());
            await ShutdownAndReplaySessionAsync(environment.Client, sessions, conversation);
        }
        finally
        {
            model.Release.TrySetResult();
            await schedule.DeleteAsync();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Backfill_UsesNominalOccurrenceIdentity_AndLostAcknowledgmentRetryDeduplicates()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new RecordingModel();
        var queue = $"schedule-backfill-{Guid.NewGuid():N}";
        using var host = BuildHost(environment, queue, model, loseFirstAcknowledgment: true);
        await host.StartAsync();
        var sessions = host.Services.GetRequiredService<DurableChatSessionClient>();
        var attempts = host.Services.GetRequiredService<SubmissionAttempts>();
        var conversation = $"conversation-{Guid.NewGuid():N}";
        var schedule = await CreateScheduleAsync(
            environment.Client, queue, conversation, ScheduleOverlapPolicy.Skip);
        var now = DateTime.UtcNow;
        var end = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var backfill = new ScheduleBackfill(
            end.AddHours(-2).AddSeconds(1), end, ScheduleOverlapPolicy.BufferAll);
        try
        {
            // Explicit override serializes both historical occurrences, even while paused.
            await schedule.BackfillAsync([backfill]);
            var finished = await WaitForDescriptionAsync(schedule, description =>
                description.Info.NumActions == 2 && description.Info.RunningActions.Count == 0);
            Assert.True(finished.Schedule.State.Paused);
            var receipts = await GetReceiptsAndReplayAsync(environment.Client, finished);
            Assert.Equal(2, receipts.Count);
            Assert.Equal(2, model.Calls);
            Assert.Equal(2, receipts.Select(receipt => receipt.RequestId).Distinct().Count());
            Assert.Equal(2, receipts.Select(receipt => receipt.Text).Distinct().Count());
            foreach (var action in finished.Info.RecentActions)
            {
                var expected = new ScheduledTurnOccurrence(
                    schedule.Id, action.ScheduledAt, conversation, "Report the service status.");
                Assert.Contains(receipts, receipt => receipt.RequestId == expected.RequestId);
            }

            var retryGroup = Assert.Single(attempts.Completed.GroupBy(attempt => attempt.Receipt.RequestId),
                group => group.Count() == 2);
            Assert.Equal([1, 2], retryGroup.Select(attempt => attempt.Attempt).Order().ToArray());
            Assert.Single(retryGroup.Select(attempt => attempt.Receipt.Text).Distinct());
            Assert.Equal(3, attempts.Completed.Count);

            // Replaying the same nominal time range must not run inference again.
            await schedule.BackfillAsync([backfill]);
            var repeated = await WaitForDescriptionAsync(schedule, description =>
                description.Info.NumActions == 4 && description.Info.RunningActions.Count == 0);
            var repeatedReceipts = await GetReceiptsAndReplayAsync(environment.Client, repeated);
            Assert.Equal(4, repeatedReceipts.Count);
            Assert.Equal(2, repeatedReceipts.Select(receipt => receipt.RequestId).Distinct().Count());
            Assert.Equal(2, repeatedReceipts.Select(receipt => receipt.Text).Distinct().Count());
            Assert.Equal(2, model.Calls);
            var retained = await sessions.GetHistoryAsync(conversation).WaitAsync(Deadline);
            Assert.Equal(2, retained.OfType<DurableSessionRequest>().Count());
            Assert.Equal(2, retained.OfType<DurableSessionResponse>().Count());
            await ShutdownAndReplaySessionAsync(environment.Client, sessions, conversation);
        }
        finally
        {
            await schedule.DeleteAsync();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ManualWorkflowStart_RejectsMissingOccurrenceBeforeSubmittingTurn()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new RecordingModel();
        var queue = $"schedule-missing-{Guid.NewGuid():N}";
        using var host = BuildHost(environment, queue, model);
        await host.StartAsync();
        try
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (ScheduledTurnWorkflow workflow) => workflow.RunAsync(
                    new ScheduledTurnInput("unused", "Report the service status.")),
                new WorkflowOptions($"missing-{Guid.NewGuid():N}", queue));
            var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
                () => handle.GetResultAsync().WaitAsync(Deadline));
            Assert.Equal("MissingScheduleOccurrence",
                Assert.IsType<ApplicationFailureException>(failure.InnerException).ErrorType);
            Assert.Equal(0, model.Calls);
            Assert.Empty(host.Services.GetRequiredService<SubmissionAttempts>().Completed);
            await ReplayTriggerAsync(handle);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHost BuildHost(
        WorkflowEnvironment environment, string queue, RecordingModel model,
        bool loseFirstAcknowledgment = false)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(environment.Client);
        builder.Services.AddSingleton<IChatClient>(model);
        builder.Services.AddSingleton<ScheduledTurnActivities>();
        builder.Services.AddSingleton(provider => new SubmissionAttempts(
            provider.GetRequiredService<ScheduledTurnActivities>(), loseFirstAcknowledgment));
        builder.Services.AddHostedTemporalWorker(queue)
            .AddWorkflow<ScheduledTurnWorkflow>()
            .AddSingletonActivities<SubmissionAttempts>()
            .AddDurableAI(options => options.SessionTimeToLive = TimeSpan.FromMinutes(10));
        return builder.Build();
    }

    private static Task<ScheduleHandle> CreateScheduleAsync(
        ITemporalClient client, string queue, string conversation, ScheduleOverlapPolicy overlap)
    {
        var id = $"chat-schedule-{Guid.NewGuid():N}";
        return client.CreateScheduleAsync(id, new Schedule(
            ScheduleActionStartWorkflow.Create(
                (ScheduledTurnWorkflow workflow) => workflow.RunAsync(
                    new ScheduledTurnInput(conversation, "Report the service status.")),
                new WorkflowOptions($"trigger-{id}", queue)),
            new ScheduleSpec { Intervals = [new ScheduleIntervalSpec(TimeSpan.FromHours(1))] })
        {
            Policy = new SchedulePolicy { Overlap = overlap, PauseOnFailure = true },
            State = new ScheduleState { Paused = true },
        });
    }

    private static async Task<ScheduleDescription> WaitForDescriptionAsync(
        ScheduleHandle schedule, Func<ScheduleDescription, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        ScheduleDescription? last = null;
        try
        {
            while (true)
            {
                var description = await schedule.DescribeAsync(
                    new RpcOptions { CancellationToken = timeout.Token });
                last = description;
                if (predicate(description))
                    return description;
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException(
                $"Schedule wait expired: actions={last?.Info.NumActions}, " +
                $"running={last?.Info.RunningActions.Count}, skipped={last?.Info.NumActionsSkippedOverlap}, " +
                $"paused={last?.Schedule.State.Paused}, note={last?.Schedule.State.Note}.");
        }
    }

    private static async Task WaitForBufferAsync(ITemporalClient client, ScheduleHandle schedule)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (true)
        {
            var description = await client.WorkflowService.DescribeScheduleAsync(
                new() { Namespace = client.Options.Namespace, ScheduleId = schedule.Id },
                new RpcOptions { CancellationToken = timeout.Token });
            if (description.Info.BufferSize == 1 && description.Info.OverlapSkipped >= 1)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async Task<List<ScheduledTurnReceipt>> GetReceiptsAndReplayAsync(
        ITemporalClient client, ScheduleDescription description)
    {
        var receipts = new List<ScheduledTurnReceipt>();
        foreach (var action in description.Info.RecentActions)
        {
            var execution = Assert.IsType<ScheduleActionExecutionStartWorkflow>(action.Action);
            var handle = client.GetWorkflowHandle<ScheduledTurnWorkflow, ScheduledTurnReceipt>(
                execution.WorkflowId, execution.FirstExecutionRunId);
            receipts.Add(await handle.GetResultAsync().WaitAsync(Deadline));
            await ReplayTriggerAsync(handle);
        }
        return receipts;
    }

    private static async Task ReplayTriggerAsync(WorkflowHandle<ScheduledTurnWorkflow> handle)
    {
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        }.AddWorkflow<ScheduledTurnWorkflow>());
        Assert.Null((await replayer.ReplayWorkflowAsync(
            await handle.FetchHistoryAsync(), throwOnReplayFailure: false)).ReplayFailure);
    }

    private static async Task ShutdownAndReplaySessionAsync(
        ITemporalClient client, DurableChatSessionClient sessions, string conversation)
    {
        await sessions.ShutdownAsync(conversation).WaitAsync(Deadline);
        var handle = client.GetWorkflowHandle<DurableChatWorkflow>(sessions.GetWorkflowId(conversation));
        await handle.GetResultAsync().WaitAsync(Deadline);
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        }.AddWorkflow<DurableChatWorkflow>());
        Assert.Null((await replayer.ReplayWorkflowAsync(
            await handle.FetchHistoryAsync(), throwOnReplayFailure: false)).ReplayFailure);
    }

    public sealed class SubmissionAttempts(ScheduledTurnActivities activities, bool loseFirstAcknowledgment)
    {
        private int _acknowledgmentLost;
        public ConcurrentQueue<(int Attempt, ScheduledTurnReceipt Receipt)> Completed { get; } = new();

        [Activity("ScheduledChat.SubmitTurn")]
        public async Task<ScheduledTurnReceipt> SubmitAsync(ScheduledTurnOccurrence occurrence)
        {
            var receipt = await activities.SubmitAsync(occurrence);
            Completed.Enqueue((ActivityExecutionContext.Current.Info.Attempt, receipt));
            if (loseFirstAcknowledgment && Interlocked.Exchange(ref _acknowledgmentLost, 1) == 0)
                throw new ApplicationFailureException("Simulated lost submission acknowledgment.");
            return receipt;
        }
    }

    private sealed class RecordingModel(bool blockFirst = false) : IChatClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            if (blockFirst && call == 1)
                await Release.Task.WaitAsync(cancellationToken);
            yield return new(ChatRole.Assistant, $"Service check {call}: all services are operational.")
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
