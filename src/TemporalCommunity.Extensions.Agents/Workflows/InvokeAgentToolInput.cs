using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Input payload for the per-tool activity dispatched by a durable agent's workflow loop
/// (<c>TemporalCommunity.Extensions.Agents.InvokeAgentTool</c>). The activity resolves the named
/// agent's local tool registry and invokes the named tool with the supplied arguments.
/// </summary>
/// <remarks>
/// Distinct from MEAI's flat <c>TemporalCommunity.Extensions.AI.InvokeFunction</c> activity: this
/// activity scopes tool resolution per-agent, so two agents on the same worker can register
/// tools with the same name without collision. The Temporal Web UI shows distinct activity
/// types so operators can tell which dispatch path is in play.
/// </remarks>
internal sealed class InvokeAgentToolInput
{
    /// <summary>
    /// The name of the agent whose tool registry should be searched. Must match a name registered
    /// via <c>TemporalAgentsOptions.AddDurableAgent</c>.
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>Durable Temporal agent session ID carried by the invoking workflow.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalMemoryScopeSnapshot? MemoryScope { get; init; }

    /// <summary>
    /// The case-insensitive tool name as it was declared on the <c>DurableAgentBuilder</c>
    /// (typically the same as <c>AIFunction.Name</c>).
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Arguments to forward to the tool's <c>InvokeAsync</c>. May be <see langword="null"/> when
    /// the tool takes no arguments.
    /// </summary>
    public IDictionary<string, object?>? Arguments { get; init; }

    /// <summary>
    /// Originating <c>FunctionCallContent.CallId</c> from the LLM. Echoed back in the result so the
    /// workflow can correlate the activity result with its pending tool-call slot.
    /// </summary>
    public string? CallId { get; init; }

    /// <summary>
    /// Frozen provider-selected binding for managed session calls; null for other execution paths.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreparedAgentTool? PreparedTool { get; init; }

    /// <summary>
    /// Serialized <c>AgentSessionStateBag</c> snapshot carried into the tool activity (X-1).
    /// The activity builds its <c>TemporalAgentSession</c> from this bag so tools (and any
    /// <c>AIContextProvider</c> they consult) see accumulated session state rather than an
    /// empty bag. Mutations the tool makes are returned via
    /// <see cref="InvokeAgentToolResult.StateBagDelta"/> and merged back by the workflow.
    /// </summary>
    /// <remarks><see langword="null"/> means no accumulated state.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SerializedStateBag { get; init; }

    /// <summary>Whether the workflow recorded an accepted approval for this exact call.</summary>
    public bool ApprovalAccepted { get; init; }

    /// <summary>Workflow-recorded fingerprint of the call before its approval wait.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApprovedCallFingerprint { get; init; }

    /// <summary>Conversation snapshot that initiated this tool batch.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ChatMessage>? Messages { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Iteration { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FunctionCallIndex { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FunctionCount { get; init; }

    /// <summary>Maximum serialized activity payload size, including codec expansion allowance.</summary>
    public int MaximumTransportPayloadSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>Allowance reserved for payload codec expansion.</summary>
    public int PayloadCodecExpansionAllowanceBytes { get; init; }
}
