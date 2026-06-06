using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Approvals;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Task 8.4 — Unit tests for <c>AgentWorkflow.EvaluateScopeNormalization</c>, the pure
/// static core of <c>NormalizeApprovalScopeForPersistence</c>.
///
/// Load-bearing behavioral guarantee 1: <c>NormalizeApprovalScopeForPersistence</c> must return
/// <see cref="ApprovalScope.ThisCallOnly"/> for ALL of: undefined integer scope, empty or
/// whitespace-only <c>Pattern</c>, malformed regex, whitespace <c>Parameter</c>.
/// </summary>
public class NormalizeScopeTests
{
    // Helper: build a DurableApprovalDecision with scope and pattern.
    private static DurableApprovalDecision Decision(
        ApprovalScope scope,
        ApprovalScopePattern? pattern = null) =>
        new DurableApprovalDecision
        {
            RequestId = "req-norm-test",
            Approved = true,
            Scope = scope,
            ScopePattern = pattern,
        };

    private static ApprovalScopePattern GlobPattern(string patternStr, string? parameter = "path") =>
        new ApprovalScopePattern { Type = PatternMatchType.Glob, Pattern = patternStr, Parameter = parameter };

    private static ApprovalScopePattern RegexPattern(string patternStr, string? parameter = "path") =>
        new ApprovalScopePattern { Type = PatternMatchType.Regex, Pattern = patternStr, Parameter = parameter };

    // ── Undefined ApprovalScope integer ─────────────────────────────────────

    [Fact]
    public void UndefinedIntegerScope_ReturnsThisCallOnly()
    {
        var decision = Decision((ApprovalScope)99);
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.ThisCallOnly, scope);
        Assert.NotNull(reason); // warning reason should be provided
    }

    // ── Session scope with empty/whitespace Pattern ──────────────────────────

    [Fact]
    public void Session_EmptyPattern_ReturnsThisCallOnly()
    {
        var pattern = new ApprovalScopePattern { Type = PatternMatchType.Glob, Pattern = "", Parameter = "path" };
        var decision = Decision(ApprovalScope.Session, pattern);
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.ThisCallOnly, scope);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Always_WhitespaceOnlyPattern_ReturnsThisCallOnly()
    {
        var pattern = new ApprovalScopePattern { Type = PatternMatchType.Glob, Pattern = "   ", Parameter = "path" };
        var decision = Decision(ApprovalScope.Always, pattern);
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.ThisCallOnly, scope);
        Assert.NotNull(reason);
    }

    // ── Whitespace-only Parameter ────────────────────────────────────────────

    [Fact]
    public void Session_WhitespaceOnlyParameter_ReturnsThisCallOnly()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Glob,
            Pattern = "/tmp/*",
            Parameter = "   ", // whitespace-only = invalid
        };
        var decision = Decision(ApprovalScope.Session, pattern);
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.ThisCallOnly, scope);
        Assert.NotNull(reason);
    }

    // ── Null Parameter is valid (wildcard match) ─────────────────────────────

    [Fact]
    public void Session_NullParameter_IsValidWildcard()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Glob,
            Pattern = "/tmp/*",
            Parameter = null, // null = wildcard over all args — valid
        };
        var decision = Decision(ApprovalScope.Session, pattern);
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.Session, scope);
        Assert.Null(reason); // no degradation
    }

    // ── Malformed Regex pattern ──────────────────────────────────────────────

    [Fact]
    public void Always_MalformedRegexPattern_ReturnsThisCallOnly()
    {
        var pattern = RegexPattern("[unclosed");
        var decision = Decision(ApprovalScope.Always, pattern);
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.ThisCallOnly, scope);
        Assert.NotNull(reason);
    }

    // ── Null ScopePattern → valid wildcard ──────────────────────────────────

    [Fact]
    public void Session_NullScopePattern_ReturnsSession()
    {
        var decision = Decision(ApprovalScope.Session, pattern: null);
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.Session, scope);
        Assert.Null(reason);
    }

    [Fact]
    public void Always_NullScopePattern_ReturnsAlways()
    {
        var decision = Decision(ApprovalScope.Always, pattern: null);
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.Always, scope);
        Assert.Null(reason);
    }

    // ── Valid patterns pass through unchanged ────────────────────────────────

    [Fact]
    public void Session_ValidGlobPattern_ReturnsSession()
    {
        var decision = Decision(ApprovalScope.Session, GlobPattern("/tmp/*"));
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.Session, scope);
        Assert.Null(reason);
    }

    [Fact]
    public void Always_ValidGlobPattern_ReturnsAlways()
    {
        var decision = Decision(ApprovalScope.Always, GlobPattern("/tmp/*"));
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.Always, scope);
        Assert.Null(reason);
    }

    // ── ThisCallOnly scope always returns ThisCallOnly ───────────────────────

    [Fact]
    public void ThisCallOnly_ReturnsThisCallOnly_RegardlessOfScopePattern()
    {
        // Even if a ScopePattern is provided (unusual), ThisCallOnly always returns itself.
        var decision = Decision(ApprovalScope.ThisCallOnly, GlobPattern("/tmp/*"));
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.ThisCallOnly, scope);
        Assert.Null(reason);
    }

    // ── Undefined PatternMatchType integer ──────────────────────────────────

    [Fact]
    public void Session_UndefinedPatternMatchType_ReturnsThisCallOnly()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = (PatternMatchType)99,
            Pattern = "/tmp/*",
            Parameter = "path",
        };
        var decision = Decision(ApprovalScope.Session, pattern);
        var (scope, reason) = AgentWorkflow.EvaluateScopeNormalization(decision);

        Assert.Equal(ApprovalScope.ThisCallOnly, scope);
        Assert.NotNull(reason);
    }
}
