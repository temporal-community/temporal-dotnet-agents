using Temporalio.Extensions.AI;
using Temporalio.Workflows;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// Unit tests for <see cref="DurableToolDecisionPolicy"/> — the shared policy helpers used by
/// all four tool-interceptor dispatch-loop call sites (DurableChatWorkflow, AgentWorkflow,
/// AgentJobWorkflow, TemporalAIAgent).
/// </summary>
public class DurableToolDecisionPolicyTests
{
    // ── GetEffectiveOutcome ─────────────────────────────────────────────────

    [Fact]
    public void GetEffectiveOutcome_Proceed_NoRequiresApproval_ReturnsProceed()
    {
        var result = DurableToolDecisionPolicy.GetEffectiveOutcome(
            DurableToolOutcome.Proceed, "my-tool", requiresApprovalTools: null);

        Assert.Equal(DurableToolOutcome.Proceed, result);
    }

    [Fact]
    public void GetEffectiveOutcome_Proceed_ToolInRequiresList_ReturnsPauseForApproval()
    {
        var result = DurableToolDecisionPolicy.GetEffectiveOutcome(
            DurableToolOutcome.Proceed, "my-tool",
            requiresApprovalTools: ["my-tool", "other-tool"]);

        Assert.Equal(DurableToolOutcome.PauseForApproval, result);
    }

    [Fact]
    public void GetEffectiveOutcome_Skip_ToolInRequiresList_ReturnsPauseForApproval()
    {
        // Rule 2: even Skip is overridden to PauseForApproval when the tool is in the requires list.
        var result = DurableToolDecisionPolicy.GetEffectiveOutcome(
            DurableToolOutcome.Skip, "my-tool",
            requiresApprovalTools: ["my-tool"]);

        Assert.Equal(DurableToolOutcome.PauseForApproval, result);
    }

    [Fact]
    public void GetEffectiveOutcome_PauseForApproval_ToolInRequiresList_StaysPauseForApproval()
    {
        var result = DurableToolDecisionPolicy.GetEffectiveOutcome(
            DurableToolOutcome.PauseForApproval, "my-tool",
            requiresApprovalTools: ["my-tool"]);

        Assert.Equal(DurableToolOutcome.PauseForApproval, result);
    }

    [Fact]
    public void GetEffectiveOutcome_Block_ToolInRequiresList_StaysBlock()
    {
        // Block is NEVER overridden — this is the load-bearing BLOCK-3 invariant.
        var result = DurableToolDecisionPolicy.GetEffectiveOutcome(
            DurableToolOutcome.Block, "my-tool",
            requiresApprovalTools: ["my-tool"]);

        Assert.Equal(DurableToolOutcome.Block, result);
    }

    [Fact]
    public void GetEffectiveOutcome_NullOutcome_ToolInRequiresList_ReturnsPauseForApproval()
    {
        // null interceptor outcome → defaults to Proceed → overridden by require-approval floor.
        var result = DurableToolDecisionPolicy.GetEffectiveOutcome(
            interceptorOutcome: null, "my-tool",
            requiresApprovalTools: ["my-tool"]);

        Assert.Equal(DurableToolOutcome.PauseForApproval, result);
    }

    // ── GetEffectiveOutcome: Rule 2 exclusion (scope-aware required tools) ──

    [Fact]
    public void GetEffectiveOutcome_ScopeAwareRequiredTool_AbsentFromRequiresList_Proceed_ReturnsProceed()
    {
        // Load-bearing guarantee 2: scope-aware required tools are NOT in requiresApprovalTools.
        // When the tool is absent from the requires list, Rule 2 does not fire.
        var result = DurableToolDecisionPolicy.GetEffectiveOutcome(
            DurableToolOutcome.Proceed, "WriteFile",
            requiresApprovalTools: []); // scope-aware tool excluded from this list

        Assert.Equal(DurableToolOutcome.Proceed, result);
    }

