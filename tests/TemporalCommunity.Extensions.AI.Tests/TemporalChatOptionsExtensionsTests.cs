using Microsoft.Extensions.AI;
using System.Text.Json;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class TemporalChatOptionsExtensionsTests
{
    [Fact]
    public void TemporalGetters_ReadDurableConverterJsonElements()
    {
        var original = new ChatOptions()
            .WithActivityTimeout(TimeSpan.FromMinutes(2))
            .WithHeartbeatTimeout(TimeSpan.FromSeconds(15))
            .WithMaxRetryAttempts(4)
            .WithChatClientKey("provider")
            .WithChatClientTag("tenant", "acme");

        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var payload = converter.ToPayload(new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            Options = original,
            ConversationId = "round-trip",
        });
        var roundTripped = (DurableChatInput)converter.ToValue(payload, typeof(DurableChatInput))!;
        var options = Assert.IsType<ChatOptions>(roundTripped.Options);

        Assert.Equal(TimeSpan.FromMinutes(2), options.GetActivityTimeout());
        Assert.Equal(TimeSpan.FromSeconds(15), options.GetHeartbeatTimeout());
        Assert.Equal(4, options.GetMaxRetryAttempts());
        Assert.Equal("provider", options.GetChatClientKey());
        Assert.Contains(options.GetChatClientTags(), tag => tag is { Key: "tenant", Value: "acme" });
    }

    [Fact]
    public void WithActivityTimeout_SetsProperty()
    {
        var options = new ChatOptions();
        options.WithActivityTimeout(TimeSpan.FromMinutes(10));

        Assert.NotNull(options.AdditionalProperties);
        Assert.Equal(TimeSpan.FromMinutes(10), options.GetActivityTimeout());
    }

    [Fact]
    public void WithMaxRetryAttempts_SetsProperty()
    {
        var options = new ChatOptions();
        options.WithMaxRetryAttempts(5);

        Assert.NotNull(options.AdditionalProperties);
        Assert.Equal(5, options.GetMaxRetryAttempts());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithMaxRetryAttempts_RejectsNonPositiveValues(int maxAttempts)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ChatOptions().WithMaxRetryAttempts(maxAttempts));

        Assert.Equal("maxAttempts", exception.ParamName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void GetMaxRetryAttempts_RejectsNonPositiveRawStringValues(string rawValue)
    {
        var options = new ChatOptions();
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[TemporalChatOptionsExtensions.MaxRetryAttemptsKey] = rawValue;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.GetMaxRetryAttempts());
        Assert.Equal(TemporalChatOptionsExtensions.MaxRetryAttemptsKey, exception.ParamName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void GetMaxRetryAttempts_RejectsNonPositiveRawJsonValues(string rawValue)
    {
        using var document = JsonDocument.Parse(rawValue);
        var options = new ChatOptions();
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[TemporalChatOptionsExtensions.MaxRetryAttemptsKey] = document.RootElement.Clone();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.GetMaxRetryAttempts());
        Assert.Equal(TemporalChatOptionsExtensions.MaxRetryAttemptsKey, exception.ParamName);
    }

    [Fact]
    public void WithHeartbeatTimeout_SetsProperty()
    {
        var options = new ChatOptions();
        options.WithHeartbeatTimeout(TimeSpan.FromMinutes(3));

        Assert.NotNull(options.AdditionalProperties);
        Assert.Equal(TimeSpan.FromMinutes(3), options.GetHeartbeatTimeout());
    }

    [Fact]
    public void GetActivityTimeout_ReturnsNullWhenNotSet()
    {
        var options = new ChatOptions();
        Assert.Null(options.GetActivityTimeout());
    }

    [Fact]
    public void GetActivityTimeout_ReturnsNullForNullOptions()
    {
        ChatOptions? options = null;
        Assert.Null(options.GetActivityTimeout());
    }

    [Fact]
    public void GetActivityTimeout_ReturnsValueWhenSet()
    {
        var options = new ChatOptions();
        options.WithActivityTimeout(TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(15), options.GetActivityTimeout());
    }

    [Fact]
    public void GetMaxRetryAttempts_ReturnsValueWhenSet()
    {
        var options = new ChatOptions();
        options.WithMaxRetryAttempts(3);
        Assert.Equal(3, options.GetMaxRetryAttempts());
    }

    [Fact]
    public void GetMaxRetryAttempts_ReturnsNullWhenNotSet()
    {
        var options = new ChatOptions();
        Assert.Null(options.GetMaxRetryAttempts());
    }

    [Fact]
    public void GetHeartbeatTimeout_ReturnsValueWhenSet()
    {
        var options = new ChatOptions();
        options.WithHeartbeatTimeout(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), options.GetHeartbeatTimeout());
    }

    [Fact]
    public void FluentChaining_Works()
    {
        var options = new ChatOptions()
            .WithActivityTimeout(TimeSpan.FromMinutes(10))
            .WithMaxRetryAttempts(3)
            .WithHeartbeatTimeout(TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(10), options.GetActivityTimeout());
        Assert.Equal(3, options.GetMaxRetryAttempts());
        Assert.Equal(TimeSpan.FromMinutes(2), options.GetHeartbeatTimeout());
    }

    [Fact]
    public void WithActivityTimeout_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => TemporalChatOptionsExtensions.WithActivityTimeout(null!, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Constants_AreCorrect()
    {
        Assert.Equal("temporal.activity.timeout", TemporalChatOptionsExtensions.ActivityTimeoutKey);
        Assert.Equal("temporal.retry.max_attempts", TemporalChatOptionsExtensions.MaxRetryAttemptsKey);
        Assert.Equal("temporal.heartbeat.timeout", TemporalChatOptionsExtensions.HeartbeatTimeoutKey);
    }

    [Fact]
    public void WithChatClientKey_SetsKeyInAdditionalProperties()
    {
        var options = new ChatOptions();
        options.WithChatClientKey("my-client");

        Assert.NotNull(options.AdditionalProperties);
        Assert.True(options.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.ChatClientKeySettingKey));
        Assert.Equal("my-client", options.AdditionalProperties[TemporalChatOptionsExtensions.ChatClientKeySettingKey]);
    }

    [Fact]
    public void WithChatClientKey_ReturnsOptionsForChaining()
    {
        var options = new ChatOptions();
        var returned = options.WithChatClientKey("my-client");

        Assert.Same(options, returned);
    }

    [Fact]
    public void WithChatClientKey_ThrowsOnNullKey()
    {
        var options = new ChatOptions();
        Assert.Throws<ArgumentNullException>(() => options.WithChatClientKey(null!));
    }

    [Fact]
    public void WithChatClientKey_ThrowsOnEmptyKey()
    {
        var options = new ChatOptions();
        Assert.Throws<ArgumentException>(() => options.WithChatClientKey(""));
    }

    [Fact]
    public void GetChatClientKey_ReturnsNullWhenNotSet()
    {
        var options = new ChatOptions();
        Assert.Null(options.GetChatClientKey());
    }

    [Fact]
    public void GetChatClientKey_ReturnsKeyWhenSet()
    {
        var options = new ChatOptions();
        options.WithChatClientKey("my-client");
        Assert.Equal("my-client", options.GetChatClientKey());
    }

    [Fact]
    public async Task ProviderPreparation_RemovesChatClientKey()
    {
        var options = new ChatOptions()
            .WithActivityTimeout(TimeSpan.FromMinutes(5))
            .WithChatClientKey("my-client");
        options.AdditionalProperties!["user.custom"] = "keep";

        // Provider preparation is internal — exercise it through DurableChatClient pass-through
        // (not in workflow context) so the inner client receives stripped options.
        var captured = (ChatOptions?)null;
        var innerClient = new CapturingChatClient(opts => captured = opts);
        var execOptions = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, execOptions);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        Assert.NotNull(captured?.AdditionalProperties);
        Assert.False(captured!.AdditionalProperties!.ContainsKey(TemporalChatOptionsExtensions.ChatClientKeySettingKey));
        Assert.False(captured.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.ActivityTimeoutKey));
        Assert.True(captured.AdditionalProperties.ContainsKey("user.custom"));
    }

    [Fact]
    public void WithChatClientTag_SetsTagAtPrefixedKey()
    {
        var options = new ChatOptions();
        options.WithChatClientTag("tenant", "acme-corp");

        Assert.NotNull(options.AdditionalProperties);
        Assert.True(options.AdditionalProperties.ContainsKey(
            TemporalChatOptionsExtensions.ChatClientTagsKeyPrefix + "tenant"));
    }

    [Fact]
    public void WithChatClientTag_MultipleCalls_AccumulateTags()
    {
        var options = new ChatOptions();
        options.WithChatClientTag("tenant", "acme-corp");
        options.WithChatClientTag("request_id", "abc-123");

        var tags = options.GetChatClientTags();
        Assert.Equal(2, tags.Count);
        Assert.Contains(tags, t => t.Key == "tenant" && t.Value == "acme-corp");
        Assert.Contains(tags, t => t.Key == "request_id" && t.Value == "abc-123");
    }

    [Fact]
    public void WithChatClientTag_RepeatedName_LatestValueWins()
    {
        var options = new ChatOptions();
        options.WithChatClientTag("tenant", "first");
        options.WithChatClientTag("tenant", "second");

        var tags = options.GetChatClientTags();
        Assert.Single(tags);
        Assert.Equal("second", tags[0].Value);
    }

    [Fact]
    public void WithChatClientTag_NullName_Throws()
    {
        // ArgumentException.ThrowIfNullOrEmpty throws ArgumentNullException for null;
        // xUnit's Assert.Throws requires exact type per the CLAUDE.md gotcha.
        var options = new ChatOptions();
        Assert.Throws<ArgumentNullException>(() => options.WithChatClientTag(null!, "value"));
    }

    [Fact]
    public void WithChatClientTag_EmptyName_Throws()
    {
        var options = new ChatOptions();
        Assert.Throws<ArgumentException>(() => options.WithChatClientTag(string.Empty, "value"));
    }

    [Fact]
    public void WithChatClientTag_NullValue_Throws()
    {
        var options = new ChatOptions();
        Assert.Throws<ArgumentNullException>(() => options.WithChatClientTag("name", null!));
    }

    [Fact]
    public void GetChatClientTags_NoTags_ReturnsEmpty()
    {
        var options = new ChatOptions();
        Assert.Empty(options.GetChatClientTags());
    }

    [Fact]
    public void GetChatClientTags_IgnoresNonTagAdditionalProperties()
    {
        // Tag prefix discrimination — must not pick up unrelated additional-properties keys.
        var options = new ChatOptions();
        options.WithChatClientTag("tenant", "acme");
        options.AdditionalProperties!["unrelated.key"] = "ignored";
        options.WithActivityTimeout(TimeSpan.FromMinutes(5));

        var tags = options.GetChatClientTags();
        Assert.Single(tags);
        Assert.Equal("tenant", tags[0].Key);
    }

    /// <summary>Minimal IChatClient stub that captures the ChatOptions it receives.</summary>
    private sealed class CapturingChatClient(Action<ChatOptions?> capture) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            capture(options);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
