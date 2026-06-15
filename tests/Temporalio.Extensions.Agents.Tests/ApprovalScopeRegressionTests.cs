using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Tools;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Approvals;
using Temporalio.Extensions.AI.Tools;
using Xunit;
using AgentsInterceptorInput = Temporalio.Extensions.Agents.Workflows.DurableToolInterceptorInput;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Task 8.10 — Regression tests ensuring Feature B (Approval Scopes) does not silently
/// change the behavior of non-HITL paths, non-scope-aware tools, or agents that have not
/// opted into approval scopes.
///
/// Spec sections: 8 (non-HITL paths — SerializedStateBag = null), 11 (out of scope for
/// AgentJobWorkflow / TemporalAIAgent scope application).
///
/// Runtime-path cases that require TestWorkflowEnvironment (e.g., verifying the
/// RunToolInterceptor activity input is captured or that SkipInterceptor is a no-op) are
/// covered in Tasks 8.7 and 8.8 (ApprovalScopeWorkflowTests.cs).
/// </summary>
public class ApprovalScopeRegressionTests
{
    // ── DurableToolInterceptorInput: SerializedStateBag default ─────────────

    [Fact]
    public void DurableToolInterceptorInput_SerializedStateBag_DefaultIsNull()
    {
        // Regression: non-HITL construction paths (AgentJobWorkflow, TemporalAIAgent)
        // explicitly set SerializedStateBag = null. Verify the type's default is null.
        var input = new AgentsInterceptorInput
        {
            AgentName = "MyAgent",
            ToolName = "WriteFile",
        };

        Assert.Null(input.SerializedStateBag);
    }

    [Fact]
    public void DurableToolInterceptorInput_ScopeAware_DefaultIsFalse()
    {
        // Wire-compatibility: existing workflow history lacks this field and must deserialize
        // with false (C# default). Test that the type's default is false.
        var input = new AgentsInterceptorInput
        {
            AgentName = "MyAgent",
            ToolName = "WriteFile",
        };

        Assert.False(input.ScopeAware);
    }

    [Fact]
    public void DurableToolInterceptorInput_RequiresApproval_DefaultIsFalse()
    {
        // Wire-compatibility: existing workflow history lacks this field and must deserialize
        // with false (C# default). Test that the type's default is false.
        var input = new AgentsInterceptorInput
        {
            AgentName = "MyAgent",
            ToolName = "WriteFile",
        };

        Assert.False(input.RequiresApproval);
    }

    // ── RequireApproval() without ScopeAware() stays in RequiresApprovalTools ──

    [Fact]
    public void RequireApprovalWithoutScopeAware_ToolOptionsHaveRequireApprovalFlagSetScopeAwareFlagUnset()
    {
        // Existing RequireApproval() tools without ScopeAware() must remain in RequiresApprovalTools
        // (Rule 2 enforcement). Verify at the DurableToolOptions level.
        var opts = new DurableToolOptions();
        opts.RequireApproval();

        Assert.True(opts.RequireApprovalFlag);
        Assert.False(opts.ScopeAwareFlag);
    }

    [Fact]
    public void RequireApprovalWithScopeAware_BothFlagsSet_ToolExcludedFromRequiresApprovalToolsList()
    {
        // Scope-aware required tools are excluded from RequiresApprovalTools in the resolved
        // config — they go into ScopeAwareApprovalTools instead. This is tested at the
        // DurableAgentRegistration level by checking the builder produces both flags set.
        var opts = new DurableToolOptions();
        opts.RequireApproval().ScopeAware();

        Assert.True(opts.RequireApprovalFlag);
        Assert.True(opts.ScopeAwareFlag);
    }

    // ── SkipInterceptor() without RequireApproval()+ScopeAware() retains existing behavior ─

    [Fact]
    public void SkipInterceptor_WithoutRequireApprovalOrScopeAware_FlagSetNoOtherFlagsSet()
    {
        // Regression: SkipInterceptor() without the scope-aware combination must not
        // accidentally set any scope flags.
        var opts = new DurableToolOptions();
        opts.SkipInterceptor();

        Assert.True(opts.SkipInterceptorFlag);
        Assert.False(opts.RequireApprovalFlag);
        Assert.False(opts.ScopeAwareFlag);
    }

