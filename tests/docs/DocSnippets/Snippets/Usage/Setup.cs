// HARNESS for docs/how-to/MAF/usage.md § "Setup" (the OpenTelemetry setup block).
//
// Near-duplicate of docs/how-to/MAF/observability.md § "Setup"; both are compiled because either
// can drift independently. Snippet `using` lines are hoisted above the file-scoped namespace, plus
// the same two the doc omits (see Observability/Setup.cs for the note).
using Microsoft.Agents.AI;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Temporalio.Extensions.OpenTelemetry;
using TemporalCommunity.Extensions.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;

namespace DocSnippets.Usage;

internal static class Setup
{
    internal static void Configure(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#setup (lines 911-947)
        const string mafTelemetrySource = "MyCompany.MyAgent";

        // 1. Configure the OTel tracer provider with all relevant sources
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(
                TracingInterceptor.ClientSource.Name,      // Temporal client spans (StartWorkflow, etc.)
                TracingInterceptor.WorkflowsSource.Name,   // Temporal workflow spans
                TracingInterceptor.ActivitiesSource.Name,  // Temporal activity spans (RunActivity)
                TemporalAgentTelemetry.ActivitySourceName, // Temporal correlation spans
                mafTelemetrySource)                        // optional MAF canonical GenAI spans
            .AddOtlpExporter()
            .Build();

        // 2. Add the tracing interceptor to the Temporal client
        builder.Services.AddTemporalClient(opts =>
        {
            opts.TargetHost  = "localhost:7233";
            opts.Interceptors = new[] { new TracingInterceptor() };
        });

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
        // END SNIPPET docs/how-to/MAF/usage.md#setup
    }
}
