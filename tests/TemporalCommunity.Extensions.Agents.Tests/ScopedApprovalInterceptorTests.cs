using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Tools;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Unit tests for <see cref="ScopedApprovalInterceptor"/> — the built-in interceptor
/// installed by <c>UseApprovalScopes()</c>.
///
/// These tests cover the decision priority stack (spec Section 5) without requiring a
/// workflow environment. Workflow-level tests (StateBag round-trip after approval,
/// CAN survival, always-scope dispatch) live in the integration test project.
/// </summary>
public class ScopedApprovalInterceptorTests
{
    private static ApprovalScopesOptions DefaultOpts() => new ApprovalScopesOptions();

    private static AgentToolContext MakeContext(
        string toolName,
        bool scopeAware,
        bool requiresApproval,
        AgentSessionStateBag? stateBag = null,
        Dictionary<string, object?>? arguments = null) =>
        new AgentToolContext
        {
            AgentName = "TestAgent",
            ToolName = toolName,
            ScopeAware = scopeAware,
            RequiresApproval = requiresApproval,
            StateBag = stateBag,
            Arguments = arguments ?? new Dictionary<string, object?>(),
            CallId = "call-1",
        };

    private static AgentSessionStateBag BagWithSessionScope(
        string toolName,
        string? requestId = null)
    {
        var record = new ApprovalScopeRecord
        {
            ToolName = toolName,
            GrantedAt = DateTimeOffset.UtcNow,
            OriginatingRequestId = requestId ?? Guid.NewGuid().ToString("N"),
        };

        var bag = new AgentSessionStateBag();
        bag.SetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.session",
            [record],
            TemporalAgentJsonUtilities.DefaultOptions);

        var serialized = bag.Serialize();
        return AgentSessionStateBag.Deserialize(serialized);
    }

    // ── Non-scope-aware tool → immediate Proceed ─────────────────────────────

    [Fact]
    public async Task NonScopeAwareTool_ReturnsProceed_Immediately()
    {
        var interceptor = new ScopedApprovalInterceptor(DefaultOpts());
        var ctx = MakeContext("WriteFile", scopeAware: false, requiresApproval: true);

        var decision = await interceptor.BeforeToolCallAsync(ctx, CancellationToken.None);

        Assert.IsType<DurableToolDecision.ProceedDecision>(decision);
    }

    // ── Scope-aware with null StateBag and RequiresApproval → PauseForApproval ─

    [Fact]
    public async Task ScopeAwareRequired_NullStateBag_ReturnsPauseForApproval()
    {
        // Spec (Task 8.8): ScopedApprovalInterceptor with null StateBag → PauseForApproval
        // for scope-aware required tool (no scope record can be found).
        var interceptor = new ScopedApprovalInterceptor(DefaultOpts());
        var ctx = MakeContext("WriteFile", scopeAware: true, requiresApproval: true, stateBag: null);

        var decision = await interceptor.BeforeToolCallAsync(ctx, CancellationToken.None);

        Assert.IsType<DurableToolDecision.ApprovalRequiredDecision>(decision);
    }

    [Fact]
    public async Task ScopeAwareRequired_EmptyStateBag_ReturnsPauseForApproval()
    {
        // Empty StateBag (no scope records) → same behavior as null.
        var interceptor = new ScopedApprovalInterceptor(DefaultOpts());
        var ctx = MakeContext("WriteFile", scopeAware: true, requiresApproval: true,
            stateBag: new AgentSessionStateBag());

        var decision = await interceptor.BeforeToolCallAsync(ctx, CancellationToken.None);

        Assert.IsType<DurableToolDecision.ApprovalRequiredDecision>(decision);
    }

    // ── Scope-aware with matching session scope → Proceed ────────────────────

    [Fact]
    public async Task ScopeAwareRequired_MatchingSessionScope_ReturnsProceed()
    {
        var bag = BagWithSessionScope("WriteFile");
        var interceptor = new ScopedApprovalInterceptor(DefaultOpts());
        var ctx = MakeContext("WriteFile", scopeAware: true, requiresApproval: true, stateBag: bag);

        var decision = await interceptor.BeforeToolCallAsync(ctx, CancellationToken.None);

        var proceed = Assert.IsType<DurableToolDecision.ProceedDecision>(decision);
        // Enriched description confirms auto-approval path.
        Assert.Contains("session scope", proceed.EnrichedDescription, StringComparison.OrdinalIgnoreCase);
    }

    // ── Scope-aware without RequiresApproval → Proceed even without scope ────

    [Fact]
    public async Task ScopeAwareNotRequired_NoScope_ReturnsProceed()
    {
        // Not required → scope is optional; interceptor should not block the call.
        var interceptor = new ScopedApprovalInterceptor(DefaultOpts());
        var ctx = MakeContext("ReadFile", scopeAware: true, requiresApproval: false, stateBag: null);

        var decision = await interceptor.BeforeToolCallAsync(ctx, CancellationToken.None);

        Assert.IsType<DurableToolDecision.ProceedDecision>(decision);
    }

    // ── Case-insensitive tool name matching ─────────────────────────────────

    [Fact]
    public async Task ScopeAwareRequired_ToolNameCaseInsensitive_Matches()
    {
        // Store has "WriteFile", context uses "writefile" — must match.
        var bag = BagWithSessionScope("WriteFile");
        var interceptor = new ScopedApprovalInterceptor(DefaultOpts());
        var ctx = MakeContext("writefile", scopeAware: true, requiresApproval: true, stateBag: bag);

        var decision = await interceptor.BeforeToolCallAsync(ctx, CancellationToken.None);

        Assert.IsType<DurableToolDecision.ProceedDecision>(decision);
    }
}
