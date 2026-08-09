using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Step 4c activity-side wiring tests: verify that DurableChatActivities looks up the
/// IChatClientDecorator named by either WithChatClientFactoryKey (per-call) or
/// DurableExecutionOptions.DefaultChatClientFactoryKey (worker default), and applies it
/// around the resolved IChatClient before dispatch.
/// </summary>
public class DurableChatActivitiesDecorationTests
{
    [Fact]
    public async Task NoFactoryKey_NoDecoration_DispatchesInnerClientDirectly()
    {
        // Baseline: no per-call key, no worker default. The inner chat client is invoked
        // without any wrapping; the decorator is not consulted.
        var inner = new RecordingChatClient("inner");
        var (provider, activities) = BuildActivities(inner, options => { /* no DefaultChatClientFactoryKey */ });

        var result = await activities.GetResponseAsync(new DurableChatInput
        {
            Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
            Options = new ChatOptions(),
            ConversationId = "test",
            TurnNumber = 1,
        });

        Assert.Equal(1, inner.CallCount);
        Assert.Null(result.Messages[0].Contents.OfType<TextContent>().FirstOrDefault()?.Text == "wrapped" ? "wrapped" : null);
        provider.Dispose();
    }

    [Fact]
    public async Task PerCallFactoryKey_ResolvesAndAppliesDecorator()
    {
        var inner = new RecordingChatClient("inner");
        var (provider, activities) = BuildActivities(inner, options => { },
            registerCustom: ("custom", new TextRewritingDecorator("decorated")));

        var opts = new ChatOptions();
        opts.WithChatClientFactoryKey("custom");
        opts.WithChatClientTag("tenant", "acme");
        opts.AdditionalProperties!["user.custom"] = "keep";

        await activities.GetResponseAsync(new DurableChatInput
        {
            Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
            Options = opts,
            ConversationId = "test",
            TurnNumber = 1,
        });

        Assert.Equal(1, inner.CallCount);
        var decorator = (TextRewritingDecorator)provider.GetRequiredKeyedService<IChatClientDecorator>("custom");
        Assert.True(decorator.WasInvoked);
        Assert.Equal("custom", decorator.Options?.GetChatClientFactoryKey());
        Assert.Contains(decorator.Options!.GetChatClientTags(), tag => tag.Key == "tenant");
        Assert.Equal("keep", inner.Options?.AdditionalProperties?["user.custom"]);
        Assert.DoesNotContain(
            inner.Options!.AdditionalProperties!,
            pair => pair.Key.StartsWith("temporal.", StringComparison.Ordinal));
        provider.Dispose();
    }

    [Fact]
    public async Task GetChatStep_DecoratorSeesMetadata_ProviderDoesNot()
    {
        var inner = new RecordingChatClient("inner");
        var decorator = new TextRewritingDecorator("decorated");
        var (provider, activities) = BuildActivities(
            inner,
            options => { },
            registerCustom: ("custom", decorator));
        var options = new ChatOptions()
            .WithChatClientFactoryKey("custom")
            .WithChatClientTag("tenant", "acme")
            .WithActivityTimeout(TimeSpan.FromSeconds(10));
        options.AdditionalProperties!["user.custom"] = "keep";

        await activities.GetChatStepAsync(new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            Options = options,
            ConversationId = "test",
            TurnNumber = 1,
        });

