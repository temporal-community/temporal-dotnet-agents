using System.Text.Json;
using Temporalio.Extensions.AI.Tools;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// Round-trip serialization tests for the MEAI-path interceptor wire types:
/// <see cref="DurableToolInterceptorInput"/>, <see cref="DurableToolInterceptorResult"/>,
/// and <see cref="DurableToolOutcome"/> via <see cref="DurableAIJsonContext"/>.
/// These types cross the workflow/activity boundary — they must survive source-gen JSON.
/// </summary>
public class DurableToolInterceptorSerializationTests
{
    private static readonly JsonSerializerOptions s_opts =
        DurableAIJsonContext.Default.Options;

    [Fact]
    public void DurableToolOutcome_Enum_AllValuesRoundTrip()
    {
        foreach (var value in Enum.GetValues<DurableToolOutcome>())
        {
            var json = JsonSerializer.Serialize(value, s_opts);
            var deserialized = JsonSerializer.Deserialize<DurableToolOutcome>(json, s_opts);
            Assert.Equal(value, deserialized);
        }
    }

    [Fact]
    public void DurableToolOutcome_Proceed_IsZero()
    {
        // Numeric identity is part of the wire contract — changing values would break replay.
        Assert.Equal(0, (int)DurableToolOutcome.Proceed);
        Assert.Equal(1, (int)DurableToolOutcome.PauseForApproval);
        Assert.Equal(2, (int)DurableToolOutcome.Skip);
        Assert.Equal(3, (int)DurableToolOutcome.Block);
    }

    [Fact]
    public void DurableToolInterceptorResult_Proceed_RoundTrips()
    {
        var result = new DurableToolInterceptorResult
        {
            Outcome = DurableToolOutcome.Proceed,
            EnrichedDescription = "enriched description",
            ModifiedArguments = new Dictionary<string, object?> { ["x"] = "42" },
            Metadata = new Dictionary<string, string> { ["audit.id"] = "abc" },
        };

        var json = JsonSerializer.Serialize(result, s_opts);
        var rt = JsonSerializer.Deserialize<DurableToolInterceptorResult>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal(DurableToolOutcome.Proceed, rt!.Outcome);
        Assert.Equal("enriched description", rt.EnrichedDescription);
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
            Message = "cached result",
        };

        var json = JsonSerializer.Serialize(result, s_opts);
        var rt = JsonSerializer.Deserialize<DurableToolInterceptorResult>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal(DurableToolOutcome.Skip, rt!.Outcome);
        Assert.Equal("cached result", rt.Message);
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
        Assert.Equal("Approve refund for Jane Doe", rt.Message);
    }

    [Fact]
    public void DurableToolInterceptorInput_FullyPopulated_RoundTrips()
    {
        var input = new DurableToolInterceptorInput
        {
            ToolName = "refund",
            Arguments = new Dictionary<string, object?> { ["amount"] = "29.99", ["orderId"] = "ORD-123" },
            CallId = "call-001",
            ConversationId = "conv-abc",
            CorrelationId = "corr-xyz",
            TurnNumber = 3,
        };

        var json = JsonSerializer.Serialize(input, s_opts);
        var rt = JsonSerializer.Deserialize<DurableToolInterceptorInput>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal("refund", rt!.ToolName);
        Assert.Equal("call-001", rt.CallId);
        Assert.Equal("conv-abc", rt.ConversationId);
        Assert.Equal("corr-xyz", rt.CorrelationId);
        Assert.Equal(3, rt.TurnNumber);
        Assert.NotNull(rt.Arguments);
    }

    [Fact]
    public void DurableToolInterceptorInput_NullOptionals_RoundTrips()
    {
        var input = new DurableToolInterceptorInput
        {
            ToolName = "ping",
            Arguments = null,
            CallId = null,
            ConversationId = null,
            CorrelationId = null,
            TurnNumber = null,
        };

        var json = JsonSerializer.Serialize(input, s_opts);
        var rt = JsonSerializer.Deserialize<DurableToolInterceptorInput>(json, s_opts);

        Assert.NotNull(rt);
        Assert.Equal("ping", rt!.ToolName);
        Assert.Null(rt.Arguments);
        Assert.Null(rt.CallId);
        Assert.Null(rt.ConversationId);
        Assert.Null(rt.CorrelationId);
        Assert.Null(rt.TurnNumber);
    }
}
