using OpenTelemetry.Context.Propagation;
using Temporalio.Extensions.OpenTelemetry;

namespace DocSnippets.Observability;

internal static class TracingClientSetup
{
    internal static void Configure()
    {
        // BEGIN SNIPPET docs/how-to/MEAI/observability.md#w3c-propagator
        var tracingInterceptor = new TracingInterceptor(new TracingInterceptorOptions
        {
            Propagator = new TraceContextPropagator(),
        });
        // END SNIPPET docs/how-to/MEAI/observability.md#w3c-propagator
    }
}