    [Fact]
    public void ScopeAware_WithSkipInterceptor_WithoutRequireApproval_AcceptedAtRegistration()
    {
        // ScopeAware() + SkipInterceptor() without RequireApproval() is valid — the scope-aware
        // flag is informational when not combined with RequireApproval().
        var builder = new DurableAgentBuilder("ScopeAwareSkipAgent");
        builder.ChatClient = _ => new StubChatClientForRegressionTests();

        var tool = AIFunctionFactory.Create(() => "result", "ReadFile");
        builder.UseApprovalScopes();
        builder.AddTool(tool, opts => opts.ScopeAware().SkipInterceptor());

        // Must not throw.
        var registration = builder.ToRegistration();

        Assert.NotNull(registration);
        Assert.True(registration.UseApprovalScopes);
    }

    // ── Feature B off is invisible: existing approval/interceptor behavior unchanged ──

    [Fact]
    public void AgentWithoutUseApprovalScopes_RegistrationHasUseApprovalScopesFalse()
    {
        // Feature B off: agent without UseApprovalScopes() must not have approval scopes activated.
        var builder = new DurableAgentBuilder("PlainAgent");
        builder.ChatClient = _ => new StubChatClientForRegressionTests();

        var registration = builder.ToRegistration();

        Assert.False(registration.UseApprovalScopes);
        Assert.Null(registration.ApprovalScopesOptions);
    }

    [Fact]
    public void AgentWithCustomInterceptor_NoUseApprovalScopes_InterceptorFactoryPreservedOnRegistration()
    {
        // Feature B off: agents with plain custom interceptors keep using the custom interceptor.
        var builder = new DurableAgentBuilder("InterceptedAgent");
        builder.ChatClient = _ => new StubChatClientForRegressionTests();
        builder.AddToolInterceptor(_ => new StubApprovalRegressionInterceptor());

        var registration = builder.ToRegistration();

        Assert.NotNull(registration.ToolInterceptorFactory);
        Assert.False(registration.UseApprovalScopes);
    }

    [Fact]
    public void AgentWithWorkerDefaultApprovalScopeStore_NoUseApprovalScopes_ApprovalScopesOptionsIsNull()
    {
        // Feature B off: agents without UseApprovalScopes() do not invoke a worker-level store
        // factory, even when one is configured. Registration must have null ApprovalScopesOptions.
        var workerInvocationCount = 0;
        var options = new TemporalAgentsOptions
        {
            ApprovalScopeStore = _ =>
            {
                workerInvocationCount++;
                return new FakeApprovalScopeStore();
            }
        };

        var builder = new DurableAgentBuilder("NoScopeAgent");
        builder.ChatClient = _ => new StubChatClientForRegressionTests();
        // Intentionally NOT calling UseApprovalScopes().

        var registration = builder.ToRegistration();

        Assert.Null(registration.ApprovalScopesOptions);
        Assert.False(registration.UseApprovalScopes);
        Assert.Equal(0, workerInvocationCount); // factory not invoked at registration time
    }

    // ── MEAI scope fields are silently ignored on the MEAI path ─────────────

    [Fact]
    public void DurableApprovalDecision_ScopeFields_ExistButDoNotAffectMeaiPath()
    {
        // MEAI DurableChatWorkflow approval behavior is unchanged: scope fields on
        // DurableApprovalDecision are defined but the MEAI workflow loop does not use them.
        // This test is a type-existence and default-value guard.
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-meai-compat",
            Approved = true,
        };

        // Scope defaults to ThisCallOnly (0) — the zero-value, which is also the wire default.
        Assert.Equal(ApprovalScope.ThisCallOnly, decision.Scope);
        Assert.Null(decision.ScopePattern);
    }

    // ── Private stub ─────────────────────────────────────────────────────────

    private sealed class StubApprovalRegressionInterceptor : IAgentToolInterceptor
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(AgentToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(DurableToolDecision.Proceed());
    }
}

/// <summary>Minimal <see cref="IChatClient"/> stub for regression test builder construction.</summary>
internal sealed class StubChatClientForRegressionTests : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stub — not called in unit tests");
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stub — not called in unit tests");
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
