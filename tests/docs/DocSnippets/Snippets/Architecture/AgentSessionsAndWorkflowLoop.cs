// HARNESS for docs/architecture/MAF/agent-sessions-and-workflow-loop.md.
//
// Architecture docs carry registration examples too. Until the coverage glob was widened from
// docs/how-to/MAF to docs/, these had no compile coverage at all — only a regex pre-filter that
// could see a bad shape but never whether the code bound to a real overload.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Architecture;

internal static class AgentSessionsAndWorkflowLoop
{
    internal static void ActivityStartToCloseTimeout(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/architecture/MAF/agent-sessions-and-workflow-loop.md#1-activity-starttoclosetimeout-default-5-minutes
        // Configure via options
        builder.Services.AddTemporalClient("localhost:7233", "default");
        builder.Services.AddHostedTemporalWorker("task-queue")
            .AddTemporalAgents(opts =>
            {
                opts.DefaultActivityTimeout = TimeSpan.FromMinutes(60);
                opts.AddDurableAgent("MyAgent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
            });
        // END SNIPPET docs/architecture/MAF/agent-sessions-and-workflow-loop.md#1-activity-starttoclosetimeout-default-5-minutes
    }

    internal static void WorkflowTimeToLive(TemporalAgentsOptions opts)
    {
        // BEGIN SNIPPET docs/architecture/MAF/agent-sessions-and-workflow-loop.md#3-workflow-timetolive-default-14-days
        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromHours(1);
        });
        // or — worker-level default for every agent that does not override
        opts.DefaultTimeToLive = TimeSpan.FromDays(7);
        // END SNIPPET docs/architecture/MAF/agent-sessions-and-workflow-loop.md#3-workflow-timetolive-default-14-days
    }
}