    [Fact]
    public void GetEffectiveOutcome_ScopeAwareRequiredTool_AbsentFromRequiresList_PauseForApproval_StaysPauseForApproval()
    {
        // Interceptor already said PauseForApproval; Rule 2 is irrelevant, but
        // still must not fire spuriously when the tool is absent from requires list.
        var result = DurableToolDecisionPolicy.GetEffectiveOutcome(
            DurableToolOutcome.PauseForApproval, "WriteFile",
            requiresApprovalTools: []); // scope-aware tool excluded from this list

        Assert.Equal(DurableToolOutcome.PauseForApproval, result);
    }

    [Fact]
    public void GetEffectiveOutcome_NonScopeAwareRequiredTool_InRequiresList_Proceed_ReturnsPauseForApproval()
    {
        // Non-scope-aware required tool in requiresApprovalTools → Rule 2 unchanged.
        var result = DurableToolDecisionPolicy.GetEffectiveOutcome(
            DurableToolOutcome.Proceed, "ToolX",
            requiresApprovalTools: ["ToolX"]);

        Assert.Equal(DurableToolOutcome.PauseForApproval, result);
    }

    // ── IsToolSkipped ──────────────────────────────────────────────────────

    [Fact]
    public void IsToolSkipped_ExactMatch_ReturnsTrue()
    {
        var result = DurableToolDecisionPolicy.IsToolSkipped("my-tool", ["my-tool", "other"]);

        Assert.True(result);
    }

    [Fact]
    public void IsToolSkipped_CaseInsensitiveMatch_ReturnsTrue()
    {
        var result = DurableToolDecisionPolicy.IsToolSkipped("MY-TOOL", ["my-tool"]);

        Assert.True(result);
    }

    [Fact]
    public void IsToolSkipped_NullList_ReturnsFalse()
    {
        var result = DurableToolDecisionPolicy.IsToolSkipped("my-tool", skippedTools: null);

        Assert.False(result);
    }

    [Fact]
    public void IsToolSkipped_NotInList_ReturnsFalse()
    {
        var result = DurableToolDecisionPolicy.IsToolSkipped("absent", ["my-tool", "other"]);

        Assert.False(result);
    }

    // ── ResolveInterceptorActivityOptions ─────────────────────────────────

