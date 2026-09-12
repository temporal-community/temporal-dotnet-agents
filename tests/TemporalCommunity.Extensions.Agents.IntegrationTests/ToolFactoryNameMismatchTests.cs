using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Tests.Shared;
using Xunit;
using Xunit.Abstractions;
using static TemporalCommunity.Extensions.Agents.WorkflowAgents;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// A tool factory whose resolved <see cref="AIFunction.Name"/> differs from the name declared on
/// <c>AddTool(name, factory)</c> is a misconfiguration: no retry can turn the resolved name into
/// the declared one. It must therefore fail the attempt terminally instead of consuming the retry
/// budget (measured before the fix: the failure surfaced on attempt 5 of 5).
/// </summary>
/// <remarks>
/// Same contract as the function-invocation guard in
/// <see cref="FunctionInvocationConflictTests"/>: one attempt, error type
/// <c>DurableConfigurationException</c> so <c>TemporalFailureInspector</c> recognises it, and the
/// model is never reached.
/// </remarks>
[Trait("Category", "Integration")]
public class ToolFactoryNameMismatchTests
{
    private const string RunDurableAgentStepActivity =
        "TemporalCommunity.Extensions.Agents.RunDurableAgentStep";

    private const string DeclaredToolName = "declared_tool";
    private const string ResolvedToolName = "resolved_tool";

    private readonly ITestOutputHelper _output;

    public ToolFactoryNameMismatchTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ToolFactoryResolvingWrongName_FailsOnFirstAttempt_WithoutCallingTheModel()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        // Would answer if the blueprint ever got far enough to build the pipeline.
        var scripted = new ScriptedChatClient(
            [new ChatResponse(new ChatMessage(ChatRole.Assistant, "Should never be reached."))]);

        var factoryCalls = 0;

