using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Worker;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Compat;

/// <summary>
/// Replay-corpus CI gate for <see cref="DurableChatWorkflow"/>.
/// </summary>
/// <remarks>
/// <para>
/// These tests run in the <c>just test-unit-all</c> fast lane — no embedded Temporal
/// server required. <see cref="WorkflowReplayer"/> is a pure in-process unit-test
/// primitive that replays a captured event-history JSON against the current workflow code.
/// </para>
/// <para>
/// <b>How histories are generated:</b>
/// <c>HistoryCaptureTests.cs</c> (in the integration-test project) runs three workflows
/// against an embedded server, fetches the event history via <c>FetchHistoryAsync()</c>,
/// and saves JSON files under <c>tests/TemporalCommunity.Extensions.AI.Tests/Compat/Histories/</c>.
/// Those files are checked in and copied to the test output directory by the
/// <c>ItemGroup</c> in the test project's <c>.csproj</c>.
/// </para>
/// <para>
/// <b>How to update:</b>
/// If you change workflow command sequences (new activity type, new timer, reordered commands)
/// you MUST re-run the capture tests and commit the updated JSON, then verify these replay
/// tests pass.  Any wire-name rename that wasn't applied uniformly will cause a replay test
/// to fail here at CI time — that is the intended safety net.
/// </para>
/// <para>
/// <b>Adding histories:</b>
/// Add a new <c>Capture_*</c> test in <c>HistoryCaptureTests.cs</c>, re-run the capture
/// suite, commit the JSON, then add a corresponding <c>[Fact]</c> here.
/// </para>
/// </remarks>
public class WorkflowReplayTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Load a history JSON from the checked-in Histories directory (copied to output).
    /// </summary>
    private static WorkflowHistory LoadHistory(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Compat", "Histories", filename);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"History file not found: {path}. " +
                "Run HistoryCaptureTests in the integration project to regenerate it.",
                path);
        }
        var json = File.ReadAllText(path);
        return WorkflowHistory.FromJson(Path.GetFileNameWithoutExtension(filename), json);
    }

    /// <summary>
    /// Build a <see cref="WorkflowReplayer"/> wired with <see cref="DurableChatWorkflow"/> —
    /// the same workflow <c>AddDurableAI</c> registers on a production worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DurableAIDataConverter.Instance"/> is set on the replayer options so that
    /// polymorphic <c>AIContent</c> subtypes (e.g., <c>FunctionCallContent</c>,
    /// <c>FunctionResultContent</c>) in the workflow history payloads are deserialized
    /// correctly during replay.  Without it the workflow input cannot be decoded and
    /// the workflow exits immediately without scheduling any activities, causing
    /// false-positive nondeterminism errors on every history that has activity events.
    /// </para>
    /// </remarks>
    private static WorkflowReplayer BuildReplayer()
    {
        // A decode-capable reader must continue to replay histories written before encoding was
        // enabled. The checked-in corpus is intentionally uncompressed and exercises that path.
        var codec = new DurableAIGzipPayloadCodec(new DurableAIGzipPayloadCodecOptions());
        var opts = new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.CreateDataConverter(codec),
        };
        opts.AddWorkflow<DurableChatWorkflow>();
        opts.AddWorkflow<DurableChatClientWorkflow>();
        opts.AddWorkflow<TypedDurableTurnWorkflow>();
        return new WorkflowReplayer(opts);
    }

    // ── Happy-path replays ──────────────────────────────────────────────────

    /// <summary>
    /// The baseline no-tool history remains replayable.
    /// </summary>
    [Fact]
    public async Task Pattern1_SimpleTurn_ReplaysWithoutError()
    {
        var replayer = BuildReplayer();
        var history = LoadHistory("pattern-1-simple-turn.json");

        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    /// <summary>
    /// A Pattern-3 history (one tool call: GetChatStep → InvokeFunction → GetChatStep final)
    /// replays cleanly. This validates the durable tool dispatch loop wire-names:
    /// <c>TemporalCommunity.Extensions.AI.GetChatStep</c> and
    /// <c>TemporalCommunity.Extensions.AI.InvokeFunction</c>.
    /// Any rename of these activity type strings will break replay on the checked-in history.
    /// </summary>
    [Fact]
    public async Task Pattern3_WithTool_ReplaysWithoutError()
    {
        var replayer = BuildReplayer();
        var history = LoadHistory("pattern-3-with-tool.json");

        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    /// <summary>
    /// The post-Continue-as-New history remains replayable.
    /// </summary>
    [Fact]
    public async Task CanTransition_PostCanHistory_ReplaysWithoutError()
    {
        var replayer = BuildReplayer();
        var history = LoadHistory("can-transition.json");

        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    /// <summary>
    /// The closing CAN run re-reduces after a turn completes against a pending reducer,
    /// records the patch, and carries that turn instead of the first stale snapshot.
    /// </summary>
    [Fact]
    public async Task CanDrainBeforeSnapshot_ReplaysWithoutError()
    {
        var history = LoadHistory("can-drain-before-snapshot-v1.json");
        AssertPatchMarker(history, "meai-can-drain-before-snapshot");
        var reducers = history.Events.Where(ev => ev.ActivityTaskScheduledEventAttributes?
            .ActivityType.Name == "TemporalCommunity.Extensions.AI.ReduceHistoryByKey").ToList();
        Assert.Equal(2, reducers.Count);
        var firstCompletion = Assert.Single(history.Events, ev =>
            ev.ActivityTaskCompletedEventAttributes?.ScheduledEventId == reducers[0].EventId);
        var admitted = Assert.Single(history.Events, ev =>
            ev.WorkflowExecutionUpdateAcceptedEventAttributes?.AcceptedRequest.Input.Args.Payloads_
                .Any(payload => payload.Data.ToStringUtf8().Contains("later-successful-turn",
                    StringComparison.Ordinal)) == true);
        var completed = Assert.Single(history.Events, ev =>
            ev.WorkflowExecutionUpdateCompletedEventAttributes?.AcceptedEventId == admitted.EventId);
        Assert.True(reducers[0].EventId < admitted.EventId);
        Assert.True(admitted.EventId < completed.EventId);
        Assert.True(completed.EventId < firstCompletion.EventId);
        Assert.True(firstCompletion.EventId < reducers[1].EventId);
        var transition = Assert.Single(history.Events,
            ev => ev.WorkflowExecutionContinuedAsNewEventAttributes is not null);
        var input = Assert.IsType<DurableChatWorkflowInput>(
            DurableAIDataConverter.Instance.PayloadConverter.ToValue(
                Assert.Single(transition.WorkflowExecutionContinuedAsNewEventAttributes.Input.Payloads_),
                typeof(DurableChatWorkflowInput)));
        var carried = input.CarriedHistory!;
        Assert.Equal(2, carried.Count);
        Assert.IsType<DurableSessionRequest>(carried[0]);
        Assert.IsType<DurableSessionResponse>(carried[1]);
        Assert.All(carried, entry => Assert.Equal("later-successful-turn", entry.CorrelationId));
        Assert.Equal("turn 3", Assert.Single(carried[0].Messages).Text);
        Assert.Equal("Response: turn 3", Assert.Single(carried[1].Messages).Text);

        var result = await BuildReplayer().ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    /// <summary>The shutdown patch waits for an admitted Update before completing.</summary>
    [Fact]
    public async Task ShutdownDrainHandlers_ReplaysWithoutError()
    {
        var history = LoadHistory("shutdown-drain-handlers-v1.json");
        AssertPatchMarker(history, "meai-shutdown-drain-handlers");
        var admitted = Assert.Single(history.Events,
            ev => ev.WorkflowExecutionUpdateAcceptedEventAttributes is not null);
        var signal = Assert.Single(history.Events,
            ev => ev.WorkflowExecutionSignaledEventAttributes is not null);
        var processed = history.Events.First(ev => ev.EventId > signal.EventId &&
            ev.WorkflowTaskCompletedEventAttributes is not null);
        var modelScheduled = Assert.Single(history.Events, ev => ev.ActivityTaskScheduledEventAttributes?
            .ActivityType.Name == "TemporalCommunity.Extensions.AI.GetChatStep");
        var modelCompletion = Assert.Single(history.Events,
            ev => ev.ActivityTaskCompletedEventAttributes?.ScheduledEventId == modelScheduled.EventId);
        var updateCompletion = Assert.Single(history.Events,
            ev => ev.WorkflowExecutionUpdateCompletedEventAttributes is not null);
        var workflowCompletion = Assert.Single(history.Events,
            ev => ev.WorkflowExecutionCompletedEventAttributes is not null);
        Assert.Equal("Shutdown", signal.WorkflowExecutionSignaledEventAttributes.SignalName);
        Assert.True(admitted.EventId < signal.EventId);
        Assert.True(processed.EventId < modelCompletion.EventId);
        Assert.True(modelCompletion.EventId < updateCompletion.EventId);
        Assert.True(updateCompletion.EventId < workflowCompletion.EventId);

        var result = await BuildReplayer().ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    private static void AssertPatchMarker(WorkflowHistory history, string patchId) =>
        Assert.Contains(history.Events, ev => ev.MarkerRecordedEventAttributes?.MarkerName == "core_patch" &&
            ev.MarkerRecordedEventAttributes.Details.Values.SelectMany(value => value.Payloads_)
                .Any(payload => payload.Data.ToStringUtf8().Contains(patchId, StringComparison.Ordinal)));

    [Fact]
    public async Task WorkerOwnedToolsetV1_ReplaysWithoutError()
    {
        var replayer = BuildReplayer();
        var history = LoadHistory("worker-owned-toolset-v1.json");

        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    /// <summary>
    /// The direct-adapter history captured before Temporal routing metadata was preserved
    /// remains replayable. This protects the workflow command sequence, not byte-for-byte
    /// equality of activity-input payloads created by later workflow executions.
    /// </summary>
    [Fact]
    public async Task DirectMiddlewareOptionsV1_ReplaysWithoutError()
    {
        var replayer = BuildReplayer();
        var history = LoadHistory("direct-middleware-options-v1.json");

        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    [Fact]
    public async Task TypedDurableTurnV1_ReplaysWithoutError()
    {
        var replayer = BuildReplayer();
        var history = LoadHistory("typed-durable-turn-v1.json");

        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    [Fact]
    public async Task TypedWorkerOwnedToolsetV1_ReplaysWithoutError()
    {
        var replayer = BuildReplayer();
        var history = LoadHistory("typed-worker-owned-toolset-v1.json");

        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    /// <summary>
    /// A 0.12.0 typed-turn history that persisted the complete function protocol after an
    /// iteration-limit result remains replayable. New executions use a patch marker to persist
    /// only the sentinel, while replay of this pre-marker history retains the old command path.
    /// </summary>
    [Fact]
    public async Task TypedIterationLimitV0_12_0_ReplaysWithoutError()
    {
        var replayer = BuildReplayer();
        var history = LoadHistory("typed-iteration-limit-v0_12_0.json");

        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    // ── Negative test: proves the harness catches nondeterminism ─────────────

    /// <summary>
    /// A hand-edited history with a spurious extra <c>ACTIVITY_TASK_SCHEDULED</c>
    /// event injected after activity completion causes a
    /// <see cref="WorkflowNondeterminismException"/> during replay.
    /// </summary>
    /// <remarks>
    /// This is the critical negative case: it proves that the replay harness actually
    /// catches determinism breaks and does NOT run silently. Without this test, a
    /// misconfigured replayer could green-light all histories vacuously.
    /// </remarks>
    [Fact]
    public async Task NondeterministicHistory_ThrowsWorkflowNondeterminismException()
    {
        var replayer = BuildReplayer();
        var history = LoadHistory("pattern-1-nondeterminism.json");

        // throwOnReplayFailure: true → throws WorkflowNondeterminismException
        await Assert.ThrowsAnyAsync<WorkflowNondeterminismException>(
            () => replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: true));
    }

    /// <summary>
    /// The same nondeterministic history — when replayed with <c>throwOnReplayFailure: false</c> —
    /// returns a <see cref="WorkflowReplayResult"/> whose
    /// <see cref="WorkflowReplayResult.ReplayFailure"/> is a
    /// <see cref="WorkflowNondeterminismException"/>, not null.
    /// </summary>
    [Fact]
    public async Task NondeterministicHistory_ReplayResultCarriesException()
    {
        var replayer = BuildReplayer();
        var history = LoadHistory("pattern-1-nondeterminism.json");

        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.NotNull(result.ReplayFailure);
        Assert.IsType<WorkflowNondeterminismException>(result.ReplayFailure);
    }
}
