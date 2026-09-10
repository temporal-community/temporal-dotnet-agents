using Microsoft.Extensions.AI;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

/// <summary>
/// Pins the retry-policy precedence for fire-and-forget agent jobs:
/// <c>OneTimeAgentRun.RetryPolicy</c> → per-agent <c>RetryPolicy</c> → worker
/// <c>DefaultRetryPolicy</c> → the bounded five-attempt backstop.
///
/// <para>
/// <c>OneTimeAgentRun.RetryPolicy</c> was a public property that nothing read:
/// <c>ScheduleActivities</c> built its job input without passing it, so a caller who set it got
/// the worker default and no diagnostic. These tests pin the precedence but <b>not</b> that
/// wiring — they call the builder directly, and pass even with the argument removed from
/// <c>ScheduleActivities</c> again. <c>ScheduledJobTests.ScheduleOneTime_PerRunRetryPolicy_ReachesTheModelStepActivity</c>
/// is the one that fails in that case.
/// </para>
///
/// <para>
/// <c>BuildAgentJobInput</c> is pure — no client, no server — so this is a unit test even though
/// the scheduling paths around it are covered by integration tests.
/// </para>
/// </summary>
public class AgentJobInputRetryPolicyTests
{
    private const string TaskQueue = "test-queue";
    private const string AgentName = "JobAgent";

    private static TemporalAgentsOptions OptionsWith(
        RetryPolicy? workerDefault = null,
        RetryPolicy? perAgent = null)
    {
        var options = new TemporalAgentsOptions { DefaultRetryPolicy = workerDefault };
        options.AddDurableAgent(AgentName, agent =>
        {
            agent.ChatClient = _ => new NoopChatClient();
            if (perAgent is not null)
            {
                agent.RetryPolicy = perAgent;
            }
        });

        return options;
    }

    private static AgentJobInput Build(TemporalAgentsOptions options, RetryPolicy? perRun) =>
        DefaultTemporalAgentClient.BuildAgentJobInput(
            AgentName, new RunRequest("go"), options, TaskQueue, perRun);

    [Fact]
    public void PerRunRetryPolicy_OutranksTheWorkerDefault()
    {
        var input = Build(
            OptionsWith(workerDefault: new RetryPolicy { MaximumAttempts = 9 }),
            perRun: new RetryPolicy { MaximumAttempts = 2 });

        Assert.Equal(2, input.RetryPolicy!.MaximumAttempts);
    }

    [Fact]
    public void PerRunRetryPolicy_OutranksThePerAgentPolicy()
    {
        // The per-agent policy is the one a reader is most likely to assume wins, since it is the
        // more specific of the two configured values. The per-run value is more specific still.
        var input = Build(
            OptionsWith(
                workerDefault: new RetryPolicy { MaximumAttempts = 9 },
                perAgent: new RetryPolicy { MaximumAttempts = 7 }),
            perRun: new RetryPolicy { MaximumAttempts = 1 });

        Assert.Equal(1, input.RetryPolicy!.MaximumAttempts);
    }

    [Fact]
    public void NoPerRunPolicy_FallsBackToThePerAgentPolicy()
    {
        var input = Build(
            OptionsWith(
                workerDefault: new RetryPolicy { MaximumAttempts = 9 },
                perAgent: new RetryPolicy { MaximumAttempts = 7 }),
            perRun: null);

        Assert.Equal(7, input.RetryPolicy!.MaximumAttempts);
    }

    [Fact]
    public void NothingConfigured_FallsBackToTheBoundedBackstop()
    {
        // Not Temporal's server default of 0 (unlimited) — the library substitutes a bounded five.
        var input = Build(OptionsWith(), perRun: null);

        Assert.Equal(5, input.RetryPolicy!.MaximumAttempts);
    }

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not invoked — registration only.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not invoked — registration only.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
