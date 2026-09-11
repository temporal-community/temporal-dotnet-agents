// HARNESS for docs/how-to/MAF/scheduling.md § "Register a recurring run".
//
// The snippet carries its own `using` block; those are hoisted above the namespace here (file-scoped
// namespaces cannot follow statements) and kept identical otherwise.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Api.Enums.V1;
using Temporalio.Client.Schedules;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Scheduling;
using Microsoft.Extensions.Hosting;
using OpenAI;

namespace DocSnippets.Scheduling;

internal static class RegisterARecurringRun
{
    internal static void Configure(HostApplicationBuilder builder, OpenAIClient openAiClient, string model)
    {
        // BEGIN SNIPPET docs/how-to/MAF/scheduling.md#register-a-recurring-run (lines 23-66)
        builder.Services.AddTemporalClient("localhost:7233", "default");
        builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient()).Build();

        builder.Services
            .AddHostedTemporalWorker("agents-worker")
            .AddTemporalAgents(options =>
            {
                options.AddDurableAgent("DigestAgent", agent =>
                {
                    agent.Instructions = "Summarize new customer feedback.";
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>();
                });

                options.AddScheduledAgentRun(
                    agentName: "DigestAgent",
                    scheduleId: "daily-digest",
                    request: new RunRequest("Summarize feedback received since the previous digest."),
                    spec: new ScheduleSpec
                    {
                        Calendars =
                        [
                            new ScheduleCalendarSpec
                            {
                                Hour = [new ScheduleRange(8)],
                                Minute = [new ScheduleRange(0)],
                            },
                        ],
                        TimeZoneName = "America/New_York",
                    },
                    policy: new SchedulePolicy
                    {
                        Overlap = ScheduleOverlapPolicy.Skip,
                        CatchupWindow = TimeSpan.FromMinutes(10),
                    });
            });
        // END SNIPPET docs/how-to/MAF/scheduling.md#register-a-recurring-run
    }
}
