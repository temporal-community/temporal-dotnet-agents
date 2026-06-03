using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(DurableSessionEntry))]
[JsonSerializable(typeof(DurableSessionRequest))]
[JsonSerializable(typeof(DurableSessionResponse))]
[JsonSerializable(typeof(CompactionMarkerEntry))]
[JsonSerializable(typeof(AgentSessionRequest))]
[JsonSerializable(typeof(AgentSessionResponse))]
// Activity I/O types — workflow ↔ activity boundary
[JsonSerializable(typeof(AgentStepInput))]
[JsonSerializable(typeof(AgentStepResult))]
[JsonSerializable(typeof(InvokeAgentToolInput))]
[JsonSerializable(typeof(InvokeAgentToolResult))]
[JsonSerializable(typeof(AppendAgentTurnInput))]
[JsonSerializable(typeof(ReduceHistoryInStoreInput))]
[JsonSerializable(typeof(RunCompactionSummaryInput))]
[JsonSerializable(typeof(RunCompactionSummaryResult))]
[JsonSerializable(typeof(CompactHistoryInput))]
[JsonSerializable(typeof(IReadOnlyList<DurableSessionEntry>))]
[JsonSerializable(typeof(List<DurableSessionEntry>))]
// Feature L — tool interceptor I/O (Agents-specific wire types; fully qualified to avoid
// ambiguity with the identically-named types in Temporalio.Extensions.AI that were added
// in Phase 2 for the MEAI interceptor path).
[JsonSerializable(typeof(Temporalio.Extensions.Agents.Workflows.DurableToolInterceptorInput))]
[JsonSerializable(typeof(Temporalio.Extensions.Agents.Workflows.DurableToolInterceptorResult))]
[JsonSerializable(typeof(Temporalio.Extensions.Agents.Workflows.DurableToolOutcome))]
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
