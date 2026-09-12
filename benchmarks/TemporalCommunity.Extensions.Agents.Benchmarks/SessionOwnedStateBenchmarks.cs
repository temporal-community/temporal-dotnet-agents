using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.Benchmarks;

/// <summary>
/// Option D Phase 1e — the regression budget for moving conversation history and the StateBag off
/// <c>TemporalAIAgent</c> and onto <see cref="TemporalAgentSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both models are measured in the same binary against the same fixtures rather than across two
/// git revisions, so the comparison is not contaminated by machine, runtime, or dependency drift.
/// <see cref="LegacyAgentOwnedTurnLoop"/> reproduces the pre-change model exactly: a
/// <c>List&lt;DurableSessionEntry&gt;</c> field plus a carried <see cref="JsonElement"/> StateBag
/// that each LLM step replaces wholesale.
/// </para>
/// <para>
/// The measured work is the per-turn state bookkeeping only — no Temporal server, no model call.
/// That is deliberate: those dominate real wall-clock by orders of magnitude, and burying the
/// bookkeeping under them would make a genuine regression invisible.
/// </para>
/// <para>
/// The two arms are not free of each other's costs by accident. Session ownership pays for
/// deserializing the merged StateBag back into a typed <see cref="AgentSessionStateBag"/> on every
/// mutation, where the legacy model just reassigned a <see cref="JsonElement"/>. That is the price
/// of multi-session isolation and continue-as-new carry-forward; this benchmark is what keeps it
/// honest.
/// </para>
/// <para>
/// <strong>Tool write-backs: two different questions.</strong>
/// <see cref="TurnShape.ToolsHistorical"/> is the exact v0.3 comparison — tool StateBag write-backs
/// were always <see langword="null"/> on the sub-agent path, because
/// <c>InvokeAgentToolInput</c> carries no session ID and the tool activity therefore never
/// established a <c>TemporalAgentContext</c>. <see cref="TurnShape.ToolsProspective"/> populates
/// them instead; that is <em>not</em> a historical baseline but a forward cost model for what a
/// tool round would cost if that gap were closed. Read the ratios in
/// <see cref="TurnShape.ToolsHistorical"/> when asking "did this change regress v0.3?".
/// </para>
/// </remarks>
[MemoryDiagnoser]
[ShortRunJob(RuntimeMoniker.Net10_0)]
[JsonExporterAttribute.Full]
public class SessionOwnedStateBenchmarks
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private JsonElement _llmStepBag;
    private JsonElement?[] _toolWriteBacks = [];
    private bool _hasToolRound;
    private TemporalAgentSessionId _sessionId;
    private JsonElement _snapshotOfPopulatedSession;
    private TemporalAgentSession _populatedSession = null!;

    /// <summary>Gets or sets the number of completed turns the conversation accumulates.</summary>
    [Params(1, 10, 100)]
    public int TurnCount { get; set; }

    /// <summary>Gets or sets the tool-round shape for the turn. See the type-level remarks.</summary>
    [Params(TurnShape.NoTools, TurnShape.ToolsHistorical, TurnShape.ToolsProspective)]
    public TurnShape Shape { get; set; }

    private const int ToolsPerTurn = 4;

    [GlobalSetup]
    public void Setup()
    {
        _sessionId = new TemporalAgentSessionId("Bench", "key");

        var providerBag = new AgentSessionStateBag();
        providerBag.SetValue("app.context.files", new[] { "src/a.cs", "src/b.cs", "src/c.cs" });
        providerBag.SetValue("test.step_counter", "1");
        _llmStepBag = providerBag.Serialize();

        _hasToolRound = Shape != TurnShape.NoTools;
        _toolWriteBacks = new JsonElement?[ToolsPerTurn];
        if (Shape == TurnShape.ToolsProspective)
        {
            for (var i = 0; i < _toolWriteBacks.Length; i++)
            {
                _toolWriteBacks[i] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, string> { [$"tool.{i}.note"] = $"result-{i}" });
            }
        }
        // ToolsHistorical leaves every slot null — the shape the sub-agent path actually produced.

        _populatedSession = new TemporalAgentSession(_sessionId);
        RunSessionOwnedTurns(_populatedSession, TurnCount);
        _snapshotOfPopulatedSession = _populatedSession.Serialize();
    }

    /// <summary>Baseline: history and StateBag held on the agent instance (pre-Option-D model).</summary>
    [Benchmark(Baseline = true)]
    public int LegacyAgentOwnedTurnLoop()
    {
        var history = new List<DurableSessionEntry>();
        JsonElement? carriedBag = null;

        for (var turn = 0; turn < TurnCount; turn++)
        {
            history.Add(BuildRequest(turn));

            // Rebuilding the accumulated prompt from history each turn is unchanged by Option D;
            // it is included so both arms carry the same O(history) cost.
            var accumulated = new List<ChatMessage>();
            foreach (var entry in history)
            {
                foreach (var message in entry.Messages)
                {
                    accumulated.Add(message);
                }
            }

            // Old behaviour: the LLM step's bag replaced the carried bag outright.
            carriedBag = _llmStepBag;

            if (_hasToolRound)
            {
                carriedBag = StateBagMerge.Merge(carriedBag, _toolWriteBacks, alwaysScopesStoreKey: null);
            }

            history.Add(BuildResponse(turn));
        }

        return history.Count + (carriedBag?.ValueKind == JsonValueKind.Object ? 1 : 0);
    }

    /// <summary>Option D: history and StateBag owned by the session.</summary>
    [Benchmark]
    public int SessionOwnedTurnLoop() =>
        RunSessionOwnedTurns(new TemporalAgentSession(_sessionId), TurnCount);

    /// <summary>Snapshot write cost at the accumulated history size.</summary>
    [Benchmark]
    public JsonElement SerializeSnapshot() => _populatedSession.Serialize();

    /// <summary>Snapshot read cost — the continue-as-new restore path.</summary>
    [Benchmark]
    public int DeserializeSnapshot() =>
        TemporalAgentSession.Deserialize(_snapshotOfPopulatedSession).History.Count;

    private int RunSessionOwnedTurns(TemporalAgentSession session, int turnCount)
    {
        for (var turn = 0; turn < turnCount; turn++)
        {
            session.AppendHistoryEntry(BuildRequest(turn));

            var accumulated = new List<ChatMessage>();
            foreach (var entry in session.History)
            {
                foreach (var message in entry.Messages)
                {
                    accumulated.Add(message);
                }
            }

            _ = session.SerializeStateBag();
            session.OverlayTrustedStateBag(_llmStepBag);

            if (_hasToolRound)
            {
                _ = session.SerializeStateBag();
                session.MergeToolStateBagWriteBacks(_toolWriteBacks);
            }

            session.AppendHistoryEntry(BuildResponse(turn));
        }

        return session.History.Count;
    }

    /// <summary>The tool-round shape a benchmarked turn uses.</summary>
    public enum TurnShape
    {
        /// <summary>No tool calls — one LLM step per turn.</summary>
        NoTools,

        /// <summary>
        /// Four tool calls whose StateBag write-backs are all null. This is what the sub-agent path
        /// actually produced in v0.3 and still produces today, so it is the exact historical
        /// baseline.
        /// </summary>
        ToolsHistorical,

        /// <summary>
        /// Four tool calls with populated StateBag write-backs. Not a historical baseline — a
        /// forward cost model for closing the missing-session-ID gap in InvokeAgentToolInput.
        /// </summary>
        ToolsProspective,
    }

    private static AgentSessionRequest BuildRequest(int turn) => new()
    {
        CorrelationId = $"corr-{turn}",
        CreatedAt = Timestamp,
        Messages = [new ChatMessage(ChatRole.User, $"Question number {turn} about the codebase.")],
    };

    private static AgentSessionResponse BuildResponse(int turn) => new()
    {
        CorrelationId = $"corr-{turn}",
        CreatedAt = Timestamp,
        Messages = [new ChatMessage(ChatRole.Assistant, $"Answer number {turn} with some detail.")],
    };
}
