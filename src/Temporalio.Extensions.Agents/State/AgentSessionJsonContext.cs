using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

[JsonSourceGenerationOptions(WriteIndented = false)]
// Feature B — approval scope types
[JsonSerializable(typeof(Temporalio.Extensions.Agents.ApprovalScopeRecord))]
[JsonSerializable(typeof(List<Temporalio.Extensions.Agents.ApprovalScopeRecord>))]
[JsonSerializable(typeof(IReadOnlyList<Temporalio.Extensions.Agents.ApprovalScopeRecord>))]
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
// Feature L — tool interceptor I/O. DurableToolInterceptorResult/DurableToolOutcome live in
// Temporalio.Extensions.AI (DurableAIJsonContext already registers them); only the
// Agents-specific input type needs registration here.
[JsonSerializable(typeof(Temporalio.Extensions.Agents.Workflows.DurableToolInterceptorInput))]
// Feature B — approval-scope store activity I/O
[JsonSerializable(typeof(Temporalio.Extensions.Agents.Workflows.AppendAlwaysScopeInput))]
[JsonSerializable(typeof(Temporalio.Extensions.Agents.Workflows.LoadAlwaysScopesInput))]
[JsonSerializable(typeof(Temporalio.Extensions.Agents.Workflows.LoadAlwaysScopesResult))]
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
