using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Integration coverage for split worker/client deployments. Verifies bugs P1-1 and P1-2:
/// <list type="bullet">
///   <item>P1-2: Write tools registered with <c>NoRetry()</c> use <c>MaximumAttempts = 1</c>
///   even when the workflow was started by a proxy-only client.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class SplitDeploymentTests
{
    private const string InvokeAgentToolActivity = "TemporalCommunity.Extensions.Agents.InvokeAgentTool";

    /// <summary>
    /// P1-2: In a split deployment (client registers only an agent proxy, worker hosts the
    /// full durable agent), a write tool registered with <c>opts.NoRetry()</c> must use
    /// <c>MaximumAttempts = 1</c> for its <c>InvokeAgentTool</c> activity.
    ///
    /// Before the fix, <c>BuildProxyOnlyAgentWorkflowInput</c> left
    /// <c>DurableAgentToolActivityOptions = null</c> permanently, causing all tools to inherit
    /// the flat job-level <c>RetryPolicy</c> (unbounded retries) regardless of per-tool overrides.
    /// </summary>
    [Fact]
    public async Task SplitDeployment_WriteToolWithNoRetry_UsesMaximumAttempts1()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var recorder = new RecordingTool
        {
            Name = "write_record",
            Behavior = RecordingToolBehavior.AlwaysFail,
        };
        var aiFunction = recorder.Build();

        var fc = new FunctionCallContent("call-1", "write_record",
            new Dictionary<string, object?> { ["input"] = "data" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "Done.");

        var taskQueue = $"split-deploy-noretry-{Guid.NewGuid():N}";

        // Build the worker host (full registration with NoRetry on write tool).
        using var workerHost = BuildWorkerHost(env.Client, scripted, taskQueue,
            configureAgent: agent =>
            {
                agent.AddTool(aiFunction, opts => opts.NoRetry());
            });
        await workerHost.StartAsync();

        try
        {
            // Build the client host (proxy-only, no AddDurableAgent, no IChatClient).
            using var clientHost = BuildClientHost(env.Client, taskQueue);

            var proxy = clientHost.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Tool always fails — exception surfaces to caller.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await proxy.RunAsync("Hello", session));

            // Only one attempt should have been made (NoRetry = MaximumAttempts 1).
            Assert.Equal(1, recorder.CallCount);

            // Inspect history: the InvokeAgentTool scheduled event must carry MaximumAttempts=1.
            var handle = env.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            var foundToolSchedule = false;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                    a.ActivityType.Name == InvokeAgentToolActivity)
                {
                    foundToolSchedule = true;
                    Assert.NotNull(a.RetryPolicy);
                    Assert.Equal(1, a.RetryPolicy.MaximumAttempts);
                    break;
                }
            }

            Assert.True(foundToolSchedule, "Expected at least one InvokeAgentTool ActivityTaskScheduled event.");
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    // ── Host builders ────────────────────────────────────────────────────────────

    private static IHost BuildWorkerHost(
        ITemporalClient client,
        ScriptedChatClient scripted,
        string taskQueue,
        Action<DurableAgentBuilder>? configureAgent,
        Action<TemporalAgentsOptions>? configureOpts = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                configureOpts?.Invoke(opts);
                opts.AddDurableAgent("DurableAgent", agent =>
                {
                    agent.Instructions = "You are a helpful agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    configureAgent?.Invoke(agent);
                });
            });

        return builder.Build();
    }

    /// <summary>
    /// Builds a client-only host that declares the agent proxy but hosts no worker.
    /// </summary>
    private static IHost BuildClientHost(ITemporalClient client, string taskQueue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);

        // AddTemporalAgentProxies: no worker, no IChatClient — proxy only.
        builder.Services.AddTemporalAgentProxies(
            configure: opts => opts.AddAgentProxy("DurableAgent"),
            taskQueue: taskQueue);

        return builder.Build();
    }

}