    [Fact]
    public void ResolveInterceptorActivityOptions_PerToolEntryExists_UsePerToolBase()
    {
        var shared = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(5) };
        var perTool = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) };
        var perToolMap = new Dictionary<string, ActivityOptions> { ["my-tool"] = perTool };

        var result = DurableToolDecisionPolicy.ResolveInterceptorActivityOptions("my-tool", shared, perToolMap);

        Assert.Equal($"intercept:my-tool", result.Summary);
        Assert.Equal(TimeSpan.FromSeconds(30), result.StartToCloseTimeout);
    }

    [Fact]
    public void ResolveInterceptorActivityOptions_NoPerToolEntry_UseSharedBase()
    {
        var shared = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) };

        var result = DurableToolDecisionPolicy.ResolveInterceptorActivityOptions("absent-tool", shared, perToolOptions: null);

        Assert.Equal("intercept:absent-tool", result.Summary);
        Assert.Equal(TimeSpan.FromSeconds(10), result.StartToCloseTimeout);
    }

    [Fact]
    public void ResolveInterceptorActivityOptions_ReturnsNewReference()
    {
        var shared = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(5) };
        var perTool = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) };
        var perToolMap = new Dictionary<string, ActivityOptions> { ["my-tool"] = perTool };

        var result = DurableToolDecisionPolicy.ResolveInterceptorActivityOptions("my-tool", shared, perToolMap);

        // Result must not be either input object — a fresh allocation.
        Assert.NotSame(shared, result);
        Assert.NotSame(perTool, result);
    }

    // ── GetEffectiveArguments ─────────────────────────────────────────────

    [Fact]
    public void GetEffectiveArguments_ModifiedArgsPresent_ReturnsModifiedArgs()
    {
        var modified = new Dictionary<string, object?> { ["x"] = 1 };
        var original = new Dictionary<string, object?> { ["x"] = 99, ["y"] = 2 };

        var result = DurableToolDecisionPolicy.GetEffectiveArguments(modified, original);

        Assert.Same(modified, result);
    }

    [Fact]
    public void GetEffectiveArguments_NoModifiedArgs_ReturnsFreshCopyOfOriginal()
    {
        var original = new Dictionary<string, object?> { ["key"] = "value" };

        var result = DurableToolDecisionPolicy.GetEffectiveArguments(
            interceptorModifiedArgs: null, original);

        Assert.NotNull(result);
        Assert.NotSame(original, result);
        Assert.Equal("value", result["key"]);
    }

    [Fact]
    public void GetEffectiveArguments_BothNull_ReturnsNull()
    {
        var result = DurableToolDecisionPolicy.GetEffectiveArguments(
            interceptorModifiedArgs: null, originalArgs: null);

        Assert.Null(result);
    }

    // ── GetApprovalDescription ─────────────────────────────────────────────

    [Fact]
    public void GetApprovalDescription_NullResult_ReturnsDefaultMessage()
    {
        var result = DurableToolDecisionPolicy.GetApprovalDescription(result: null, "my-tool");

        Assert.Equal("Approve invocation of tool 'my-tool'", result);
    }

    [Fact]
    public void GetApprovalDescription_NonNullEnrichedDescription_ReturnsEnrichedDescription()
    {
        var interceptorResult = new DurableToolInterceptorResult
        {
            Outcome = DurableToolOutcome.PauseForApproval,
            EnrichedDescription = "Enriched: this tool will delete records",
        };

        var result = DurableToolDecisionPolicy.GetApprovalDescription(interceptorResult, "my-tool");

        Assert.Equal("Enriched: this tool will delete records", result);
    }

    [Fact]
    public void GetApprovalDescription_EmptyEnrichedDescription_ReturnsEmpty()
    {
        // Empty string is NOT null — ?? only coalesces null, so the empty string is preserved.
        var interceptorResult = new DurableToolInterceptorResult
        {
            Outcome = DurableToolOutcome.PauseForApproval,
            EnrichedDescription = string.Empty,
        };

        var result = DurableToolDecisionPolicy.GetApprovalDescription(interceptorResult, "my-tool");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetApprovalDescription_NullEnrichedDescription_NonNullMessage_ReturnsMessage()
    {
        var interceptorResult = new DurableToolInterceptorResult
        {
            Outcome = DurableToolOutcome.PauseForApproval,
            EnrichedDescription = null,
            Message = "Please review this sensitive operation",
        };

        var result = DurableToolDecisionPolicy.GetApprovalDescription(interceptorResult, "my-tool");

        Assert.Equal("Please review this sensitive operation", result);
    }

    [Fact]
    public void GetApprovalDescription_BothNull_ReturnsDefault()
    {
        var interceptorResult = new DurableToolInterceptorResult
        {
            Outcome = DurableToolOutcome.PauseForApproval,
            EnrichedDescription = null,
            Message = null,
        };

        var result = DurableToolDecisionPolicy.GetApprovalDescription(interceptorResult, "my-tool");

        Assert.Equal("Approve invocation of tool 'my-tool'", result);
    }

    // ── Message formatters ─────────────────────────────────────────────────

    [Fact]
    public void SkipMessage_NullMessage_ReturnsEmpty()
    {
        var result = DurableToolDecisionPolicy.SkipMessage(interceptorMessage: null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SkipMessage_NonNullMessage_ReturnsMessage()
    {
        var result = DurableToolDecisionPolicy.SkipMessage("cached result from prior call");

        Assert.Equal("cached result from prior call", result);
    }

    [Fact]
    public void BlockMessage_NullMessage_ReturnsFallback()
    {
        var result = DurableToolDecisionPolicy.BlockMessage(interceptorMessage: null);

        Assert.Equal("[Blocked] Tool execution was blocked.", result);
    }

    [Fact]
    public void BlockMessage_NonNullMessage_ReturnsPrefixedMessage()
    {
        var result = DurableToolDecisionPolicy.BlockMessage("policy violation detected");

        Assert.Equal("[Blocked] policy violation detected", result);
    }

    [Fact]
    public void DenialMessage_ReturnsExpectedFormat()
    {
        var result = DurableToolDecisionPolicy.DenialMessage("denied by reviewer");

        Assert.Equal("[Denied] denied by reviewer", result);
    }
}
