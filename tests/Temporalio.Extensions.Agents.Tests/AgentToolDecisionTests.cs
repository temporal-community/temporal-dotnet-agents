using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for <see cref="AgentToolDecision"/> and its four inner outcome types.
/// </summary>
public class AgentToolDecisionTests
{
    // ── Proceed ──────────────────────────────────────────────────────────────

    [Fact]
    public void Proceed_NoArgs_ReturnsDefaultProceedDecision()
    {
        var d = AgentToolDecision.Proceed();
        var proceed = Assert.IsType<AgentToolDecision.ProceedDecision>(d);
        Assert.Null(proceed.EnrichedDescription);
        Assert.Null(proceed.ModifiedArguments);
        Assert.Null(proceed.Metadata);
    }

    [Fact]
    public void Proceed_WithAllArgs_ReturnsPopulatedProceedDecision()
    {
        var args = new Dictionary<string, object?> { ["x"] = 42 };
        var meta = new Dictionary<string, string> { ["key"] = "value" };
        var d = AgentToolDecision.Proceed("enriched", args, meta);
        var proceed = Assert.IsType<AgentToolDecision.ProceedDecision>(d);
        Assert.Equal("enriched", proceed.EnrichedDescription);
        Assert.Same(args, proceed.ModifiedArguments);
        Assert.Same(meta, proceed.Metadata);
    }

    // ── PauseForApproval ─────────────────────────────────────────────────────

    [Fact]
    public void PauseForApproval_ReturnsApprovalRequiredDecision()
    {
        var d = AgentToolDecision.PauseForApproval("needs approval");
        var approval = Assert.IsType<AgentToolDecision.ApprovalRequiredDecision>(d);
        Assert.Equal("needs approval", approval.Description);
        Assert.Null(approval.Metadata);
    }

    [Fact]
    public void PauseForApproval_WithMetadata_ReturnsMetadata()
    {
        var meta = new Dictionary<string, string> { ["risk"] = "high" };
        var d = AgentToolDecision.PauseForApproval("needs review", meta);
        var approval = Assert.IsType<AgentToolDecision.ApprovalRequiredDecision>(d);
        Assert.Same(meta, approval.Metadata);
    }

    [Fact]
    public void PauseForApproval_NullOrEmptyDescription_Throws()
    {
        Assert.Throws<ArgumentException>(() => AgentToolDecision.PauseForApproval(string.Empty));
        Assert.Throws<ArgumentException>(() => AgentToolDecision.PauseForApproval(""));
    }

    // ── Skip ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Skip_ReturnsSyntheticResult()
    {
        var d = AgentToolDecision.Skip("cached result");
        var skip = Assert.IsType<AgentToolDecision.SkipDecision>(d);
        Assert.Equal("cached result", skip.SyntheticResult);
        Assert.Null(skip.Metadata);
    }

    [Fact]
    public void Skip_EmptyString_IsAllowed()
    {
        // Empty synthetic result is valid (tool produces no output).
        var d = AgentToolDecision.Skip(string.Empty);
        var skip = Assert.IsType<AgentToolDecision.SkipDecision>(d);
        Assert.Equal(string.Empty, skip.SyntheticResult);
    }

    [Fact]
    public void Skip_NullSyntheticResult_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AgentToolDecision.Skip(null!));
    }

    // ── Block ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Block_ReturnsBlockDecision()
    {
        var d = AgentToolDecision.Block("policy violation");
        var block = Assert.IsType<AgentToolDecision.BlockDecision>(d);
        Assert.Equal("policy violation", block.Reason);
        Assert.Null(block.Metadata);
    }

    [Fact]
    public void Block_NullOrEmptyReason_Throws()
    {
        Assert.Throws<ArgumentException>(() => AgentToolDecision.Block(string.Empty));
    }

    // ── Type discrimination ────────────────────────────────────────────────────

    [Fact]
    public void AllOutcomes_AreDistinctTypes()
    {
        AgentToolDecision proceed = AgentToolDecision.Proceed();
        AgentToolDecision approval = AgentToolDecision.PauseForApproval("desc");
        AgentToolDecision skip = AgentToolDecision.Skip("result");
        AgentToolDecision block = AgentToolDecision.Block("reason");

        Assert.IsType<AgentToolDecision.ProceedDecision>(proceed);
        Assert.IsType<AgentToolDecision.ApprovalRequiredDecision>(approval);
        Assert.IsType<AgentToolDecision.SkipDecision>(skip);
        Assert.IsType<AgentToolDecision.BlockDecision>(block);
    }
}
