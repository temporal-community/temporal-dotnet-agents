using System.Reflection;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

/// <summary>
/// S-X-5 nit — pins the documented residual that an <em>unmapped</em> tool name falls back to
/// activity options carrying the configured <see cref="RetryPolicy"/> (so a non-idempotent
/// unregistered tool does not inherit Temporal's default unlimited-retry policy), and stays null
/// when no retry policy is configured.
///
/// <para>
/// Tested against the MAF <c>AgentWorkflow.ResolveDurableToolActivityOptions</c>, which is a pure
/// workflow-thread method (no <c>Workflow.Logger</c> / no Temporal context). The AI-side
/// <c>DurableChatWorkflow.ResolveToolActivityOptions</c> shares the same residual contract but
/// emits a <c>Workflow.Logger.LogWarning</c> on the unmapped branch, so it can only be exercised
/// inside a real workflow context — see the test report note. The behavior pinned here is the same
/// RetryPolicy-propagation residual on the unit-testable side.
/// </para>
///
/// <para>
/// <c>_input</c> is a private field set inside the workflow run loop; we seed it by reflection and
/// invoke the private method by reflection — pure computation, no server needed.
/// </para>
/// </summary>
public class ResolveDurableToolActivityOptionsTests
{
    private static ActivityOptions InvokeResolve(AgentWorkflowInput input, string toolName = "unmapped")
    {
        var workflow = new AgentWorkflow();

        var inputField = typeof(AgentWorkflow).GetField(
            "_input", BindingFlags.Instance | BindingFlags.NonPublic)!;
        inputField.SetValue(workflow, input);

        var method = typeof(AgentWorkflow).GetMethod(
            "ResolveDurableToolActivityOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;

        return (ActivityOptions)method.Invoke(workflow, [toolName])!;
    }

    [Fact]
    public void Resolve_UnmappedTool_WithRetryPolicy_CarriesConfiguredPolicy()
    {
        var retryPolicy = new RetryPolicy { MaximumAttempts = 1 };
        var input = new AgentWorkflowInput
        {
            AgentName = "TestAgent",
            TaskQueue = "tq",
            ActivityTimeout = TimeSpan.FromMinutes(3),
            HeartbeatTimeout = TimeSpan.FromMinutes(1),
            RetryPolicy = retryPolicy,
            // No ResolvedWorkerConfig → DurableAgentToolActivityOptions is null → fallback branch.
        };

        var resolved = InvokeResolve(input);

        Assert.NotNull(resolved.RetryPolicy);
        Assert.Equal(1, resolved.RetryPolicy!.MaximumAttempts);
        Assert.Equal(TimeSpan.FromMinutes(3), resolved.StartToCloseTimeout);
        Assert.Equal("unmapped", resolved.Summary);
    }

    [Fact]
    public void Resolve_UnmappedTool_NoRetryPolicy_StaysNull()
    {
        var input = new AgentWorkflowInput
        {
            AgentName = "TestAgent",
            TaskQueue = "tq",
            ActivityTimeout = TimeSpan.FromMinutes(5),
            HeartbeatTimeout = TimeSpan.FromMinutes(2),
            // No RetryPolicy configured.
        };

        var resolved = InvokeResolve(input);

        Assert.Null(resolved.RetryPolicy);
    }
}
