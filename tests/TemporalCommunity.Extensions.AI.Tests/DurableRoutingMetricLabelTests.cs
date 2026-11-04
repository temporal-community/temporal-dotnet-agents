using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

[Collection(nameof(DurableToolsetTelemetryTests))]
public sealed class DurableRoutingMetricLabelTests
{
    [Fact]
    public async Task ModelAndToolOperations_MetricDimensionsExcludeRequestAndRoutingData()
    {
        const string tenantId = "metric-leak-tenant-7f2a";
        const string sessionId = "metric-leak-session-9c31";
        const string requestId = "metric-leak-request-4b08";
        const string modelQueue = "metric-leak-model-queue-6d51";
        const string toolQueue = "metric-leak-tool-queue-2e73";
        const string prompt = "metric-leak-private-prompt-8a40";
        var sensitiveValues = new[]
        {
            tenantId,
            sessionId,
            requestId,
            modelQueue,
            toolQueue,
            prompt,
        };

        using var capture = new MetricCapture();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = new DurableExecutionOptions
        {
            TaskQueue = "durable-metrics-default-queue",
            ModelTaskQueue = modelQueue,
            CaptureTelemetryContent = false,
        };
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(new ModelChatClient())
            .AddSingleton(options)
            .BuildServiceProvider();

        var chatActivities = new DurableChatActivities(services, loggerFactory);
        var response = await new ActivityEnvironment().RunAsync(
            () => chatActivities.GetResponseAsync(new DurableChatInput
            {
                Messages = [new ChatMessage(ChatRole.User, prompt)],
                Options = new ChatOptions { ModelId = "bounded-test-model" },
                ConversationId = sessionId,
                CorrelationId = requestId,
                RequestId = requestId,
                TurnNumber = 1,
            }));

        Assert.Equal("bounded-model-response", response.Messages.Single().Text);

        var function = AIFunctionFactory.Create(
            (string tenant, string privatePrompt) => $"{tenant}:{privatePrompt}",
            "bounded_tool");
        var declaration = DurableFunctionDeclarationSnapshot.Create(function.AsDeclarationOnly());
        var registration = new DurableToolsetRegistration(tenantId, false);
        registration.Add(new DurableRegisteredTool(
            declaration,
            new DurableChatToolOptions { TaskQueue = toolQueue },
            function,
            ActivationFactory: null));
        var resolver = new DurableToolsetActivities(
            new DurableToolsetCatalog([registration], options),
            loggerFactory);

        var manifest = await resolver.ResolveDurableToolsetsAsync(new()
        {
            ToolsetIds = [tenantId],
        });
        Assert.Single(manifest.Members);
        Assert.Equal(toolQueue, manifest.Members.Single().ToolActivityOptions.TaskQueue);
        Assert.Equal(
            modelQueue,
            new DurableChatWorkflowInputFactory(options, null, null).Create().ModelTaskQueue);

        var toolActivities = new DurableFunctionActivities(
            new Dictionary<string, AIFunction> { [function.Name] = function },
            loggerFactory);
        var toolOutput = await new ActivityEnvironment().RunAsync(
            () => toolActivities.InvokeFunctionAsync(new DurableFunctionInput
            {
                FunctionName = function.Name,
                Declaration = declaration,
                Arguments = new Dictionary<string, object?>
                {
                    ["tenant"] = tenantId,
                    ["privatePrompt"] = prompt,
                },
                ToolCallId = requestId,
                ConversationId = sessionId,
                CorrelationId = requestId,
                RequestId = requestId,
            }));

        Assert.Equal(DurableFunctionOutputStatus.Succeeded, toolOutput.Status);
        var toolResult = Assert.IsType<JsonElement>(toolOutput.Result);
        Assert.Equal(JsonValueKind.String, toolResult.ValueKind);
        Assert.Equal($"{tenantId}:{prompt}", toolResult.GetString());

        Assert.Contains(capture.Measurements, measurement =>
            measurement.Instrument == "temporal.ai.toolset.resolver.attempts"
            && measurement.Tags.Any(tag => tag.Key == "outcome" && Equals(tag.Value, "success")));
        Assert.Contains(capture.Measurements, measurement =>
            measurement.Instrument == "temporal.ai.toolset.resolver.selected_toolsets");
        Assert.Contains(capture.Measurements, measurement =>
            measurement.Instrument == "temporal.ai.toolset.resolver.selected_functions");

        var dimensions = string.Join(
            "|",
            capture.Measurements.SelectMany(measurement => measurement.Tags)
                .Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(sensitiveValues, sensitive =>
            dimensions.Contains(sensitive, StringComparison.Ordinal));
    }

    private sealed class ModelChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "bounded-model-response")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "bounded-model-response")
            {
                ModelId = "bounded-test-model",
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener listener = new();

        internal ConcurrentQueue<Measurement> Measurements { get; } = new();

        internal MetricCapture()
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == DurableChatTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Enqueue(new Measurement(instrument.Name, value, tags.ToArray())));
            listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
                Measurements.Enqueue(new Measurement(instrument.Name, value, tags.ToArray())));
            listener.Start();
        }

        public void Dispose() => listener.Dispose();
    }

    private sealed record Measurement(
        string Instrument,
        long Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
