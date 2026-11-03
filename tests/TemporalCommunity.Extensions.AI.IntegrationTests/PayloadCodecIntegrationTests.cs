using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public sealed class PayloadCodecIntegrationTests
{
    [Fact]
    public async Task EncodedWorkflowHistory_CompletesAndReplaysWithCompatibleDecoder()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        var codec = new DurableAIGzipPayloadCodec(new DurableAIGzipPayloadCodecOptions
        {
            MinimumPayloadSizeBytes = 1,
            MaximumEncodedPayloadSizeBytes = 1024 * 1024,
            MaximumDecodedPayloadSizeBytes = 2 * 1024 * 1024,
        });
        var converter = DurableAIDataConverter.CreateDataConverter(codec);
        environment.Client.Options.DataConverter = converter;
        var taskQueue = $"payload-codec-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(environment.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<PayloadCodecEchoWorkflow>();
        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var input = string.Concat(Enumerable.Repeat("durable-codec-payload;", 2000));
            var handle = await environment.Client.StartWorkflowAsync(
                (PayloadCodecEchoWorkflow workflow) => workflow.RunAsync(input),
                new WorkflowOptions($"payload-codec-{Guid.NewGuid():N}", taskQueue));

            Assert.Equal(input, await handle.GetResultAsync());

            var history = await handle.FetchHistoryAsync();
            var expectedEncoding = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(DurableAIGzipPayloadCodec.EncodingValue));
            Assert.Contains(expectedEncoding, history.ToJson(), StringComparison.Ordinal);

            var replayerOptions = new WorkflowReplayerOptions { DataConverter = converter };
            replayerOptions.AddWorkflow<PayloadCodecEchoWorkflow>();
            var replayResult = await new WorkflowReplayer(replayerOptions)
                .ReplayWorkflowAsync(history, throwOnReplayFailure: false);

            Assert.Null(replayResult.ReplayFailure);

            var incompatibleOptions = new WorkflowReplayerOptions();
            incompatibleOptions.AddWorkflow<PayloadCodecEchoWorkflow>();
            var incompatibleReplay = await new WorkflowReplayer(incompatibleOptions)
                .ReplayWorkflowAsync(history, throwOnReplayFailure: false);

            var replayFailure = Assert.IsAssignableFrom<Exception>(incompatibleReplay.ReplayFailure);
            Assert.Contains(
                "encoding",
                replayFailure.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                DurableAIGzipPayloadCodec.EncodingValue,
                replayFailure.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task EncodedDurableSessionResponse_RoundTripsAcrossClientWorkerAndReplay()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        var codec = new DurableAIGzipPayloadCodec(new DurableAIGzipPayloadCodecOptions
        {
            MinimumPayloadSizeBytes = 1,
            MaximumEncodedPayloadSizeBytes = 1024 * 1024,
            MaximumDecodedPayloadSizeBytes = 2 * 1024 * 1024,
        });
        var converter = DurableAIDataConverter.CreateDataConverter(codec);
        environment.Client.Options.DataConverter = converter;
        var taskQueue = $"payload-codec-session-response-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(environment.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<PayloadCodecSessionResponseWorkflow>();
        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var messageText = string.Concat(Enumerable.Repeat("structured-response-payload;", 2000));
            var response = DurableSessionResponse.FromChatResponse(
                "codec-correlation",
                new Microsoft.Extensions.AI.ChatResponse(
                    new Microsoft.Extensions.AI.ChatMessage(
                        Microsoft.Extensions.AI.ChatRole.Assistant,
                        messageText))
                {
                    ResponseId = "provider-response",
                    ConversationId = "provider-conversation",
                    ModelId = "model-1",
                    FinishReason = Microsoft.Extensions.AI.ChatFinishReason.Stop,
                    AdditionalProperties = new Microsoft.Extensions.AI.AdditionalPropertiesDictionary
                    {
                        ["provider.trace"] = "trace-1",
                    },
                },
                DateTimeOffset.UnixEpoch,
                DurableTurnCompletionReason.FinalResponse,
                requestId: "codec-request");

            var handle = await environment.Client.StartWorkflowAsync(
                (PayloadCodecSessionResponseWorkflow workflow) => workflow.RunAsync(response),
                new WorkflowOptions($"payload-codec-session-response-{Guid.NewGuid():N}", taskQueue));

            var result = await handle.GetResultAsync();
            Assert.Equal(messageText, result.Text);
            Assert.Equal("codec-request", result.RequestId);
            Assert.Equal("provider-response", result.ResponseId);
            Assert.Equal("provider-conversation", result.ProviderConversationId);
            Assert.Equal("model-1", result.ModelId);
            Assert.Equal(
                "trace-1",
                result.ResponseAdditionalProperties!["provider.trace"].GetString());

            var history = await handle.FetchHistoryAsync();
            var expectedEncoding = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(DurableAIGzipPayloadCodec.EncodingValue));
            Assert.Contains(expectedEncoding, history.ToJson(), StringComparison.Ordinal);

            var replayOptions = new WorkflowReplayerOptions { DataConverter = converter };
            replayOptions.AddWorkflow<PayloadCodecSessionResponseWorkflow>();
            var replay = await new WorkflowReplayer(replayOptions)
                .ReplayWorkflowAsync(history, throwOnReplayFailure: false);
            Assert.Null(replay.ReplayFailure);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}

[Workflow("TemporalCommunity.Extensions.AI.IntegrationTests.PayloadCodecEchoWorkflow")]
public sealed class PayloadCodecEchoWorkflow
{
    [WorkflowRun]
    public Task<string> RunAsync(string input) => Task.FromResult(input);
}

[Workflow("TemporalCommunity.Extensions.AI.IntegrationTests.PayloadCodecSessionResponseWorkflow")]
public sealed class PayloadCodecSessionResponseWorkflow
{
    [WorkflowRun]
    public Task<DurableSessionResponse> RunAsync(DurableSessionResponse input) =>
        Task.FromResult(input);
}
