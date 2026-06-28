using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public class TagsChatClientDecoratorTests
{
    [Fact]
    public void Decorate_ReturnsNonNullWrapper()
    {
        var decorator = new TagsChatClientDecorator();
        var inner = new NoopChatClient();

        var wrapped = decorator.Decorate(inner, options: null);

        Assert.NotNull(wrapped);
        Assert.NotSame(inner, wrapped);
    }

    [Fact]
    public void Decorate_NullInner_Throws()
    {
        var decorator = new TagsChatClientDecorator();
        Assert.Throws<ArgumentNullException>(() => decorator.Decorate(null!, options: null));
    }

    [Fact]
    public async Task Wrapper_AppliesTagsToActivityCurrent_WhenActivityListenerAttached()
    {
        // Set up an ActivitySource + listener so Activity.Current is populated during the call.
        using var src = new ActivitySource(Guid.NewGuid().ToString());
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == src.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity,
        };
        ActivitySource.AddActivityListener(listener);

        var decorator = new TagsChatClientDecorator();
        var inner = new NoopChatClient();
        var wrapped = decorator.Decorate(inner, options: null);

        var options = new ChatOptions();
        options.WithChatClientTag("tenant", "acme-corp");
        options.WithChatClientTag("request_id", "abc-123");

        using (src.StartActivity("test-span"))
        {
            await wrapped.GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "hello") },
                options);
        }

        Assert.NotNull(captured);
        Assert.Equal("acme-corp", captured.GetTagItem("tenant"));
        Assert.Equal("abc-123", captured.GetTagItem("request_id"));
    }

    [Fact]
    public async Task Wrapper_NoActivityCurrent_DoesNotThrow()
    {
        // No ActivitySource listener attached → Activity.Current is null during the call.
        // Decorator must not throw; should silently skip tag application (Q10 sad path).
        var decorator = new TagsChatClientDecorator();
        var inner = new NoopChatClient();
        var wrapped = decorator.Decorate(inner, options: null);

        var options = new ChatOptions();
        options.WithChatClientTag("tenant", "acme-corp");

        var response = await wrapped.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hello") },
            options);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task Wrapper_NoTags_DoesNotThrow()
    {
        // Edge case: WithChatClientFactoryKey("tags") is set but no WithChatClientTag calls.
        // Decorator should be a clean no-op.
        var decorator = new TagsChatClientDecorator();
        var inner = new NoopChatClient();
        var wrapped = decorator.Decorate(inner, options: null);

        var response = await wrapped.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hello") },
            new ChatOptions());

        Assert.NotNull(response);
    }

    [Fact]
    public void TagsDecorator_ResolvedFromDI_ViaAddDurableAI()
    {
        // Pin the contract that AddDurableAI pre-registers the "tags" decorator under the
        // expected keyed-DI service key. Users must be able to do
        // sp.GetKeyedService<IChatClientDecorator>("tags") and get the built-in.
        var services = new ServiceCollection();
        services.AddLogging();
        DurableAIRegistrar.Register(
            services,
            builder: null,
            options: new DurableExecutionOptions { TaskQueue = "test" });

        var provider = services.BuildServiceProvider();
        var decorator = provider.GetKeyedService<IChatClientDecorator>("tags");

        Assert.NotNull(decorator);
    }

    private sealed class NoopChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
