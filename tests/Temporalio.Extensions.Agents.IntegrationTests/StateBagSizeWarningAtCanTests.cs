using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Tests.StepMode;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// C-1 — the StateBag 64 KB size-guard warning. When the serialized <c>CarriedStateBag</c> exceeds
/// <see cref="AgentWorkflow.StateBagSizeWarnThresholdBytes"/> (64 KB) at continue-as-new time, a
/// <c>LogWarning</c> fires (warn-only — the session keeps running). Under the threshold, no warning.
///
/// <para>
/// CAN is forced deterministically with a low <c>MaxEntryCount</c> (history-count trigger), per the
/// test-laws — not the SDK <c>ContinueAsNewSuggested</c> heuristic. The warning is captured via a
/// <see cref="CapturingLoggerProvider"/> registered in the worker host's DI logger factory, which
/// <c>Workflow.Logger</c> routes through.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class StateBagSizeWarningAtCanTests
{
    [Fact]
    public async Task StateBagOverThreshold_AtContinueAsNew_FiresWarning()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var capture = new CapturingLoggerProvider();

        // A context provider that writes a ~100 KB value into the StateBag (over the 64 KB guard).
        var bigProvider = new BigStateBagProvider(sizeBytes: 100 * 1024);

        // MaxEntryCount = 4 → 2 turns (req+resp each) trips the count-driven CAN deterministically.
        using var host = BuildHost(env.Client, capture, bigProvider, maxEntryCount: 4);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("BigBagAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Two turns → 4 history entries → CAN. Each turn's provider keeps the big value present.
            await proxy.RunAsync("turn 1", session);
            await DriveUntilWarningOrTurnsAsync(proxy, session, capture, maxTurns: 6);

            Assert.True(
                capture.ContainsLog(LogLevel.Warning, "CarriedStateBag is", "continue-as-new"),
                "Expected the 64 KB StateBag size-guard warning at continue-as-new.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task StateBagUnderThreshold_AtContinueAsNew_NoWarning()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var capture = new CapturingLoggerProvider();

        // A small StateBag value, well under 64 KB.
        var smallProvider = new BigStateBagProvider(sizeBytes: 256);

        using var host = BuildHost(env.Client, capture, smallProvider, maxEntryCount: 4);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("BigBagAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            await proxy.RunAsync("turn 1", session);
            // Drive enough turns to trip CAN at least once.
            for (var i = 2; i <= 6; i++)
            {
                try { await proxy.RunAsync($"turn {i}", session); }
                catch (Temporalio.Exceptions.WorkflowUpdateFailedException) { break; }
            }
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.False(
                capture.ContainsLog(LogLevel.Warning, "CarriedStateBag is", "continue-as-new"),
                "A small StateBag must not trigger the size-guard warning.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task DriveUntilWarningOrTurnsAsync(
        AIAgent proxy, AgentSession session,
        CapturingLoggerProvider capture, int maxTurns)
    {
        for (var i = 2; i <= maxTurns; i++)
        {
            if (capture.ContainsLog(LogLevel.Warning, "CarriedStateBag is", "continue-as-new"))
                return;
            try { await proxy.RunAsync($"turn {i}", session); }
            catch (Temporalio.Exceptions.WorkflowUpdateFailedException)
            {
                // CAN fired in flight — give the warning a moment to flush, then stop.
                break;
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    private static IHost BuildHost(
        ITemporalClient client,
        CapturingLoggerProvider capture,
        AIContextProvider provider,
        int maxEntryCount)
    {
        var taskQueue = $"big-bag-{Guid.NewGuid():N}";
        var scripted = new ScriptedChatClient(
            Enumerable.Range(1, 30).Select(i => new ChatResponse(new ChatMessage(ChatRole.Assistant, $"r{i}"))));

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(capture);
        });

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("BigBagAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.MaxEntryCount = maxEntryCount;
                    agent.TimeToLive = TimeSpan.FromMinutes(10);
                    agent.AddContextProvider(provider);
                });
            });

        return builder.Build();
    }

    /// <summary>Context provider that writes a fixed-size string into the StateBag on each LLM call.</summary>
    private sealed class BigStateBagProvider(int sizeBytes) : AIContextProvider
    {
        private readonly string _payload = new('x', sizeBytes);

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            if (context.Session is TemporalAgentSession s)
                s.StateBag.SetValue("test.big_blob", _payload, System.Text.Json.JsonSerializerOptions.Default);
            return new ValueTask<AIContext>(new AIContext());
        }
    }
}