        var taskQueue = $"tool-name-mismatch-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<MismatchWorkflow>()
            .AddTemporalAgents(opts =>
            {
                // Generous on purpose: a configuration error must not consume it.
                opts.DefaultRetryPolicy = new RetryPolicy { MaximumAttempts = 5 };

                opts.AddDurableAgent("SubAgent", agent =>
                {
                    agent.ChatClient = _ => scripted;
                    agent.AddTool(DeclaredToolName, _ =>
                    {
                        Interlocked.Increment(ref factoryCalls);
                        return AIFunctionFactory.Create(
                            () => "ok",
                            new AIFunctionFactoryOptions { Name = ResolvedToolName });
                    });
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var handle = await env.Client.StartWorkflowAsync(
                (MismatchWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions($"tool-name-mismatch-{Guid.NewGuid():N}", taskQueue));

            var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
                async () => await handle.GetResultAsync());

            // Asserted first: the step ran exactly one attempt despite MaximumAttempts = 5. This
            // is the retry-budget claim the fix exists for, so it is the primary failure signal.
            //
            // Counting ActivityTaskStarted EVENTS would prove nothing: Temporal does not keep the
            // started/failed events of retried attempts in history, so that count is 1 whether the
            // activity ran once or five times. The attempt number lives in the Attempt field of the
            // surviving started event.
            var stepScheduledEventIds = new HashSet<long>();
            var observedAttempts = new List<int>();
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } scheduled
                    && scheduled.ActivityType.Name == RunDurableAgentStepActivity)
                {
                    stepScheduledEventIds.Add(ev.EventId);
                }

                if (ev.ActivityTaskStartedEventAttributes is { } started
                    && stepScheduledEventIds.Contains(started.ScheduledEventId))
                {
                    observedAttempts.Add(started.Attempt);
                }
            }

            _output.WriteLine($"RunDurableAgentStep attempts: [{string.Join(", ", observedAttempts)}]");

            Assert.NotEmpty(observedAttempts);
            Assert.All(observedAttempts, attempt => Assert.Equal(1, attempt));

            var activityFailure = Assert.IsType<ActivityFailureException>(failure.InnerException);
            var appFailure = Assert.IsType<ApplicationFailureException>(activityFailure.InnerException);

            _output.WriteLine($"errorType={appFailure.ErrorType}: {appFailure.Message}");

            // Classified so TemporalFailureInspector can recognise it.
            Assert.Equal("DurableConfigurationException", appFailure.ErrorType);
            Assert.Contains(DeclaredToolName, appFailure.Message, StringComparison.Ordinal);
            Assert.Contains(ResolvedToolName, appFailure.Message, StringComparison.Ordinal);
            Assert.Contains(
                "must match the name declared", appFailure.Message, StringComparison.Ordinal);

            // The blueprint fails before any chat client or pipeline is built.
            Assert.Equal(0, scripted.CallCount);

            // One blueprint build was attempted, so the factory ran exactly once. A retried
            // attempt would re-run it (the throw leaves nothing in the blueprint cache).
            Assert.Equal(1, Volatile.Read(ref factoryCalls));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// The sibling guard: a factory that returns <see langword="null"/>. Same reasoning as the
    /// name mismatch — null on the first attempt is null on the fifth — and until this test was
    /// written that branch threw a plain <see cref="InvalidOperationException"/>, which Temporal
    /// retries. It therefore burned the whole budget before surfacing the identical error.
    /// </summary>
    [Fact]
    public async Task ToolFactoryReturningNull_FailsOnFirstAttempt_WithoutCallingTheModel()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = new ScriptedChatClient(
            [new ChatResponse(new ChatMessage(ChatRole.Assistant, "Should never be reached."))]);

        var factoryCalls = 0;

        var taskQueue = $"tool-null-factory-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<MismatchWorkflow>()
            .AddTemporalAgents(opts =>
            {
                // Generous on purpose: a configuration error must not consume it.
                opts.DefaultRetryPolicy = new RetryPolicy { MaximumAttempts = 5 };

                opts.AddDurableAgent("SubAgent", agent =>
                {
                    agent.ChatClient = _ => scripted;
                    agent.AddTool(DeclaredToolName, _ =>
                    {
                        Interlocked.Increment(ref factoryCalls);
                        return null!;
                    });
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var handle = await env.Client.StartWorkflowAsync(
                (MismatchWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions($"tool-null-factory-{Guid.NewGuid():N}", taskQueue));

            var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
                async () => await handle.GetResultAsync());

            // Attempt NUMBER, not event count — see the note in the mismatch test above.
            var stepScheduledEventIds = new HashSet<long>();
            var observedAttempts = new List<int>();
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } scheduled
                    && scheduled.ActivityType.Name == RunDurableAgentStepActivity)
                {
                    stepScheduledEventIds.Add(ev.EventId);
                }

                if (ev.ActivityTaskStartedEventAttributes is { } started
                    && stepScheduledEventIds.Contains(started.ScheduledEventId))
                {
                    observedAttempts.Add(started.Attempt);
                }
            }

            _output.WriteLine($"RunDurableAgentStep attempts: [{string.Join(", ", observedAttempts)}]");

            Assert.NotEmpty(observedAttempts);
            Assert.All(observedAttempts, attempt => Assert.Equal(1, attempt));

            var activityFailure = Assert.IsType<ActivityFailureException>(failure.InnerException);
            var appFailure = Assert.IsType<ApplicationFailureException>(activityFailure.InnerException);

            _output.WriteLine($"errorType={appFailure.ErrorType}: {appFailure.Message}");

            // Same error type as the mismatch guard, so TemporalFailureInspector treats both alike.
            Assert.Equal("DurableConfigurationException", appFailure.ErrorType);
            Assert.Contains(DeclaredToolName, appFailure.Message, StringComparison.Ordinal);
            Assert.Contains("returned null", appFailure.Message, StringComparison.Ordinal);

            Assert.Equal(0, scripted.CallCount);
            Assert.Equal(1, Volatile.Read(ref factoryCalls));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Workflow("ToolFactoryNameMismatch.Mismatch")]
    internal class MismatchWorkflow
    {
        [WorkflowRun]
        public async Task RunAsync()
        {
            var agent = GetTemporalAgent("SubAgent");
            var session = await agent.CreateSessionAsync().ConfigureAwait(true);
            await agent.RunAsync([new ChatMessage(ChatRole.User, "Hello")], session)
                .ConfigureAwait(true);
        }
    }
}
