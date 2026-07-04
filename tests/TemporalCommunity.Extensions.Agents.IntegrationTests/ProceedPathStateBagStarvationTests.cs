using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using TemporalCommunity.Extensions.AI;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Fix 1 regression — Proceed-path StateBag starvation (Cypher audit, third latent
/// null-starvation consumer).
///
/// <para>
/// The bug: <c>AgentWorkflow</c>'s Proceed-path <c>InvokeAgentTool</c> dispatch used the
/// non-forced <c>GetStateBagForDispatch()</c> hash gate. On step 2+ of a hash-unchanged turn
/// the gate returns <see langword="null"/>, and the stateless tool activity builds an EMPTY
/// session via <c>TemporalAgentSession.FromStateBag(id, null)</c>. A tool that reads a
/// workflow-thread-written StateBag key (here: a context provider's key, overlaid into
/// <c>_currentStateBag</c> on the workflow thread) therefore saw an empty bag on the second
/// tool call of the same turn.
/// </para>
///
/// <para>
/// The repro: one turn with TWO sequential tool-call iterations. A context provider writes a
/// stable key on every LLM call — its value never changes after the first overlay, so the bag
/// hash is unchanged by the time the SECOND tool dispatches. Pre-fix, that second tool observed
/// <c>&lt;absent&gt;</c>. Post-fix (Proceed path always forces the bag), it observes the key.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class ProceedPathStateBagStarvationTests : IClassFixture<ProceedPathStateBagStarvationTests.Fixture>
{
    private readonly Fixture _fixture;
    private readonly ITestOutputHelper _output;
    private WorkflowEnvironment Env => _fixture.Environment;

    public ProceedPathStateBagStarvationTests(Fixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task ProceedTool_MidTurnHashUnchangedStep_SeesWorkflowThreadStateBagKey()
    {
        const string toolName = "reader_tool";
        const string providerKey = "provider.key";
        const string providerValue = "from-provider";

        // A single turn with THREE sequential tool-call iterations:
        //   step 0: reader_tool → step 1: reader_tool → step 2: reader_tool → step 3: final.
        // The context provider seeds a stable providerKey on every LLM call. Step 0's bag is the
        // provider's raw activity serialization; from step 1 on the bag is the workflow's canonical
        // (Utf8JsonWriter, ordinal-sorted) re-serialization — byte-identical step-to-step, so its
        // FNV hash is UNCHANGED by the step-2 tool dispatch. That is exactly the hash-gate condition
        // that starved the Proceed-path tool of the bag pre-fix (it received null → EMPTY session).
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c0", toolName, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c1", toolName, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c2", toolName, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
        ]);

        var toolReads = new ConcurrentQueue<string>();

        var tool = AIFunctionFactory.Create(
            () =>
            {
                var bag = TemporalAgentContext.Current.CurrentSession.StateBag;
                var seen = bag.TryGetValue<string>(providerKey, out var v,
                    System.Text.Json.JsonSerializerOptions.Default) ? v : "<absent>";
                toolReads.Enqueue(seen ?? "<null>");
                return seen ?? "<null>";
            },
            new AIFunctionFactoryOptions { Name = toolName });

        var taskQueue = $"proceed-starvation-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("StarvationAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(tool);
                    // Workflow-thread StateBag writer: overlaid into _currentStateBag each LLM step.
                    agent.AddContextProvider(new SeedingProvider(providerKey, providerValue));
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("StarvationAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("go", session);

            // All three tool calls must see the provider key.
            Assert.True(toolReads.TryDequeue(out var firstCall), "First tool call did not run.");
            Assert.True(toolReads.TryDequeue(out var secondCall), "Second tool call did not run.");
            Assert.True(toolReads.TryDequeue(out var thirdCall), "Third tool call did not run.");
            _output.WriteLine($"Tool call 1 observed {providerKey} = {firstCall}");
            _output.WriteLine($"Tool call 2 observed {providerKey} = {secondCall}");
            _output.WriteLine($"Tool call 3 observed {providerKey} = {thirdCall}");

            Assert.Equal(providerValue, firstCall);
            Assert.Equal(providerValue, secondCall);
            // The load-bearing assertion: the step-2 tool dispatch is hash-unchanged from step-1's,
            // so pre-fix the non-forced gate returned null and this tool saw "<absent>" (EMPTY
            // session). Post-fix the Proceed path always forces the bag, so it sees the key.
            Assert.Equal(providerValue, thirdCall);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public WorkflowEnvironment Environment { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Environment = await TestEnvironmentHelper.StartLocalAsync();
            Environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        }

        public Task DisposeAsync() => Environment.ShutdownAsync();
    }

    /// <summary>Context provider that seeds a fixed, stable key on every LLM call. Because the
    /// value never changes, the workflow-thread bag hash is unchanged by the second tool
    /// dispatch — exactly the condition that triggered the Proceed-path null-starvation bug.</summary>
    private sealed class SeedingProvider(string key, string value) : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            if (context.Session is TemporalAgentSession s)
                s.StateBag.SetValue(key, value, System.Text.Json.JsonSerializerOptions.Default);
            return new ValueTask<AIContext>(new AIContext());
        }
    }
}
