using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Result of a single <c>RunAgentStepAsync</c> activity execution.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="IsFinal"/> is <see langword="true"/>, the step produced an assistant message
/// with no tool calls and the workflow loop terminates. When <see langword="false"/>,
/// <see cref="ToolCalls"/> carries the LLM's pending <see cref="FunctionCallContent"/> items,
/// which the workflow dispatches as separate <c>InvokeFunctionAsync</c> activities.
/// </para>
/// </remarks>
internal sealed class AgentStepResult
{
    /// <summary>
    /// <see langword="true"/> when the assistant produced a final answer (no tool calls).
    /// </summary>
    public required bool IsFinal { get; init; }

    /// <summary>
    /// The assistant message produced by this step. In tool-call iterations, this contains
    /// one or more <see cref="FunctionCallContent"/> items; in the final iteration, it
    /// contains the final text answer.
    /// </summary>
    public required ChatMessage AssistantMessage { get; init; }

    /// <summary>
    /// When <see cref="IsFinal"/> is <see langword="false"/>, this contains the
    /// <see cref="FunctionCallContent"/> items the workflow must dispatch as activities.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FunctionCallContent>? ToolCalls { get; init; }

    /// <summary>
    /// Updated <see cref="State.AgentSessionStateBag"/> serialization from this step.
    /// Threaded back into the next iteration via <see cref="AgentStepInput.SerializedStateBag"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? UpdatedStateBag { get; init; }

    /// <summary>Optional usage metadata for the LLM call performed during this step.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageDetails? Usage { get; init; }

    /// <summary>
    /// Optional response identifier produced by the LLM provider for this step. Maps to OTel
    /// GenAI semantic convention <c>gen_ai.response.id</c>. Used for correlating Temporal-side
    /// activity execution with upstream provider observability (request logs, billing detail).
    /// Step 3c.2 carve-out from <see cref="Microsoft.Agents.AI.AgentResponse"/>'s broader surface
    /// per the Q17 design decision (everything else from <c>AgentResponse</c>
    /// — <c>ContinuationToken</c>, <c>RawRepresentation</c>, <c>AdditionalProperties</c> — is
    /// dropped at the activity boundary to keep replay-critical payloads minimal).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseId { get; init; }

    /// <summary>
    /// Worker-side settings bundle resolved from the agent's <c>DurableAgentRegistration</c>.
    /// Only populated when <see cref="AgentStepInput.NeedsWorkerSettingsResolution"/> was
    /// <see langword="true"/>; <see langword="null"/> on non-resolution steps.
    /// </summary>
    /// <remarks>
    /// Replaces the prior <c>ResolvedUseExternalStoreMode</c> / <c>ResolvedToolActivityOptions</c> /
    /// <c>ResolvedMaxToolCallsPerTurn</c> trio (Step 3c.1 migration). The legacy field names are
    /// preserved as forwarding computed properties below so consumers don't need updating; new
    /// fields added in Steps 4 + 6 (<c>DefaultChatClientFactoryKey</c>,
    /// <c>CompactionStrategyKey</c>) flow through the same record without further schema thrashing.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProxyResolvedWorkerConfig? ResolvedWorkerConfig { get; init; }

    // ── Forwarding properties — preserve consumer call sites across the Step 3c.1 migration ──

    /// <summary>
    /// Resolved worker-side external-store mode flag. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.UseExternalStoreMode"/>;
    /// <see langword="null"/> on non-resolution steps.
    /// </summary>
    [JsonIgnore]
    public bool? ResolvedUseExternalStoreMode => ResolvedWorkerConfig?.UseExternalStoreMode;

    /// <summary>
    /// Resolved per-tool <see cref="ActivityOptions"/> dictionary. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.ToolActivityOptions"/>;
    /// <see langword="null"/> on non-resolution steps.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ActivityOptions>? ResolvedToolActivityOptions =>
        ResolvedWorkerConfig?.ToolActivityOptions;

    /// <summary>
    /// Resolved <c>MaxToolCallsPerTurn</c>. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.MaxToolCallsPerTurn"/>;
    /// <see langword="null"/> on non-resolution steps.
    /// </summary>
    [JsonIgnore]
    public int? ResolvedMaxToolCallsPerTurn => ResolvedWorkerConfig?.MaxToolCallsPerTurn;
}
