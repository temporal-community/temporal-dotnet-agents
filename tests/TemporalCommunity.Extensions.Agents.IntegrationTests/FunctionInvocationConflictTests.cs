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
/// End-to-end behaviour of the durable-agent guard against an in-process function-invocation loop
/// in the user's chat client.
/// </summary>
/// <remarks>
/// The unit tests cover detection. These cover the three properties detection alone cannot show:
/// the model is never called, the misconfiguration does not consume Temporal retries, and the
/// startup-only test bypass cannot disable the runtime guard.
/// </remarks>
[Trait("Category", "Integration")]
public class FunctionInvocationConflictTests
{
    private readonly ITestOutputHelper _output;

    public FunctionInvocationConflictTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ChatClientWithFunctionInvocation_FailsOnFirstAttempt_WithoutCallingTheModel()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        // If the guard did not fire, this scripted client would answer and the turn would succeed.
        var scripted = new ScriptedChatClient(
            [new ChatResponse(new ChatMessage(ChatRole.Assistant, "Should never be reached."))]);

        var taskQueue = $"fic-conflict-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<ConflictWorkflow>()
            .AddTemporalAgents(opts =>
            {
                // A generous retry policy on purpose: a configuration error must not consume it.
                opts.DefaultRetryPolicy = new RetryPolicy { MaximumAttempts = 5 };

                // The startup bypass exists for tests of the agent-pipeline dry run. It must not
                // reach the runtime chat-client guard.
                opts.SkipDryRunCCheck = true;

                opts.AddDurableAgent("SubAgent", agent =>
                {
                    agent.ChatClient = _ => new ChatClientBuilder(scripted)
                        .UseFunctionInvocation()
                        .Build();
                    agent.AddTool(AIFunctionFactory.Create(
                        () => "ok", new AIFunctionFactoryOptions { Name = "noop_tool" }));
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var handle = await env.Client.StartWorkflowAsync(
                (ConflictWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions($"fic-conflict-{Guid.NewGuid():N}", taskQueue));

            var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
                async () => await handle.GetResultAsync());

            var activityFailure = Assert.IsType<ActivityFailureException>(failure.InnerException);
            var appFailure = Assert.IsType<ApplicationFailureException>(activityFailure.InnerException);

            _output.WriteLine($"errorType={appFailure.ErrorType}: {appFailure.Message}");

            // Classified so TemporalFailureInspector can recognise it.
            Assert.Equal("DurableConfigurationException", appFailure.ErrorType);
            Assert.Contains("agent.ChatClient", appFailure.Message, StringComparison.Ordinal);

            // The CLR exception type does NOT survive the wire. Temporal's DefaultFailureConverter
            // turns the worker-side DurableFunctionInvocationConflictException into a nested
            // ApplicationFailureException whose ErrorType carries the type name. A remote caller
            // therefore never receives the typed exception itself — only this shape. Pinned here so
            // the documentation and this behaviour cannot drift apart again.
            var serializedCause = Assert.IsType<ApplicationFailureException>(appFailure.InnerException);
            Assert.Equal(
                nameof(TemporalCommunity.Extensions.AI.Exceptions.DurableFunctionInvocationConflictException),
                serializedCause.ErrorType);

            // The model was never reached — the guard runs before ChatClientAgent is constructed.
            Assert.Equal(0, scripted.CallCount);

            // Exactly one attempt, despite MaximumAttempts = 5. This is the whole point of the
            // non-retryable conversion: a misconfiguration cannot be fixed by trying again.
            //
            // Read the ATTEMPT NUMBER, not the number of ActivityTaskStarted events. Temporal does
            // not persist a Started event per retry — intermediate attempts of a retrying activity
            // are transient, so an event COUNT reads 1 whether the activity ran once or five times.
            // Counting would therefore pass even if the non-retryable conversion regressed, which
            // is precisely the failure this test exists to catch. `Attempt` is 1-based and is the
            // only field in history that distinguishes the two.
            var maxAttempt = 0;
            var toolActivities = 0;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskStartedEventAttributes is { } started)
                {
                    maxAttempt = Math.Max(maxAttempt, started.Attempt);
                }

                if (ev.ActivityTaskScheduledEventAttributes?.ActivityType.Name
                    == "TemporalCommunity.Extensions.Agents.InvokeAgentTool")
                {
                    toolActivities++;
                }
            }

            Assert.Equal(1, maxAttempt);

            // And no tool ran in-process or otherwise.
            Assert.Equal(0, toolActivities);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task UndecoratedChatClient_StillRuns()
    {
        // The guard must not be so eager that a normal agent stops working.
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = new ScriptedChatClient(
            [new ChatResponse(new ChatMessage(ChatRole.Assistant, "Answered."))]);

        var taskQueue = $"fic-ok-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<ConflictWorkflow>()
            .AddTemporalAgents(opts => opts.AddDurableAgent("SubAgent", agent =>
            {
                agent.ChatClient = _ => scripted;
            }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var result = await env.Client.ExecuteWorkflowAsync(
                (ConflictWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions($"fic-ok-{Guid.NewGuid():N}", taskQueue));

            Assert.Equal("Answered.", result);
            Assert.Equal(1, scripted.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Workflow("FunctionInvocationConflict.Run")]
    internal class ConflictWorkflow
    {
        [WorkflowRun]
        public async Task<string> RunAsync()
        {
            var agent = GetTemporalAgent("SubAgent");
            var session = await agent.CreateSessionAsync().ConfigureAwait(true);
            var response = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, "hello")], session).ConfigureAwait(true);
            return response.Messages.Count > 0 ? response.Messages[^1].Text ?? string.Empty : string.Empty;
        }
    }
}
