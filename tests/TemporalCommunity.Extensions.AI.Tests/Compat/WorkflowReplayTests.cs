#pragma warning disable TAI001 // DurableAIPlugin is [Experimental("TAI001")]; deliberate use in tests

using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Worker;
using TemporalCommunity.Extensions.AI;
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
    /// Build a <see cref="WorkflowReplayer"/> wired with <see cref="DurableChatWorkflow"/>
    /// via the same plugin hook that production workers use.
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
        var opts = new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        };
        var plugin = new DurableAIPlugin();
        plugin.ConfigureReplayer(opts);
        return new WorkflowReplayer(opts);
    }

    // ── Happy-path replays ──────────────────────────────────────────────────

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