        Assert.Equal("custom", decorator.Options?.GetChatClientFactoryKey());
        Assert.Contains(decorator.Options!.GetChatClientTags(), tag => tag.Key == "tenant");
        Assert.Equal("keep", inner.Options?.AdditionalProperties?["user.custom"]);
        Assert.DoesNotContain(
            inner.Options!.AdditionalProperties!,
            pair => pair.Key.StartsWith("temporal.", StringComparison.Ordinal));
        provider.Dispose();
    }

    [Fact]
    public async Task EmptyPerCallFactoryKey_DisablesWorkerDefault()
    {
        var inner = new RecordingChatClient("inner");
        var workerDefault = new TextRewritingDecorator("worker-default");
        var (provider, activities) = BuildActivities(
            inner,
            options => options.DefaultChatClientFactoryKey = "worker",
            registerCustom: ("worker", workerDefault));
        var options = new ChatOptions().WithChatClientFactoryKey(string.Empty);

        await activities.GetResponseAsync(new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            Options = options,
            ConversationId = "test",
            TurnNumber = 1,
        });

        Assert.False(workerDefault.WasInvoked);
        Assert.Null(inner.Options?.GetChatClientFactoryKey());
        provider.Dispose();
    }

    [Fact]
    public async Task WorkerDefaultFactoryKey_UsedWhenNoPerCall()
    {
        var inner = new RecordingChatClient("inner");
        var (provider, activities) = BuildActivities(
            inner,
            options => options.DefaultChatClientFactoryKey = "custom",
            registerCustom: ("custom", new TextRewritingDecorator("worker-default")));

        await activities.GetResponseAsync(new DurableChatInput
        {
            Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
            Options = new ChatOptions(), // no per-call factory key
            ConversationId = "test",
            TurnNumber = 1,
        });

        Assert.True(((TextRewritingDecorator)provider.GetRequiredKeyedService<IChatClientDecorator>("custom")).WasInvoked);
        provider.Dispose();
    }

    [Fact]
    public async Task UnknownFactoryKey_ThrowsDurableChatClientFactoryNotFoundException()
    {
        var inner = new RecordingChatClient("inner");
        var (provider, activities) = BuildActivities(inner, options => { });

        var opts = new ChatOptions();
        opts.WithChatClientFactoryKey("non-existent");

        var ex = await Assert.ThrowsAsync<DurableChatClientFactoryNotFoundException>(
            () => activities.GetResponseAsync(new DurableChatInput
            {
                Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
                Options = opts,
                ConversationId = "test",
                TurnNumber = 1,
            }));

        Assert.Equal("non-existent", ex.FactoryKey);
        provider.Dispose();
    }

    [Fact]
    public async Task PerCallFactoryKey_TakesPrecedenceOverWorkerDefault()
    {
        var inner = new RecordingChatClient("inner");
        var workerDefault = new TextRewritingDecorator("worker-default");
        var perCall = new TextRewritingDecorator("per-call");
        var (provider, activities) = BuildActivities(
            inner,
            options => options.DefaultChatClientFactoryKey = "worker",
            extraRegistrations: services =>
            {
                services.AddKeyedSingleton<IChatClientDecorator>("worker", workerDefault);
                services.AddKeyedSingleton<IChatClientDecorator>("call", perCall);
            });

        var opts = new ChatOptions();
        opts.WithChatClientFactoryKey("call");

        await activities.GetResponseAsync(new DurableChatInput
        {
            Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
            Options = opts,
            ConversationId = "test",
            TurnNumber = 1,
        });

        Assert.True(perCall.WasInvoked);
        Assert.False(workerDefault.WasInvoked);
        provider.Dispose();
    }

    [Fact]
    public async Task BuiltInTagsKey_ResolvesAndAppliesWithoutCustomRegistration()
    {
        // Pin the end-to-end contract: AddDurableAI pre-registers "tags", and the activity
        // dispatch path resolves + applies it transparently when the user calls
        // WithChatClientFactoryKey("tags").
        var inner = new RecordingChatClient("inner");
        var (provider, activities) = BuildActivities(inner, options => { });

        var opts = new ChatOptions();
        opts.WithChatClientFactoryKey("tags");
        opts.WithChatClientTag("tenant", "acme");

        await activities.GetResponseAsync(new DurableChatInput
        {
            Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
            Options = opts,
            ConversationId = "test",
            TurnNumber = 1,
        });

        // The "tags" decorator wraps but doesn't fail; without an ActivityListener the tags
        // silently no-op (Q10). The contract verified here: the dispatch path resolves the
        // built-in "tags" key without the user having to register a custom decorator.
        Assert.Equal(1, inner.CallCount);
        provider.Dispose();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static (ServiceProvider Provider, DurableChatActivities Activities) BuildActivities(
        IChatClient inner,
        Action<DurableExecutionOptions> configureOptions,
        (string Key, IChatClientDecorator Decorator)? registerCustom = null,
        Action<IServiceCollection>? extraRegistrations = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<ITemporalClient>());
        services.AddSingleton<IChatClient>(inner);

        var options = new DurableExecutionOptions { TaskQueue = "test" };
        configureOptions(options);
        DurableAIRegistrar.Register(services, builder: null, options: options);

        if (registerCustom is var (key, decorator))
        {
            services.AddKeyedSingleton<IChatClientDecorator>(key, decorator);
        }
        extraRegistrations?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var activities = new DurableChatActivities(provider, provider.GetService<ILoggerFactory>());
        return (provider, activities);
    }

    private sealed class RecordingChatClient(string name) : IChatClient
    {
        public string Name { get; } = name;
        public int CallCount { get; private set; }
        public ChatOptions? Options { get; private set; }

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Options = options;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, Name)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            Options = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, Name);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>Stub decorator that records whether it was invoked.</summary>
    private sealed class TextRewritingDecorator(string id) : IChatClientDecorator
    {
        public bool WasInvoked { get; private set; }
        public string Id { get; } = id;
        public ChatOptions? Options { get; private set; }

        public IChatClient Decorate(IChatClient inner, ChatOptions? options)
        {
            WasInvoked = true;
            Options = options;
            return inner;
        }
    }
}
