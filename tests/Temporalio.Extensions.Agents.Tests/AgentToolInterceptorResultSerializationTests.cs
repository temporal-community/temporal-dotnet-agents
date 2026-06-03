using System.Text.Json;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Round-trip serialization tests for <see cref="DurableToolInterceptorResult"/> and
/// <see cref="DurableToolInterceptorInput"/> via <see cref="AgentSessionJsonContext"/>.
/// These types cross the workflow/activity boundary — they must survive source-gen JSON.
/// </summary>
public class AgentToolInterceptorResultSerializationTests
{
    private static readonly JsonSerializerOptions s_opts =
        AgentSessionJsonContext.Default.Options;

    [Fact]
    public void DurableToolOutcome_Enum_RoundTrips()
    {
        foreach (var value in Enum.GetValues<DurableToolOutcome>())
        {
            var json = JsonSerializer.Serialize(value, s_opts);
            var deserialized = JsonSerializer.Deserialize<DurableToolOutcome>(json, s_opts);
            Assert.Equal(value, deserialized);
        }
    }

    [Fact]
    public void DurableToolInterceptorResult_Proceed_RoundTrips()
    {
        var result = new DurableToolInterceptorResult
        {
            Outcome = DurableToolOutcome.Proceed,
            EnrichedDescription = "enriched desc",
            ModifiedArguments = new Dictionary<string, object?> { ["x"] = 42 },
            Metadata = new Dictionary<string, string> { ["audit.id"] = "abc" },
        };

        var json = JsonSerializer.Serialize(result, s_opts);
        var rt = JsonSerializer.Deserialize<DurableToolInterceptorResult>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal(DurableToolOutcome.Proceed, rt!.Outcome);
        Assert.Equal("enriched desc", rt.EnrichedDescription);
        Assert.NotNull(rt.ModifiedArguments);
        Assert.NotNull(rt.Metadata);
        Assert.Equal("abc", rt.Metadata["audit.id"]);
    }

    [Fact]
    public void DurableToolInterceptorResult_Block_RoundTrips()
    {
        var result = new DurableToolInterceptorResult
        {
            Outcome = DurableToolOutcome.Block,
            Message = "policy violation",
        };

        var json = JsonSerializer.Serialize(result, s_opts);
        var rt = JsonSerializer.Deserialize<DurableToolInterceptorResult>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal(DurableToolOutcome.Block, rt!.Outcome);
        Assert.Equal("policy violation", rt.Message);
        Assert.Null(rt.EnrichedDescription);
        Assert.Null(rt.ModifiedArguments);
        Assert.Null(rt.Metadata);
    }

    [Fact]
    public void DurableToolInterceptorResult_Skip_RoundTrips()
    {
        var result = new DurableToolInterceptorResult
        {
            Outcome = DurableToolOutcome.Skip,
            Message = "cached value",
        };

        var json = JsonSerializer.Serialize(result, s_opts);
        var rt = JsonSerializer.Deserialize<DurableToolInterceptorResult>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal(DurableToolOutcome.Skip, rt!.Outcome);
        Assert.Equal("cached value", rt.Message);
    }

    [Fact]
    public void DurableToolInterceptorResult_PauseForApproval_RoundTrips()
    {
        var result = new DurableToolInterceptorResult
        {
            Outcome = DurableToolOutcome.PauseForApproval,
            EnrichedDescription = "Approve refund for Jane Doe",
            Message = "Approve refund for Jane Doe",
        };

        var json = JsonSerializer.Serialize(result, s_opts);
        var rt = JsonSerializer.Deserialize<DurableToolInterceptorResult>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal(DurableToolOutcome.PauseForApproval, rt!.Outcome);
        Assert.Equal("Approve refund for Jane Doe", rt.EnrichedDescription);
    }

    [Fact]
    public void DurableToolInterceptorInput_RoundTrips()
    {
        var input = new DurableToolInterceptorInput
        {
            AgentName = "myAgent",
            ToolName = "refund",
            Arguments = new Dictionary<string, object?> { ["amount"] = 29.99, ["orderId"] = "ORD-123" },
            CallId = "call-001",
            SerializedStateBag = null,
        };

        var json = JsonSerializer.Serialize(input, s_opts);
        var rt = JsonSerializer.Deserialize<DurableToolInterceptorInput>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal("myAgent", rt!.AgentName);
        Assert.Equal("refund", rt.ToolName);
        Assert.Equal("call-001", rt.CallId);
        Assert.NotNull(rt.Arguments);
    }

    [Fact]
    public void DurableToolInterceptorInput_NullArguments_RoundTrips()
    {
        var input = new DurableToolInterceptorInput
        {
            AgentName = "agent",
            ToolName = "ping",
            Arguments = null,
            CallId = null,
        };

        var json = JsonSerializer.Serialize(input, s_opts);
        var rt = JsonSerializer.Deserialize<DurableToolInterceptorInput>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Null(rt!.Arguments);
        Assert.Null(rt.CallId);
    }
}
