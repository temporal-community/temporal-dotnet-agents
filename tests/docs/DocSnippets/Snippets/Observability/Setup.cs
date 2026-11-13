// HARNESS for docs/how-to/MAF/observability.md § "Setup".
//
// The snippet's own `using` lines are hoisted above the file-scoped namespace (statements cannot
// precede it); nothing else is altered.
//
// The doc lists the snippet's `using` directives, including `Microsoft.Agents.AI` for
// `pipeline.UseOpenTelemetry` and `OpenTelemetry` for `Sdk`.
using Microsoft.Agents.AI;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Temporalio.Extensions.OpenTelemetry;
using TemporalCommunity.Extensions.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;

namespace DocSnippets.Observability;

internal static class Setup
{
    internal static void Configure(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/how-to/MAF/observability.md#setup (lines 51-92)
        const string mafTelemetrySource = "MyCompany.MyAgent";

        // 1. Configure the OTel tracer provider with all relevant sources
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(
                TracingInterceptor.ClientSource.Name,      // Temporal client spans
                TracingInterceptor.WorkflowsSource.Name,   // Temporal workflow spans
                TracingInterceptor.ActivitiesSource.Name,  // Temporal activity spans
                TemporalAgentTelemetry.ActivitySourceName, // Temporal agent correlation spans
                mafTelemetrySource)                        // optional MAF GenAI spans
            .AddOtlpExporter()
            .Build();

        // 2. Add the tracing interceptor to the Temporal client
        builder.Services.AddTemporalClient(opts =>
        {
            opts.TargetHost = "localhost:7233";
            opts.Namespace = "default";
            opts.Interceptors = [new TracingInterceptor()];
        });

        // 3. Register agents as usual. Register an IChatClient in DI for this agent.
        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("MyAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.ConfigureAgentPipeline = pipeline =>
                        pipeline.UseOpenTelemetry(mafTelemetrySource);
                });
            });
        // END SNIPPET docs/how-to/MAF/observability.md#setup
    }
}
