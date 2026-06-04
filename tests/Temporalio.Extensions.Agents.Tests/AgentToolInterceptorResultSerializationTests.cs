using System.Text.Json;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Xunit;

using AgentsInterceptorInput = Temporalio.Extensions.Agents.Workflows.DurableToolInterceptorInput;
using AgentsInterceptorResult = Temporalio.Extensions.AI.DurableToolInterceptorResult;
using AgentsToolOutcome = Temporalio.Extensions.AI.DurableToolOutcome;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Round-trip serialization tests for <see cref="AgentsInterceptorResult"/> and
/// <see cref="AgentsInterceptorInput"/> via <see cref="TemporalAgentJsonUtilities.DefaultOptions"/>,
/// which chains <see cref="DurableAIJsonContext"/> (registers the AI-lib wire types) over
/// <see cref="State.AgentSessionJsonContext"/> (registers the Agents-lib wire types).
/// These types cross the workflow/activity boundary — they must survive source-gen JSON.
/// </summary>
public class AgentToolInterceptorResultSerializationTests
{
    private static readonly JsonSerializerOptions s_opts =
        TemporalAgentJsonUtilities.DefaultOptions;

    [Fact]
    public void AgentsToolOutcome_Enum_RoundTrips()
    {
        foreach (var value in Enum.GetValues<AgentsToolOutcome>())
        {
            var json = JsonSerializer.Serialize(value, s_opts);
            var deserialized = JsonSerializer.Deserialize<AgentsToolOutcome>(json, s_opts);
            Assert.Equal(value, deserialized);
        }
    }

    [Fact]
    public void AgentsInterceptorResult_Proceed_RoundTrips()
    {
        var result = new AgentsInterceptorResult
        {
            Outcome = AgentsToolOutcome.Proceed,
            EnrichedDescription = "enriched desc",
            ModifiedArguments = new Dictionary<string, object?> { ["x"] = 42 },
            Metadata = new Dictionary<string, string> { ["audit.id"] = "abc" },
        };

        var json = JsonSerializer.Serialize(result, s_opts);
        var rt = JsonSerializer.Deserialize<AgentsInterceptorResult>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal(AgentsToolOutcome.Proceed, rt!.Outcome);
        Assert.Equal("enriched desc", rt.EnrichedDescription);
        Assert.NotNull(rt.ModifiedArguments);
        Assert.NotNull(rt.Metadata);
        Assert.Equal("abc", rt.Metadata["audit.id"]);
    }

    [Fact]
    public void AgentsInterceptorResult_Block_RoundTrips()
    {
        var result = new AgentsInterceptorResult
        {
            Outcome = AgentsToolOutcome.Block,
            Message = "policy violation",
        };

        var json = JsonSerializer.Serialize(result, s_opts);
        var rt = JsonSerializer.Deserialize<AgentsInterceptorResult>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal(AgentsToolOutcome.Block, rt!.Outcome);
        Assert.Equal("policy violation", rt.Message);
        Assert.Null(rt.EnrichedDescription);
        Assert.Null(rt.ModifiedArguments);
        Assert.Null(rt.Metadata);
    }

    [Fact]
    public void AgentsInterceptorResult_Skip_RoundTrips()
    {
        var result = new AgentsInterceptorResult
        {
            Outcome = AgentsToolOutcome.Skip,
            Message = "cached value",
        };

        var json = JsonSerializer.Serialize(result, s_opts);
        var rt = JsonSerializer.Deserialize<AgentsInterceptorResult>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal(AgentsToolOutcome.Skip, rt!.Outcome);
        Assert.Equal("cached value", rt.Message);
    }

    [Fact]
    public void AgentsInterceptorResult_PauseForApproval_RoundTrips()
    {
        var result = new AgentsInterceptorResult
        {
            Outcome = AgentsToolOutcome.PauseForApproval,
            EnrichedDescription = "Approve refund for Jane Doe",
            Message = "Approve refund for Jane Doe",
        };

        var json = JsonSerializer.Serialize(result, s_opts);
        var rt = JsonSerializer.Deserialize<AgentsInterceptorResult>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal(AgentsToolOutcome.PauseForApproval, rt!.Outcome);
        Assert.Equal("Approve refund for Jane Doe", rt.EnrichedDescription);
    }

    [Fact]
    public void AgentsInterceptorInput_RoundTrips()
    {
        var input = new AgentsInterceptorInput
        {
            AgentName = "myAgent",
            ToolName = "refund",
            Arguments = new Dictionary<string, object?> { ["amount"] = 29.99, ["orderId"] = "ORD-123" },
            CallId = "call-001",
            SerializedStateBag = null,
        };

        var json = JsonSerializer.Serialize(input, s_opts);
        var rt = JsonSerializer.Deserialize<AgentsInterceptorInput>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal("myAgent", rt!.AgentName);
        Assert.Equal("refund", rt.ToolName);
        Assert.Equal("call-001", rt.CallId);
        Assert.NotNull(rt.Arguments);
    }

    [Fact]
    public void AgentsInterceptorInput_NullArguments_RoundTrips()
    {
        var input = new AgentsInterceptorInput
        {
            AgentName = "agent",
            ToolName = "ping",
            Arguments = null,
            CallId = null,
        };

        var json = JsonSerializer.Serialize(input, s_opts);
        var rt = JsonSerializer.Deserialize<AgentsInterceptorInput>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Null(rt!.Arguments);
        Assert.Null(rt.CallId);
    }
}
