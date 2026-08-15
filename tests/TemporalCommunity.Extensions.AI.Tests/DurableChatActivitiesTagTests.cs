using System.Diagnostics;
using System.Runtime.CompilerServices;
using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableChatActivitiesTagTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelActivity_AppliesTagsBeforeProviderAndStripsTemporalOptions(bool chatStep)
    {
        var inner = new RecordingChatClient();
        using var provider = BuildProvider(inner);
        var activities = new DurableChatActivities(
            provider,
            provider.GetRequiredService<ILoggerFactory>());
        var options = new ChatOptions()
            .WithChatClientTag("tenant", "acme")
            .WithChatClientTag("request_id", "req-1")
            .WithActivityTimeout(TimeSpan.FromSeconds(10));
        options.AdditionalProperties!["user.custom"] = "keep";
        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            Options = options,
            ConversationId = "tag-test",
            TurnNumber = 1,
        };

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DurableChatTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        if (chatStep)
        {
            await activities.GetChatStepAsync(input);
        }
        else
        {
            await activities.GetResponseAsync(input);
        }

        Assert.Equal("acme", inner.ActivityTags["tenant"]);
        Assert.Equal("req-1", inner.ActivityTags["request_id"]);
        Assert.Equal("keep", inner.Options?.AdditionalProperties?["user.custom"]);
        Assert.DoesNotContain(
            inner.Options!.AdditionalProperties!,
            pair => pair.Key.StartsWith("temporal.", StringComparison.Ordinal));
    }

    private static ServiceProvider BuildProvider(IChatClient inner)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<ITemporalClient>());
        services.AddSingleton(inner);
        services.AddSingleton<IChatClient>(inner);
        DurableAIRegistrar.Register(
            services,
            builder: null,
            options: new DurableExecutionOptions { TaskQueue = "test" });
        return services.BuildServiceProvider();
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public Dictionary<string, object?> ActivityTags { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Capture(options);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Capture(options);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private void Capture(ChatOptions? options)
        {
            Options = options;
            if (Activity.Current is not { } activity)
            {
                return;
            }

            foreach (var tag in activity.TagObjects)
            {
                ActivityTags[tag.Key] = tag.Value;
            }
        }
    }
}
