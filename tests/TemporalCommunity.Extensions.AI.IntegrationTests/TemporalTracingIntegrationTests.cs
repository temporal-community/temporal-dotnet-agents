using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry.Context.Propagation;
using Temporalio.Client;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public sealed class TemporalTracingIntegrationTests
{
    [Fact]
    public async Task UpdateHandler_IsLinkedToUpdateCallerAndCorrelatedByWorkflowId()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        var clientOptions = (TemporalClientOptions)environment.Client.Options.Clone();
        clientOptions.Interceptors =
        [
            new TracingInterceptor(new TracingInterceptorOptions
            {
                Propagator = new TraceContextPropagator(),
            }),
        ];
        var client = new TemporalClient(environment.Client.Connection, clientOptions);

        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == "Temporalio.Extensions.OpenTelemetry.Client"
                || source.Name == "Temporalio.Extensions.OpenTelemetry.Workflow",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var taskQueue = $"tracing-probe-{Guid.NewGuid():N}";
        var workflowId = $"tracing-probe-{Guid.NewGuid():N}";
        using var worker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(taskQueue).AddWorkflow<TracingProbeWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var handle = await client.StartWorkflowAsync(
                (TracingProbeWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions(workflowId, taskQueue));

            await handle.SignalAsync(workflow => workflow.ObserveAsync());
            await handle.ExecuteUpdateAsync(workflow => workflow.CompleteAsync());
            await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));

            var history = await handle.FetchHistoryAsync();
            var replay = await new WorkflowReplayer(
                    new WorkflowReplayerOptions().AddWorkflow<TracingProbeWorkflow>())
                .ReplayWorkflowAsync(history, throwOnReplayFailure: false);
            Assert.Null(replay.ReplayFailure);
        });

        var spans = activities.ToArray();
        var updateSpan = Assert.Single(spans, span =>
            span.DisplayName == "UpdateWorkflow:Complete");
        var handlerSpan = Assert.Single(spans, span =>
            span.DisplayName == "HandleUpdate:Complete");
        var signalSpan = Assert.Single(spans, span =>
            span.DisplayName == "SignalWorkflow:Observe");
        var signalHandlerSpan = Assert.Single(spans, span =>
            span.DisplayName == "HandleSignal:Observe");

        Assert.Equal(workflowId, updateSpan.GetTagItem("temporalWorkflowID"));
        Assert.Equal(workflowId, handlerSpan.GetTagItem("temporalWorkflowID"));
        Assert.Equal(workflowId, signalSpan.GetTagItem("temporalWorkflowID"));
        Assert.Equal(workflowId, signalHandlerSpan.GetTagItem("temporalWorkflowID"));
        Assert.Contains(
            handlerSpan.Links,
            link =>
                link.Context.TraceId == updateSpan.Context.TraceId
                && link.Context.SpanId == updateSpan.Context.SpanId);
        Assert.Contains(
            signalHandlerSpan.Links,
            link =>
                link.Context.TraceId == signalSpan.Context.TraceId
                && link.Context.SpanId == signalSpan.Context.SpanId);
    }

    [Workflow]
    public sealed class TracingProbeWorkflow
    {
        private static readonly ActivitySource Diagnostics = new("TemporalAgents.TracingProbe");
        private bool _completed;

        [WorkflowRun]
        public Task RunAsync() => Workflow.WaitConditionAsync(() => _completed);

        [WorkflowSignal("Observe")]
        public Task ObserveAsync()
        {
            using (Diagnostics.TrackWorkflowDiagnosticActivity("tracing_probe.observe"))
            {
                return Task.CompletedTask;
            }
        }

        [WorkflowUpdate("Complete")]
        public Task CompleteAsync()
        {
            _completed = true;
            return Task.CompletedTask;
        }
    }
}
