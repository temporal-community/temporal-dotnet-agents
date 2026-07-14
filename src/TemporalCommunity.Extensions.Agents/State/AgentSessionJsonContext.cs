using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.State;

[JsonSourceGenerationOptions(WriteIndented = false)]
// Feature B — approval scope types
[JsonSerializable(typeof(TemporalCommunity.Extensions.Agents.Approvals.ApprovalScopeRecord))]
[JsonSerializable(typeof(List<TemporalCommunity.Extensions.Agents.Approvals.ApprovalScopeRecord>))]
[JsonSerializable(typeof(IReadOnlyList<TemporalCommunity.Extensions.Agents.Approvals.ApprovalScopeRecord>))]
[JsonSerializable(typeof(TemporalCommunity.Extensions.Agents.Approvals.DurableAgentApprovalDecision))]
[JsonSerializable(typeof(List<TemporalCommunity.Extensions.Agents.Approvals.DurableAgentApprovalDecision>))]
[JsonSerializable(typeof(IReadOnlyList<TemporalCommunity.Extensions.Agents.Approvals.DurableAgentApprovalDecision>))]
[JsonSerializable(typeof(DurableSessionEntry))]
[JsonSerializable(typeof(DurableSessionRequest))]
[JsonSerializable(typeof(DurableSessionResponse))]
[JsonSerializable(typeof(AgentSessionRequest))]
[JsonSerializable(typeof(AgentSessionResponse))]
// Activity I/O types — workflow ↔ activity boundary
[JsonSerializable(typeof(AgentStepInput))]
[JsonSerializable(typeof(AgentStepResult))]
[JsonSerializable(typeof(InvokeAgentToolInput))]
[JsonSerializable(typeof(InvokeAgentToolResult))]
[JsonSerializable(typeof(IReadOnlyList<DurableSessionEntry>))]
[JsonSerializable(typeof(List<DurableSessionEntry>))]
// Feature L — tool interceptor I/O. DurableToolInterceptorResult/DurableToolOutcome live in
// TemporalCommunity.Extensions.AI (DurableAIJsonContext already registers them); only the
// Agents-specific input type needs registration here.
[JsonSerializable(typeof(TemporalCommunity.Extensions.Agents.Workflows.DurableToolInterceptorInput))]
// Feature B — approval-scope store activity I/O
[JsonSerializable(typeof(TemporalCommunity.Extensions.Agents.Workflows.AppendAlwaysScopeInput))]
[JsonSerializable(typeof(TemporalCommunity.Extensions.Agents.Workflows.LoadAlwaysScopesInput))]
[JsonSerializable(typeof(TemporalCommunity.Extensions.Agents.Workflows.LoadAlwaysScopesResult))]
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
