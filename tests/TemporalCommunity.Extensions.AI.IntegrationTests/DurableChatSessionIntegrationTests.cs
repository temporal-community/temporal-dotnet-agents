using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Context.Propagation;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.AI.Exceptions;
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
        var clientOptions = (TemporalClientOptions)env.Client.Options.Clone();
        clientOptions.DataConverter = DurableAIDataConverter.Instance;
        clientOptions.Interceptors =
        [
            new TracingInterceptor(new TracingInterceptorOptions
            {
                Propagator = new TraceContextPropagator(),
            }),
        ];
        var client = new TemporalClient(env.Client.Connection, clientOptions);

        var providerClient = new MetadataRecordingChatClient();
        var taskQueue = $"managed-metadata-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddSingleton<IChatClient>(providerClient);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
            });

        using var host = builder.Build();
        var clientSpans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == DurableChatTelemetry.ActivitySourceName
                || source.Name.StartsWith(
                    "Temporalio.Extensions.OpenTelemetry.",
                    StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == DurableChatTelemetry.ActivitySourceName)
                {
                    clientSpans.Add(activity);
                }
            },
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

            var conversationId = $"managed-metadata-{Guid.NewGuid():N}";
            await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "hello")],
                options);

            var sendSpan = Assert.Single(clientSpans, span =>
                span.DisplayName == DurableChatTelemetry.ChatSendSpanName);
            Assert.Equal(
                conversationId,
                sendSpan.GetTagItem(DurableChatTelemetry.GenAiConversationIdAttribute));

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
    public async Task IncompleteResponse_ReturnsDiagnosticsAndPersistsOnlySentinel()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, []))
            {
                FinishReason = ChatFinishReason.Length,
                ResponseId = "incomplete-response-1",
                ModelId = "incomplete-model",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["provider.trace"] = "incomplete-trace",
                },
                Usage = new UsageDetails { OutputTokenCount = 4000 },
            },
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "recovered next turn"))
            {
                FinishReason = ChatFinishReason.Stop,
            },
        ]);
        var taskQueue = $"ordinary-incomplete-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var sessionClient = host.Services.GetRequiredService<IDurableChatSessionClient>();
            var conversationId = $"ordinary-incomplete-{Guid.NewGuid():N}";

            var incomplete = await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "produce a long answer")]);

            Assert.Equal(DurableTurnCompletionReason.IncompleteResponse, incomplete.CompletionReason);
            Assert.Equal(ChatFinishReason.Length, incomplete.FinishReason);
            Assert.Equal("incomplete-response-1", incomplete.ResponseId);
            Assert.Equal("incomplete-model", incomplete.ModelId);
            Assert.Equal("incomplete-trace", incomplete.ResponseAdditionalProperties!["provider.trace"].GetString());
            Assert.Equal(4000, incomplete.Usage!.OutputTokenCount);
            Assert.Empty(Assert.Single(incomplete.Messages).Contents);

            var history = await sessionClient.GetHistoryAsync(conversationId);
            var stored = Assert.IsType<DurableSessionResponse>(history[1]);
            Assert.Equal(DurableTurnCompletionReason.IncompleteResponse, stored.CompletionReason);
            Assert.Equal(ChatFinishReason.Length, stored.FinishReason);
            Assert.Equal("incomplete-response-1", stored.ResponseId);
            Assert.Equal("incomplete-model", stored.ModelId);
            Assert.Equal("incomplete-trace", stored.ResponseAdditionalProperties!["provider.trace"].GetString());
            Assert.Equal(
                DurableManagedLoopHistory.IncompleteResponseSentinelText,
                Assert.Single(stored.Messages).Text);
            Assert.DoesNotContain(
                stored.Messages.SelectMany(message => message.Contents),
                content => content is FunctionCallContent or FunctionResultContent);

            var next = await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "try a shorter answer")]);

            Assert.Equal(DurableTurnCompletionReason.FinalResponse, next.CompletionReason);
            Assert.Equal(ChatFinishReason.Stop, next.FinishReason);
            Assert.Contains(scripted.Calls[1].Messages, message =>
                message.Text == DurableManagedLoopHistory.IncompleteResponseSentinelText);
            Assert.DoesNotContain(
                scripted.Calls[1].Messages.SelectMany(message => message.Contents),
                content => content is FunctionCallContent or FunctionResultContent);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ManagedSession_StringInputAndProviderMetadata_RoundTrip()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello back"))
            {
                ResponseId = "provider-response-1",
                ModelId = "model-1",
                FinishReason = ChatFinishReason.Stop,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["provider.trace"] = "trace-1",
                },
            },
        ]);
        var taskQueue = $"managed-response-metadata-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var sessionClient = host.Services.GetRequiredService<IDurableChatSessionClient>();
            var conversationId = $"managed-response-metadata-{Guid.NewGuid():N}";
            var response = await sessionClient.SendAsync(
                conversationId,
                "hello",
                requestId: "logical-request-1");

            Assert.Equal("hello back", response.Text);
            Assert.Equal("logical-request-1", response.RequestId);
            Assert.Equal("provider-response-1", response.ResponseId);
            Assert.Equal("model-1", response.ModelId);
            Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
            Assert.Equal("trace-1", response.ResponseAdditionalProperties!["provider.trace"].GetString());
            Assert.Equal("hello", scripted.Calls.Single().Messages.Single().Text);

            var stored = Assert.IsType<DurableSessionResponse>(
                (await sessionClient.GetHistoryAsync(conversationId))[1]);
            Assert.Equal("logical-request-1", stored.RequestId);
            Assert.Equal("provider-response-1", stored.ResponseId);
            Assert.Equal("model-1", stored.ModelId);
            Assert.Equal("trace-1", stored.ResponseAdditionalProperties!["provider.trace"].GetString());
            var storedRequest = Assert.IsType<DurableSessionRequest>(
                (await sessionClient.GetHistoryAsync(conversationId))[0]);
            Assert.Equal("logical-request-1", storedRequest.RequestId);
            Assert.StartsWith("tai-request-v1:", storedRequest.InputFingerprint, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(storedRequest.EntryId));
            Assert.False(string.IsNullOrWhiteSpace(stored.EntryId));
            Assert.NotEqual(storedRequest.EntryId, stored.EntryId);
            Assert.False(string.IsNullOrWhiteSpace(storedRequest.TurnId));
            Assert.Equal(storedRequest.TurnId, stored.TurnId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SendStructuredAsync_TransportsSchemaPreservesMetadataAndFingerprintsItsType()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"summary":"clear skies"}"""))
            {
                ResponseId = "structured-response",
                ConversationId = "provider-conversation",
                ModelId = "model-1",
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 8, OutputTokenCount = 4 },
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["provider.trace"] = "structured-trace",
                },
            },
        ]);
        var taskQueue = $"managed-structured-output-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var sessionClient = host.Services.GetRequiredService<IDurableChatSessionClient>();
            var conversationId = $"managed-structured-output-{Guid.NewGuid():N}";
            var response = await sessionClient.SendStructuredAsync<StructuredSummary>(
                conversationId,
                "Return a summary as JSON.",
                requestId: "structured-request");

            Assert.Equal("clear skies", response.Result.Summary);
            Assert.Equal("structured-response", response.ResponseId);
            Assert.Equal("provider-conversation", response.ConversationId);
            Assert.Equal("model-1", response.ModelId);
            Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
            Assert.Equal(8, response.Usage?.InputTokenCount);
            Assert.Equal("structured-trace", Assert.IsType<System.Text.Json.JsonElement>(
                response.AdditionalProperties!["provider.trace"]).GetString());

            var format = Assert.IsType<ChatResponseFormatJson>(scripted.Calls.Single().Options?.ResponseFormat);
            Assert.NotNull(format.Schema);
            Assert.Equal("object", format.Schema.Value.GetProperty("type").GetString());

            var conflict = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                sessionClient.SendStructuredAsync<StructuredCount>(
                    conversationId,
                    "Return a summary as JSON.",
                    requestId: "structured-request"));
            Assert.Equal(
                "DurableTurnConflict",
                Assert.IsType<ApplicationFailureException>(conflict.InnerException).ErrorType);
            Assert.Single(scripted.Calls);

            var storedResponse = Assert.IsType<DurableSessionResponse>(
                (await sessionClient.GetHistoryAsync(conversationId))[1]);
            Assert.Equal("provider-conversation", storedResponse.ProviderConversationId);
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

    public sealed class StructuredSummary
    {
        public string Summary { get; set; } = string.Empty;
    }

    public sealed class StructuredCount
    {
        public int Count { get; set; }
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

        var workflowHandle = _fixture.Client.GetWorkflowHandle(
            _fixture.SessionClient.GetWorkflowId(conversationId));
        var resolverSchedules = 0;
        await foreach (var historyEvent in workflowHandle.FetchHistoryEventsAsync())
        {
            if (historyEvent.ActivityTaskScheduledEventAttributes?.ActivityType.Name
                == "TemporalCommunity.Extensions.AI.ResolveDurableToolsets")
            {
                resolverSchedules++;
            }
        }

        Assert.Equal(1, resolverSchedules);
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
        var requestFailure = await Assert.ThrowsAsync<DurableChatRequestException>(() =>
            _fixture.SessionClient.SendAsync(conversationId, messages: []));
        Assert.IsType<WorkflowUpdateFailedException>(requestFailure.InnerException);

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

}
