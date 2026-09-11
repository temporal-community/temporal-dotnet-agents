using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;
using static TemporalCommunity.Extensions.Agents.WorkflowAgents;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Retry-policy contract for the sub-agent LLM step dispatched by <see cref="TemporalAIAgent"/>
/// (the <c>WorkflowAgents.GetTemporalAgent</c> path).
///
/// <para>
/// <c>WorkflowAgents.GetTemporalAgent(name)</c> defaults <c>activityOptions</c> to
/// <see langword="null"/>, so <see cref="TemporalAIAgent"/> builds its own. That default left
/// <c>RetryPolicy</c> unset, and the server reads an unset policy as <c>MaximumAttempts = 0</c> —
/// unlimited retries. A deterministically-failing LLM step therefore retried forever and hung the
/// orchestrating workflow, which is exactly what the library's bounded default exists to prevent.
/// </para>
///
/// <para>
/// These tests assert the policy that actually reaches the server (read off the
/// <c>ActivityTaskScheduled</c> history event) plus the observable attempt count, not the in-memory
/// options object.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class SubAgentRetryPolicyTests
{
    private const string RunDurableAgentStepActivity =
        "TemporalCommunity.Extensions.Agents.RunDurableAgentStep";

    /// <summary>Bounded model default from <c>DefaultRetryPolicy.ResolveForModel</c>.</summary>
    private const int BoundedModelMaximumAttempts = 5;

    private static readonly TimeSpan BoundedModelMaximumInterval = TimeSpan.FromSeconds(2);

    private readonly ITestOutputHelper _output;

    public SubAgentRetryPolicyTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The headline assertion: the library-built default for a sub-agent LLM step carries the
    /// bounded model policy (5 attempts, 2s maximum backoff) rather than a null policy the server
    /// would expand into unlimited retries.
    /// </summary>
    [Fact]
    public async Task SubAgent_DefaultActivityOptions_ScheduleBoundedModelRetryPolicy()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var taskQueue = $"subagent-retry-default-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, new EchoChatClient(), taskQueue);
        await host.StartAsync();
        try
        {
            var handle = await env.Client.StartWorkflowAsync(
                (DefaultOptionsSubAgentWorkflow wf) => wf.RunAsync("hello"),
                new WorkflowOptions($"subagent-retry-default-{Guid.NewGuid():N}", taskQueue));
            await handle.GetResultAsync();

            var scheduled = await FindFirstStepScheduleAsync(handle);
            Assert.NotNull(scheduled);
            Assert.NotNull(scheduled!.RetryPolicy);
            _output.WriteLine(
                $"Scheduled RunDurableAgentStep retry policy: {scheduled.RetryPolicy}");

            // MaximumAttempts = 0 here would mean "unlimited" — the hang this fix removes.
            Assert.Equal(BoundedModelMaximumAttempts, scheduled.RetryPolicy.MaximumAttempts);
            Assert.Equal(
                BoundedModelMaximumInterval,
                scheduled.RetryPolicy.MaximumInterval.ToTimeSpan());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// A caller-supplied <see cref="ActivityOptions"/> still wins untouched — the bounded default
    /// is only a fallback for the options the library builds itself.
    /// </summary>
    [Fact]
    public async Task SubAgent_CallerSuppliedRetryPolicy_IsScheduledVerbatim()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var taskQueue = $"subagent-retry-caller-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, new EchoChatClient(), taskQueue);
        await host.StartAsync();
        try
        {
            var handle = await env.Client.StartWorkflowAsync(
                (CallerOptionsSubAgentWorkflow wf) => wf.RunAsync("hello"),
                new WorkflowOptions($"subagent-retry-caller-{Guid.NewGuid():N}", taskQueue));
            await handle.GetResultAsync();

            var scheduled = await FindFirstStepScheduleAsync(handle);
            Assert.NotNull(scheduled);
            Assert.NotNull(scheduled!.RetryPolicy);

            // Values from CallerOptionsSubAgentWorkflow, not the library default.
            Assert.Equal(
                CallerOptionsSubAgentWorkflow.CallerMaximumAttempts,
                scheduled.RetryPolicy.MaximumAttempts);
            Assert.Equal(
                CallerOptionsSubAgentWorkflow.CallerMaximumInterval,
                scheduled.RetryPolicy.MaximumInterval.ToTimeSpan());
            Assert.Equal(
                CallerOptionsSubAgentWorkflow.CallerStartToCloseTimeout,
                scheduled.StartToCloseTimeout.ToTimeSpan());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// The behaviour the history assertion stands in for: an LLM error the classifier cannot
    /// positively identify as non-retryable terminates the workflow after the bounded number of
    /// attempts instead of retrying forever. The bounded wait is deliberate — with an unbounded
    /// policy this call never returns, so the test must fail on a deadline rather than hang.
    /// </summary>
    [Fact]
    public async Task SubAgent_AlwaysFailingLlmStep_TerminatesAfterBoundedAttempts()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        // Never succeeds, and the failure carries no HTTP status, so LlmErrorClassifier cannot
        // fail it fast — only the retry policy can stop it.
        var chatClient = new FailThenSucceedChatClient(int.MaxValue);

        var taskQueue = $"subagent-retry-hang-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, chatClient, taskQueue);
        await host.StartAsync();
        try
        {
            var handle = await env.Client.StartWorkflowAsync(
                (DefaultOptionsSubAgentWorkflow wf) => wf.RunAsync("hello"),
                new WorkflowOptions($"subagent-retry-hang-{Guid.NewGuid():N}", taskQueue));

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await Assert.ThrowsAsync<WorkflowFailedException>(() =>
                handle.GetResultAsync(
                    rpcOptions: new RpcOptions { CancellationToken = deadline.Token }));

            _output.WriteLine($"Chat-client invocations: {chatClient.CallCount}");
            Assert.Equal(BoundedModelMaximumAttempts, chatClient.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHost BuildHost(ITemporalClient client, IChatClient chatClient, string taskQueue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton(chatClient);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<DefaultOptionsSubAgentWorkflow>()
            .AddWorkflow<CallerOptionsSubAgentWorkflow>()
            .AddTemporalAgents(opts => opts.AddDurableAgent("SubAgent", agent =>
            {
                agent.Instructions = "You are a helpful sub-agent.";
                agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            }));

        return builder.Build();
    }

    /// <summary>
    /// Returns the attributes of the first <c>RunDurableAgentStep</c> <c>ActivityTaskScheduled</c>
    /// event, or <see langword="null"/> when the workflow never scheduled one.
    /// </summary>
    private static async Task<Temporalio.Api.History.V1.ActivityTaskScheduledEventAttributes?>
        FindFirstStepScheduleAsync(WorkflowHandle handle)
    {
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } scheduled
                && scheduled.ActivityType.Name == RunDurableAgentStepActivity)
            {
                return scheduled;
            }
        }

        return null;
    }

    [Workflow("SubAgentRetryPolicy.DefaultOptions")]
    internal class DefaultOptionsSubAgentWorkflow
    {
        [WorkflowRun]
        public async Task<string> RunAsync(string userMessage)
        {
            // The idiomatic sub-agent call: no activity options supplied, so the library builds them.
            var agent = GetTemporalAgent("SubAgent");
            var session = await agent.CreateSessionAsync().ConfigureAwait(true);
            var response = await agent
                .RunAsync([new ChatMessage(ChatRole.User, userMessage)], session)
                .ConfigureAwait(true);
            return response.Messages.Count > 0 ? response.Messages[^1].Text ?? string.Empty : string.Empty;
        }
    }

    [Workflow("SubAgentRetryPolicy.CallerOptions")]
    internal class CallerOptionsSubAgentWorkflow
    {
        internal const int CallerMaximumAttempts = 2;

        internal static readonly TimeSpan CallerMaximumInterval = TimeSpan.FromSeconds(7);

        internal static readonly TimeSpan CallerStartToCloseTimeout = TimeSpan.FromMinutes(3);

        [WorkflowRun]
        public async Task<string> RunAsync(string userMessage)
        {
            var agent = GetTemporalAgent(
                "SubAgent",
                new ActivityOptions
                {
                    StartToCloseTimeout = CallerStartToCloseTimeout,
                    RetryPolicy = new RetryPolicy
                    {
                        MaximumAttempts = CallerMaximumAttempts,
                        MaximumInterval = CallerMaximumInterval,
                    },
                });
            var session = await agent.CreateSessionAsync().ConfigureAwait(true);
            var response = await agent
                .RunAsync([new ChatMessage(ChatRole.User, userMessage)], session)
                .ConfigureAwait(true);
            return response.Messages.Count > 0 ? response.Messages[^1].Text ?? string.Empty : string.Empty;
        }
    }
}
