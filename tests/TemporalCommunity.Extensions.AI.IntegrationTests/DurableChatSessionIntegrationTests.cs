using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

[Collection("AI Integration Tests")]
public class DurableChatSessionIntegrationTests
{
    private readonly IntegrationTestFixture _fixture;

    public DurableChatSessionIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ManagedGetChatStep_TagsReachActivityNotProvider()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var providerClient = new MetadataRecordingChatClient();
        var taskQueue = $"managed-metadata-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(providerClient);
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
            });

        using var host = builder.Build();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DurableChatTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        await host.StartAsync();
        try
        {
            var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
            var options = new ChatOptions
            {
                Instructions = "managed instructions",
            }
                .WithChatClientTag("tenant", "acme")
                .WithActivityTimeout(TimeSpan.FromSeconds(20))
                .WithMaxRetryAttempts(2);
            options.AdditionalProperties!["user.custom"] = "keep";

            await sessionClient.SendAsync(
                $"managed-metadata-{Guid.NewGuid():N}",
                [new ChatMessage(ChatRole.User, "hello")],
                options);

            Assert.Equal("acme", providerClient.ActivityTags["tenant"]);
            Assert.Equal("keep", providerClient.Options?.AdditionalProperties?["user.custom"]?.ToString());
            Assert.Equal("managed instructions", providerClient.Options?.Instructions);
            Assert.DoesNotContain(
                providerClient.Options!.AdditionalProperties!,
                pair => pair.Key.StartsWith("temporal.", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SingleTurn_SendsMessageAndReceivesResponse()
    {
        var conversationId = $"single-turn-{Guid.NewGuid():N}";
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello AI!") };

        var response = await _fixture.SessionClient.SendAsync(conversationId, messages);

        Assert.NotNull(response);
        Assert.Single(response.Messages);
        Assert.Contains("Hello AI!", response.Messages[0].Text);
        // The response carries the model's last assistant message.
        Assert.Contains("Hello AI!", response.Text);
        // Per-turn usage details flow through the entry.
        Assert.NotNull(response.Usage);
        // CorrelationId is auto-generated when not supplied.
        Assert.False(string.IsNullOrEmpty(response.CorrelationId));
    }

    [Fact]
    public async Task MultiTurn_AccumulatesHistory()
    {
        var conversationId = $"multi-turn-{Guid.NewGuid():N}";

        // Turn 1
        var response1 = await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "First message")]);

        Assert.NotNull(response1);

        // Turn 2
        var response2 = await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Second message")]);

        Assert.NotNull(response2);

        // Query history
        var history = await _fixture.SessionClient.GetHistoryAsync(conversationId);

        // Each turn produces a request entry + a response entry → 4 entries for 2 turns.
        Assert.Equal(4, history.Count);
        Assert.IsType<DurableSessionRequest>(history[0]);
        Assert.IsType<DurableSessionResponse>(history[1]);
        Assert.IsType<DurableSessionRequest>(history[2]);
        Assert.IsType<DurableSessionResponse>(history[3]);

        // Request and response of the same turn share a correlation ID.
        Assert.Equal(history[0].CorrelationId, history[1].CorrelationId);
        Assert.Equal(history[2].CorrelationId, history[3].CorrelationId);
        // Different turns produce different correlation IDs.
        Assert.NotEqual(history[0].CorrelationId, history[2].CorrelationId);
    }

    [Fact]
    public async Task SameConversationId_ReusesSameWorkflow()
    {
        var conversationId = $"reuse-{Guid.NewGuid():N}";

        await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "First")]);

        await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Second")]);

        // Both should be in the same workflow — verify via history
        var history = await _fixture.SessionClient.GetHistoryAsync(conversationId);
        Assert.Equal(4, history.Count);
    }

    [Fact]
    public async Task TokenUsage_IsReported()
    {
        var conversationId = $"usage-{Guid.NewGuid():N}";
        var response = await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "test")]);

        Assert.NotNull(response.Usage);
        Assert.True(response.Usage!.InputTokenCount > 0);
        Assert.True(response.Usage!.OutputTokenCount > 0);
    }

    [Fact]
    public async Task UsageDetails_AreQueryablePerTurn_ViaGetHistory()
    {
        var conversationId = $"usage-history-{Guid.NewGuid():N}";

        await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "First")]);

        await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Second")]);

        var history = await _fixture.SessionClient.GetHistoryAsync(conversationId);

        // Each response entry carries the per-turn UsageDetails.
        var responseEntries = history.OfType<DurableSessionResponse>().ToList();
        Assert.Equal(2, responseEntries.Count);
        foreach (var entry in responseEntries)
        {
            Assert.NotNull(entry.Usage);
            Assert.True(entry.Usage!.InputTokenCount > 0);
            Assert.True(entry.Usage!.OutputTokenCount > 0);
        }
    }

    [Fact]
    public async Task UserSuppliedCorrelationId_IsPreserved_OnRequestAndResponseEntries()
    {
        var conversationId = $"correlation-{Guid.NewGuid():N}";
        var customCorrelationId = "trace-abc-123";

        var response = await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "hello")],
            options: null,
            correlationId: customCorrelationId);

        Assert.Equal(customCorrelationId, response.CorrelationId);

        var history = await _fixture.SessionClient.GetHistoryAsync(conversationId);
        Assert.Equal(2, history.Count);
        Assert.Equal(customCorrelationId, history[0].CorrelationId);
        Assert.Equal(customCorrelationId, history[1].CorrelationId);
    }

    [Fact]
    public async Task NullCorrelationId_AutoGeneratesGuid()
    {
        var conversationId = $"correlation-auto-{Guid.NewGuid():N}";

        var response = await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "hello")]);

        // Auto-generated correlation IDs are 32-char hex (Guid "N" format).
        Assert.False(string.IsNullOrEmpty(response.CorrelationId));
        Assert.Equal(32, response.CorrelationId.Length);
    }

    /// <summary>
    /// Verify the Update-with-Start failure path: when the workflow's
    /// <c>[WorkflowUpdateValidator]</c> rejects the update (empty messages list),
    /// <see cref="DurableChatSessionClient.SendAsync"/> surfaces a
    /// <see cref="WorkflowUpdateFailedException"/> AND the workflow was still started
    /// by the atomic RPC — i.e. <c>DescribeAsync</c> succeeds and reports an extant
    /// execution.
    /// </summary>
    /// <remarks>
    /// Failure-injection mechanism: the validator <c>ValidateChat</c> in
    /// <c>DurableChatWorkflow</c> throws <c>ArgumentException</c> ("At least one
    /// message is required.") when the message list is empty. The Temporal SDK
    /// converts that into a <see cref="WorkflowUpdateFailedException"/> on the client
    /// side. Because <c>ExecuteUpdateWithStartWorkflowAsync</c> delivers the start and
    /// the update as a single atomic RPC the workflow is always started before the
    /// validator runs — the "SDK caveat" comment in the production code.
    /// </remarks>
    [Fact]
    public async Task SendAsync_UpdateValidatorRejects_ThrowsUpdateFailedAndWorkflowStarted()
    {
        var conversationId = $"update-fail-{Guid.NewGuid():N}";

        // Pass an empty message list — ValidateChat throws ArgumentException
        // ("At least one message is required.") which the SDK surfaces as
        // WorkflowUpdateFailedException.
        await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
            _fixture.SessionClient.SendAsync(
                conversationId,
                messages: []));

        // The workflow was still started atomically before the validator ran.
        // DescribeAsync throws RpcException("not found") when the workflow does not
        // exist — asserting it succeeds (no throw) proves the workflow was started.
        var workflowId = _fixture.SessionClient.GetWorkflowId(conversationId);
        var handle = _fixture.Client.GetWorkflowHandle(workflowId);
        var description = await handle.DescribeAsync();
        Assert.NotNull(description);
    }

    private sealed class MetadataRecordingChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public Dictionary<string, object?> ActivityTags { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            if (Activity.Current is { } activity)
            {
                foreach (var tag in activity.TagObjects)
                {
                    ActivityTags[tag.Key] = tag.Value;
                }
            }
            return Task.FromResult(
                new ChatResponse([new ChatMessage(ChatRole.Assistant, "managed response")])
                {
                    Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 },
                });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class NoopEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } = new("noop", null, null, 1);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new[] { 0f })).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
