using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// integration coverage for the load-bearing lifecycle contract Q10/CP1 —
/// <see cref="AIContextProvider.InvokingAsync"/> and <see cref="AIContextProvider.InvokedAsync"/>
/// fire ONCE PER LLM CALL (per <c>RunDurableAgentStep</c> activity), not once per turn. The
/// durable workflow loop runs multiple LLM calls per turn (one per tool-call iteration plus the
/// final response), so the provider must observe each iteration.
/// </summary>
[Trait("Category", "Integration")]
public class DurableAgentContextProviderLifecycleTests
{
    [Fact]
    public async Task DurableAgent_AddContextProvider_InvokingAsyncFiresPerLlmCall()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        // Drive a 3-LLM-call turn:
        //   step 1 → 2 tool calls,
        //   step 2 → 1 tool call,
        //   step 3 → final answer.
        // Provider's InvokingAsync should fire 3 times (one per LLM call).
        var recorder = new RecordingTool { Name = "echo_tool" };
        var aiFunction = recorder.Build();

        var step1Calls = new[]
        {
            new FunctionCallContent("call-1A", "echo_tool", new Dictionary<string, object?> { ["input"] = "A" }),
            new FunctionCallContent("call-1B", "echo_tool", new Dictionary<string, object?> { ["input"] = "B" }),
        };
        var step2Calls = new[]
        {
            new FunctionCallContent("call-2A", "echo_tool", new Dictionary<string, object?> { ["input"] = "C" }),
        };

        var responses = new List<ChatResponse>
        {
            new(new ChatMessage(ChatRole.Assistant, [.. step1Calls])),
            new(new ChatMessage(ChatRole.Assistant, [.. step2Calls])),
            new(new ChatMessage(ChatRole.Assistant, "Final answer.")),
        };
        var scripted = new ScriptedChatClient(responses);

        var provider = new CountingContextProvider();

        using var host = BuildHost(env.Client, scripted, aiFunction, provider);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("Run multi-step turn.", session);

            Assert.Contains("Final answer.", response.Messages[^1].Text);
            // Three tool invocations across the three iterations.
            Assert.Equal(3, recorder.CallCount);

            // Q10/CP1 contract: provider hooks fire per LLM call. With three LLM calls in this
            // turn, both InvokingAsync and InvokedAsync should fire exactly three times.
            Assert.Equal(3, provider.InvokingCount);
            Assert.Equal(3, provider.InvokedCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_AddContextProviderInstance_RegistersAndFires()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello.")),
        });
        var provider = new CountingContextProvider();

        // Use the AddContextProvider(instance) overload (no factory) — verifies the simpler
        // entry point also wires through.
        using var host = BuildHost(env.Client, scripted, tool: null, providerInstance: provider);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("Hi", session);

            Assert.Equal(1, provider.InvokingCount);
            Assert.Equal(1, provider.InvokedCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// A provider that faults in its post-call hook must not cause a second notification of the
    /// providers that already succeeded, and must not discard an LLM response that was produced.
    /// </summary>
    /// <remarks>
    /// The success-path and failure-path <c>InvokedAsync</c> loops are separate. Without a guard
    /// on the success loop, one provider throwing sends the whole step into the failure path,
    /// which notifies <em>every</em> provider again — so a provider earlier in registration order
    /// observes the same LLM step twice, and the completed model call is thrown away.
    /// </remarks>
    [Fact]
    public async Task DurableAgent_ProviderThrowsFromInvokedAsync_NotifiesOthersOnceAndKeepsResult()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello.")),
        });

        var counting = new CountingContextProvider();
        var faulting = new ThrowingInvokedProvider();

        // counting is registered first, so it receives the success notification before the
        // faulting provider throws.
        using var host = BuildHost(env.Client, scripted, tool: null, providerInstance: counting,
            extraProvider: faulting);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            var response = await proxy.RunAsync("Hi", session);

            // The model call succeeded; a post-invocation fault must not discard it.
            Assert.Contains("Hello.", response.Messages[^1].Text);

            // Exactly one notification for one LLM step — not two.
            Assert.Equal(1, counting.InvokedCount);
            Assert.Equal(1, counting.InvokingCount);

            // The faulting provider was reached once and threw once.
            Assert.Equal(1, faulting.Attempts);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>Faults from the post-call hook only; its pre-call hook is inert.</summary>
    private sealed class ThrowingInvokedProvider : AIContextProvider
    {
        private long _attempts;

        public ThrowingInvokedProvider()
            : base(provideInputMessageFilter: null,
                   storeInputRequestMessageFilter: null,
                   storeInputResponseMessageFilter: null)
        {
        }

        public int Attempts => (int)Interlocked.Read(ref _attempts);

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default) =>
            new(new AIContext());

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("post-invocation fault");
        }
    }

    private static IHost BuildHost(
        ITemporalClient client,
        ScriptedChatClient scripted,
        AIFunction? tool,
        CountingContextProvider providerInstance,
        AIContextProvider? extraProvider = null)
    {
        var taskQueue = $"durable-agent-ctxprov-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("DurableAgent", agent =>
                {
                    agent.Instructions = "You are a helpful agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    if (tool is not null)
                    {
                        agent.AddTool(tool);
                    }
                    agent.AddContextProvider(providerInstance);
                    if (extraProvider is not null)
                    {
                        agent.AddContextProvider(extraProvider);
                    }
                });
            });

        return builder.Build();
    }

    /// <summary>
    /// Test-only <see cref="AIContextProvider"/> that records calls to its
    /// <see cref="AIContextProvider.ProvideAIContextAsync"/> hook (the load-bearing per-LLM-call
    /// hook). The provider returns an empty <see cref="AIContext"/> on every invocation so it
    /// has no side-effect on the LLM call beyond observation.
    /// </summary>
    /// <remarks>
    /// The two counters must be incremented in <em>different</em> hooks. Incrementing both inside
    /// <c>ProvideAIContextAsync</c> makes them equal by construction, so the post-call assertion
    /// cannot fail no matter what the activity does. <c>InvokedCoreAsync</c> is the overridable
    /// post-call hook on this surface, so the invoked counter is taken there.
    /// </remarks>
    private sealed class CountingContextProvider : AIContextProvider
    {
        private long _invoking;
        private long _invoked;

        public CountingContextProvider()
            : base(provideInputMessageFilter: null,
                   storeInputRequestMessageFilter: null,
                   storeInputResponseMessageFilter: null)
        {
        }

        public int InvokingCount => (int)Interlocked.Read(ref _invoking);

        /// <summary>Counts the real post-call hook, so a double notification is observable.</summary>
        public int InvokedCount => (int)Interlocked.Read(ref _invoked);

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoking);
            return new ValueTask<AIContext>(new AIContext());
        }

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoked);
            return default;
        }
    }
}
