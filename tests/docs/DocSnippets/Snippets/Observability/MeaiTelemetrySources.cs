using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TemporalCommunity.Extensions.AI;
using Temporalio.Extensions.OpenTelemetry;

namespace DocSnippets.Observability;

internal static class MeaiTelemetrySources
{
    internal static void Configure()
    {
        var builder = Host.CreateApplicationBuilder();
        // BEGIN SNIPPET docs/how-to/MEAI/observability.md#opentelemetry-setup
        builder
            .AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddSource(DurableChatTelemetry.ActivitySourceName)
                .AddSource(TracingInterceptor.ClientSource.Name)
                .AddSource(TracingInterceptor.WorkflowsSource.Name)
                .AddSource(TracingInterceptor.ActivitiesSource.Name)
                .AddSource("Experimental.Microsoft.Extensions.AI")
                .AddConsoleExporter())
            .WithMetrics(metrics => metrics
                .AddMeter("Experimental.Microsoft.Extensions.AI")
                .AddMeter(DurableChatTelemetry.MeterName)
                .AddConsoleExporter());
        // END SNIPPET docs/how-to/MEAI/observability.md#opentelemetry-setup
    }
}
