using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Tools;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// Tests for <see cref="DurableToolDecision"/> and its four inner outcome types.
/// </summary>
/// <remarks>
/// <c>DurableToolDecision</c> is the developer-facing discriminated union — it is not
/// wire-serialized across the Temporal workflow/activity boundary. The internal
/// <c>DurableToolInterceptorResult</c> DTO (in <c>Temporalio.Extensions.AI</c>) is the
/// serialized form; its round-trip coverage lives in
/// <c>AgentToolInterceptorResultSerializationTests</c> in the Agents test project.
/// </remarks>
public class DurableToolDecisionTests
{
    // ── Proceed ──────────────────────────────────────────────────────────────

    [Fact]
    public void Proceed_NoArgs_ReturnsDefaultProceedDecision()
    {
        var d = DurableToolDecision.Proceed();
        var proceed = Assert.IsType<DurableToolDecision.ProceedDecision>(d);
        Assert.Null(proceed.EnrichedDescription);
        Assert.Null(proceed.ModifiedArguments);
        Assert.Null(proceed.Metadata);
    }

    [Fact]
    public void Proceed_WithAllArgs_ReturnsPopulatedProceedDecision()
    {
        var args = new Dictionary<string, object?> { ["x"] = 42 };
        var meta = new Dictionary<string, string> { ["key"] = "value" };
        var d = DurableToolDecision.Proceed("enriched", args, meta);
        var proceed = Assert.IsType<DurableToolDecision.ProceedDecision>(d);
        Assert.Equal("enriched", proceed.EnrichedDescription);
        Assert.Same(args, proceed.ModifiedArguments);
        Assert.Same(meta, proceed.Metadata);
    }

    // ── PauseForApproval ─────────────────────────────────────────────────────

    [Fact]
    public void PauseForApproval_ReturnsApprovalRequiredDecision()
    {
        var d = DurableToolDecision.PauseForApproval("needs approval");
        var approval = Assert.IsType<DurableToolDecision.ApprovalRequiredDecision>(d);
        Assert.Equal("needs approval", approval.Description);
        Assert.Null(approval.Metadata);
    }

    [Fact]
    public void PauseForApproval_WithMetadata_ReturnsMetadata()
    {
        var meta = new Dictionary<string, string> { ["risk"] = "high" };
        var d = DurableToolDecision.PauseForApproval("needs review", meta);
        var approval = Assert.IsType<DurableToolDecision.ApprovalRequiredDecision>(d);
        Assert.Same(meta, approval.Metadata);
    }

    [Fact]
    public void PauseForApproval_NullOrEmptyDescription_Throws()
    {
        Assert.Throws<ArgumentException>(() => DurableToolDecision.PauseForApproval(string.Empty));
        Assert.Throws<ArgumentException>(() => DurableToolDecision.PauseForApproval(""));
    }

    // ── Skip ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Skip_ReturnsSyntheticResult()
    {
        var d = DurableToolDecision.Skip("cached result");
        var skip = Assert.IsType<DurableToolDecision.SkipDecision>(d);
        Assert.Equal("cached result", skip.SyntheticResult);
        Assert.Null(skip.Metadata);
    }

    [Fact]
    public void Skip_EmptyString_IsAllowed()
    {
        // Empty synthetic result is valid (tool produces no output).
        var d = DurableToolDecision.Skip(string.Empty);
        var skip = Assert.IsType<DurableToolDecision.SkipDecision>(d);
        Assert.Equal(string.Empty, skip.SyntheticResult);
    }

    [Fact]
    public void Skip_NullSyntheticResult_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DurableToolDecision.Skip(null!));
    }

    // ── Block ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Block_ReturnsBlockDecision()
    {
        var d = DurableToolDecision.Block("policy violation");
        var block = Assert.IsType<DurableToolDecision.BlockDecision>(d);
        Assert.Equal("policy violation", block.Reason);
        Assert.Null(block.Metadata);
    }

    [Fact]
    public void Block_NullOrEmptyReason_Throws()
    {
        Assert.Throws<ArgumentException>(() => DurableToolDecision.Block(string.Empty));
    }

    // ── Type discrimination ────────────────────────────────────────────────────

    [Fact]
    public void AllOutcomes_AreDistinctTypes()
    {
        DurableToolDecision proceed = DurableToolDecision.Proceed();
        DurableToolDecision approval = DurableToolDecision.PauseForApproval("desc");
        DurableToolDecision skip = DurableToolDecision.Skip("result");
        DurableToolDecision block = DurableToolDecision.Block("reason");

        Assert.IsType<DurableToolDecision.ProceedDecision>(proceed);
        Assert.IsType<DurableToolDecision.ApprovalRequiredDecision>(approval);
        Assert.IsType<DurableToolDecision.SkipDecision>(skip);
        Assert.IsType<DurableToolDecision.BlockDecision>(block);
    }
}
