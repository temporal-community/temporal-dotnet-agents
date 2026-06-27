using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Tests.StepMode; // shared ScriptedChatClient
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using Xunit;
using Xunit.Abstractions;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// X-3 — <see cref="TemporalAIAgent"/> (orchestration sub-agent via
/// <see cref="TemporalWorkflowExtensions.GetAgent"/>) threads its StateBag across steps. A context
/// provider's StateBag write in step N must be visible to the provider in step N+1.
///
/// <para>
/// Before the X-3 fix, <c>TemporalAIAgent.RunCoreAsync</c> did not carry <c>_currentStateBag</c>
/// from <c>stepResult.UpdatedStateBag</c> into the next <c>RunDurableAgentStep</c>, so a provider's
/// per-step write was lost — every step started from an empty bag. This test runs a 3-LLM-call turn
/// and verifies a per-step counter persisted in the StateBag monotonically increases (1, 2, 3),
/// proving carry-forward. If threading were broken every step would observe a count of 1.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class SubAgentStateBagThreadingTests
{
    private readonly ITestOutputHelper _output;

    public SubAgentStateBagThreadingTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SubAgent_ContextProviderStateBagWrite_VisibleInNextStep()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        // Drive a 3-LLM-call turn: step1 → tool call, step2 → tool call, step3 → final.
        var tool = AIFunctionFactory.Create(
            () => "ok",
            new AIFunctionFactoryOptions { Name = "noop_tool" });

        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c1", "noop_tool", new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c2", "noop_tool", new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Final.")),
        ]);

        var provider = new StepCountingContextProvider();

        var taskQueue = $"subagent-statebag-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<OrchestrationWorkflow>()
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("SubAgent", agent =>
                {
                    agent.Instructions = "You are a helpful sub-agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(tool);
                    agent.AddContextProvider(provider);
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var wfId = $"subagent-statebag-{Guid.NewGuid():N}";
            var handle = await env.Client.StartWorkflowAsync(
                (OrchestrationWorkflow wf) => wf.RunAsync("go"),
                new WorkflowOptions(wfId, taskQueue));
            await handle.GetResultAsync();

            // The provider injects "step-count:N" each LLM call, where N is the value it read
            // from the StateBag, incremented, and wrote back. With three LLM calls and working
            // carry-forward the observed sequence is 1, 2, 3.
            var observed = provider.ObservedCounts.ToList();
            _output.WriteLine($"Observed per-step counts: [{string.Join(", ", observed)}]");

            Assert.Equal(3, observed.Count);
            // The load-bearing assertion: step N+1 saw step N's write.
            Assert.Equal([1, 2, 3], observed);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Workflow("SubAgentStateBagThreading.Orchestration")]
    internal class OrchestrationWorkflow
    {
        [WorkflowRun]
        public async Task<string> RunAsync(string userMsg)
        {
            var agent = GetAgent("SubAgent");
            var session = await agent.CreateSessionAsync().ConfigureAwait(true);
            var response = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, userMsg)], session).ConfigureAwait(true);
            return response.Messages.Count > 0 ? response.Messages[^1].Text ?? "" : "";
        }
    }

    /// <summary>
    /// A context provider that, on each LLM call, reads a counter from the session StateBag,
    /// increments it, writes it back, and records the value it observed. Carry-forward across
    /// steps is proven when the recorded sequence increases monotonically.
    /// </summary>
    private sealed class StepCountingContextProvider : AIContextProvider
    {
        private const string CounterKey = "test.step_counter";
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _observed = new();

        public StepCountingContextProvider()
            : base(provideInputMessageFilter: null,
                   storeInputRequestMessageFilter: null,
                   storeInputResponseMessageFilter: null)
        {
        }

        public IReadOnlyCollection<int> ObservedCounts => _observed.ToArray();

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            var count = 1;
            if (context.Session is TemporalAgentSession agentSession)
            {
                var bag = agentSession.StateBag;
                if (bag.TryGetValue<string>(CounterKey, out var existing,
                        System.Text.Json.JsonSerializerOptions.Default)
                    && int.TryParse(existing, out var prior))
                {
                    count = prior + 1;
                }
                bag.SetValue(
                    CounterKey,
                    count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    System.Text.Json.JsonSerializerOptions.Default);
            }

            _observed.Enqueue(count);

            return new ValueTask<AIContext>(new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, $"step-count:{count}")],
            });
        }
    }
}
