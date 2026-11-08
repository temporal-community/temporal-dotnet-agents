using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.State;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(DurableSessionEntry))]
[JsonSerializable(typeof(DurableSessionRequest))]
[JsonSerializable(typeof(DurableSessionResponse))]
[JsonSerializable(typeof(AgentSessionRequest))]
[JsonSerializable(typeof(AgentSessionResponse))]
[JsonSerializable(typeof(TemporalAgentTurnOutcome))]
[JsonSerializable(typeof(TemporalAgentTurnOutcomeStatus))]
// Session wire contract — TemporalAgentSession is not itself a serialization root
// (see TemporalAgentSessionSnapshot remarks); the snapshot DTO is registered in its place.
[JsonSerializable(typeof(TemporalCommunity.Extensions.Agents.Session.TemporalAgentSessionSnapshot))]
// Activity I/O types — workflow ↔ activity boundary
[JsonSerializable(typeof(AgentStepInput))]
[JsonSerializable(typeof(PrepareAgentTurnInput))]
[JsonSerializable(typeof(PreparedAgentTurn))]
[JsonSerializable(typeof(PreparedMessageAttribution))]
[JsonSerializable(typeof(IReadOnlyList<PreparedMessageAttribution>))]
[JsonSerializable(typeof(PreparedAgentTool))]
[JsonSerializable(typeof(ChatHistoryMemoryProviderScope))]
[JsonSerializable(typeof(ChatHistoryMemoryProvider.State))]
[JsonSerializable(typeof(TemporalMemoryScopeSnapshot))]
[JsonSerializable(typeof(InitializeChatHistoryMemoryInput))]
[JsonSerializable(typeof(InitializedChatHistoryMemory))]
[JsonSerializable(typeof(IReadOnlyList<PreparedAgentTool>))]
[JsonSerializable(typeof(CompleteAgentTurnInput))]
[JsonSerializable(typeof(CompletedAgentTurn))]
[JsonSerializable(typeof(DurableChatTurnRange))]
[JsonSerializable(typeof(AgentStepResult))]
[JsonSerializable(typeof(StateBagDelta))]
[JsonSerializable(typeof(TemporalCommunity.Extensions.AI.Internal.DurableFunctionDeclarationSnapshot))]
[JsonSerializable(typeof(InvokeAgentToolInput))]
[JsonSerializable(typeof(InvokeAgentToolResult))]
[JsonSerializable(typeof(IReadOnlyList<DurableSessionEntry>))]
[JsonSerializable(typeof(List<DurableSessionEntry>))]
// Feature L — tool interceptor I/O. DurableToolInterceptorResult/DurableToolOutcome live in
// TemporalCommunity.Extensions.AI (DurableAIJsonContext already registers them); only the
// Agents-specific input type needs registration here.
[JsonSerializable(typeof(TemporalCommunity.Extensions.Agents.Workflows.DurableToolInterceptorInput))]
// Common dictionary payloads
[JsonSerializable(typeof(Dictionary<string, string>))]
// Function call and result content
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(JsonDocument))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonValue))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(IEnumerable<string>))]
[JsonSerializable(typeof(char))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class AgentSessionJsonContext : JsonSerializerContext;
