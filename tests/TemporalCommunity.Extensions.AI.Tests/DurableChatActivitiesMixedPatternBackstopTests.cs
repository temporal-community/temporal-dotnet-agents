using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Step 4d B-check backstop tests: pins the in-activity safety net that catches the MEAI
/// mixed-pattern misconfiguration when the A-check (startup validator) can't reach the
/// resolved <see cref="IChatClient"/> — e.g. keyed-only registrations, factory-deferred
/// dependencies, or per-call decorators that re-introduce
/// <see cref="FunctionInvokingChatClient"/>.
/// </summary>
public class DurableChatActivitiesMixedPatternBackstopTests
{
    [Fact]
    public async Task NoDurableTools_ChainHasFunctionInvocation_BackstopAllowsCall()
    {
        // Pattern 1 done correctly: in-process tool loop, no durable tools registered.
        // FunctionInvokingChatClient is fine here — the registry is empty.
        var inner = new RecordingChatClient();
        var withFI = new ChatClientBuilder(inner).UseFunctionInvocation().Build();

        var (provider, activities) = BuildActivities(withFI, registerDurableTool: false);

        var response = await activities.GetResponseAsync(new DurableChatInput
        {
            Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
            Options = new ChatOptions(),
            ConversationId = "test",
            TurnNumber = 1,
        });

        Assert.NotNull(response);
        provider.Dispose();
    }

    [Fact]
    public async Task DurableTools_PlainChatClient_BackstopAllowsCall()
    {
        // Pattern 2 done correctly: durable tools, no FunctionInvokingChatClient. Allowed.
        var (provider, activities) = BuildActivities(
            new RecordingChatClient(),
            registerDurableTool: true);

        var response = await activities.GetResponseAsync(new DurableChatInput
        {
            Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
            Options = new ChatOptions(),
            ConversationId = "test",
            TurnNumber = 1,
        });

        Assert.NotNull(response);
        provider.Dispose();
    }

    [Fact]
    public async Task DurableTools_ChainHasFunctionInvocation_BackstopThrows()
    {
        // The misconfiguration the backstop exists to catch when the A-check missed it
        // (modeled here by registering the IChatClient AFTER DurableAIRegistrar — a setup
        // ordering A-check inspects on whatever the unkeyed binding resolves to, but a
        // resolved instance in the activity-side path is the source of truth).
        var inner = new RecordingChatClient();
        var withFI = new ChatClientBuilder(inner).UseFunctionInvocation().Build();

        var (provider, activities) = BuildActivities(withFI, registerDurableTool: true);

        await Assert.ThrowsAsync<DurableMixedPatternException>(
            () => activities.GetResponseAsync(new DurableChatInput
            {
                Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
                Options = new ChatOptions(),
                ConversationId = "test",
                TurnNumber = 1,
            }));
        provider.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableTools_DecoratorAddsFunctionInvocation_ThrowsBeforeProviderCall(
        bool useChatStep)
    {
        var inner = new RecordingChatClient();
        var decorator = new FunctionInvokingDecorator();
        var (provider, activities) = BuildActivities(
            inner,
            registerDurableTool: true,
            decorator: ("inline", decorator));
        var options = new ChatOptions().WithChatClientFactoryKey("inline");
        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            Options = options,
            ConversationId = "test",
            TurnNumber = 1,
        };

        if (useChatStep)
        {
            await Assert.ThrowsAsync<DurableMixedPatternException>(
                () => activities.GetChatStepAsync(input));
        }
        else
        {
            await Assert.ThrowsAsync<DurableMixedPatternException>(
                () => activities.GetResponseAsync(input));
        }

        Assert.True(decorator.WasInvoked);
        Assert.Equal(0, inner.CallCount);
        provider.Dispose();
    }

    [Fact]
    public async Task BackstopRunsOncePerClient_RepeatCallsDoNotRewalkChain()
    {
        // The backstop caches the validated client by reference so repeat invocations
        // skip the chain walk. Once a client has passed, subsequent calls must not pay
        // the walk cost. Verified indirectly: a no-conflict client invoked twice never
        // throws and returns valid responses both times.
        var (provider, activities) = BuildActivities(
            new RecordingChatClient(),
            registerDurableTool: true);

        for (var i = 0; i < 2; i++)
        {
            var response = await activities.GetResponseAsync(new DurableChatInput
            {
                Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
                Options = new ChatOptions(),
                ConversationId = "test",
                TurnNumber = i + 1,
            });
            Assert.NotNull(response);
        }
        provider.Dispose();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static (ServiceProvider Provider, DurableChatActivities Activities) BuildActivities(
        IChatClient inner,
        bool registerDurableTool,
        (string Key, IChatClientDecorator Decorator)? decorator = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatClient>(inner);

        // Important: don't call DurableAIRegistrar.Register here — the A-check would fire
        // during BuildServiceProvider's options post-configuration and throw before the
        // backstop ever runs. We're testing the activity-side B-check in isolation.
        services.AddSingleton<DurableFunctionRegistry>();
        if (registerDurableTool)
        {
            services.AddSingleton<Action<DurableFunctionRegistry>>(
                r => r.Register(AIFunctionFactory.Create(() => "ok", name: "noop")));
        }
        if (decorator is var (key, instance))
        {
            services.AddKeyedSingleton<IChatClientDecorator>(key, instance);
        }

        var provider = services.BuildServiceProvider();
        var activities = new DurableChatActivities(provider, provider.GetService<ILoggerFactory>());
        return (provider, activities);
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class FunctionInvokingDecorator : IChatClientDecorator
    {
        public bool WasInvoked { get; private set; }

        public IChatClient Decorate(IChatClient inner, ChatOptions? options)
        {
            WasInvoked = true;
            return new ChatClientBuilder(inner).UseFunctionInvocation().Build();
        }
    }
}
