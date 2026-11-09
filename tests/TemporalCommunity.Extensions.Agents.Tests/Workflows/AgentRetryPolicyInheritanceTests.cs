using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Tools;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Common;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

public sealed class AgentRetryPolicyInheritanceTests
{
    [Theory]
    [InlineData(null, null, 1)]
    [InlineData(4, null, 1)]
    [InlineData(0, null, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(4, 1, 1)]
    public void WorkerAndAgentPolicies_AffectToolDispatchAsDocumented(
        int? workerAttempts,
        int? agentAttempts,
        int expectedToolAttempts)
    {
        var options = CreateOptions(workerAttempts, agentAttempts);
        var config = Resolve(options);

        Assert.Equal(expectedToolAttempts, config.ToolActivityOptions["lookup"].RetryPolicy!.MaximumAttempts);
        Assert.Equal(expectedToolAttempts, config.InterceptorActivityOptions!.RetryPolicy!.MaximumAttempts);
        Assert.Equal(expectedToolAttempts, config.InterceptorToolActivityOptions!["lookup"].RetryPolicy!.MaximumAttempts);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(3, null, true)]
    [InlineData(0, null, true)]
    [InlineData(null, 3, false)]
    [InlineData(null, 0, false)]
    public void PerToolNoRetry_ConflictsOnlyWithExplicitAgentPolicy(
        int? workerAttempts,
        int? agentAttempts,
        bool accepted)
    {
        var options = CreateOptions(workerAttempts, agentAttempts, tool => tool.NoRetry());

        if (accepted)
        {
            Assert.Equal(1, Resolve(options).ToolActivityOptions["lookup"].RetryPolicy!.MaximumAttempts);
            return;
        }

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(options));
        Assert.Contains("disables retries", exception.Message);
    }

    [Fact]
    public void PerToolRetry_RejectsIndependentRetryCount()
    {
        var options = CreateOptions(null, null, tool => tool.WithMaxAttempts(2));

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(options));

        Assert.Contains("per-tool retries", exception.Message);
    }

    private static TemporalAgentsOptions CreateOptions(
        int? workerAttempts,
        int? agentAttempts,
        Action<DurableToolOptions>? configureTool = null)
    {
        var options = new TemporalAgentsOptions
        {
            DefaultRetryPolicy = workerAttempts is { } attempts
                ? new RetryPolicy { MaximumAttempts = attempts }
                : null,
        };
        options.AddDurableAgent("RetryAgent", agent =>
        {
            agent.ChatClient = _ => new ScriptedChatClient(
                [new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))]);
            agent.RetryPolicy = agentAttempts is { } attempts
                ? new RetryPolicy { MaximumAttempts = attempts }
                : null;
            agent.AddTool(AIFunctionFactory.Create(() => "ok", "lookup"), tool =>
            {
                tool.WithInterceptorTimeout(TimeSpan.FromSeconds(10));
                configureTool?.Invoke(tool);
            });
            agent.AddToolInterceptor(_ => new ProceedInterceptor());
        });
        return options;
    }

    private static AgentWorkerToolConfig Resolve(TemporalAgentsOptions options) =>
        DefaultTemporalAgentClient.BuildWorkerAgentWorkflowInputCore(
            "RetryAgent",
            options,
            "agents").ResolvedWorkerConfig!;

    private sealed class ProceedInterceptor : IAgentToolInterceptor
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            AgentToolContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(DurableToolDecision.Proceed());
    }
}
