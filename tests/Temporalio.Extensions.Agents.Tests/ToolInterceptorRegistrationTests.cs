using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Tools;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Tools;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests that <see cref="DurableAgentBuilder.AddToolInterceptor"/> and
/// <see cref="TemporalAgentsOptions.DefaultToolInterceptor"/> are plumbed
/// through to <see cref="DurableAgentRegistration.ToolInterceptorFactory"/>.
/// </summary>
public class ToolInterceptorRegistrationTests
{
    private sealed class StubInterceptor : IAgentToolInterceptor
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            AgentToolContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(DurableToolDecision.Proceed());
    }

    private static DurableAgentRegistration BuildRegistration(
        Action<DurableAgentBuilder> configure)
    {
        var builder = new DurableAgentBuilder("TestAgent");
        builder.ChatClient = _ => new StubChatClient();
        configure(builder);
        return builder.ToRegistration();
    }

    [Fact]
    public void AddToolInterceptor_NullFactory_Throws()
    {
        var builder = new DurableAgentBuilder("Agent");
        builder.ChatClient = _ => new StubChatClient();
        Assert.Throws<ArgumentNullException>(() => builder.AddToolInterceptor(null!));
    }

    [Fact]
    public void AddToolInterceptor_SetsFactory_OnRegistration()
    {
        var registration = BuildRegistration(a =>
            a.AddToolInterceptor(_ => new StubInterceptor()));

        Assert.NotNull(registration.ToolInterceptorFactory);
    }

    [Fact]
    public void AddToolInterceptor_ReturnsBuilder_ForFluency()
    {
        var builder = new DurableAgentBuilder("Agent");
        builder.ChatClient = _ => new StubChatClient();
        var returned = builder.AddToolInterceptor(_ => new StubInterceptor());
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddToolInterceptor_FactoryIsInvokedWithServiceProvider()
    {
        IServiceProvider? captured = null;
        var stubProvider = new StubServiceProvider();

        var registration = BuildRegistration(a =>
            a.AddToolInterceptor(sp =>
            {
                captured = sp;
                return new StubInterceptor();
            }));

        // Invoke the factory to verify it's wired correctly.
        var interceptor = registration.ToolInterceptorFactory!(stubProvider);
        Assert.Same(stubProvider, captured);
        Assert.IsType<StubInterceptor>(interceptor);
    }

    [Fact]
    public void NoInterceptorRegistered_FactoryIsNull()
    {
        var registration = BuildRegistration(_ => { });
        Assert.Null(registration.ToolInterceptorFactory);
    }

    [Fact]
    public void DefaultToolInterceptor_CanBeSetOnOptions()
    {
        var options = new TemporalAgentsOptions();
        options.DefaultToolInterceptor = _ => new StubInterceptor();
        Assert.NotNull(options.DefaultToolInterceptor);
    }

    // ── DurableToolOptions integration ─────────────────────────────────────────

    [Fact]
    public void RequireApproval_IsCarried_ThroughToolRegistration()
    {
        var registration = BuildRegistration(a =>
            a.AddTool(
                AIFunctionFactory.Create(() => "ok", "myTool"),
                opts => opts.RequireApproval()));

        var toolReg = registration.Tools.Single(t => t.Name == "myTool");
        Assert.True(toolReg.Options.RequireApprovalFlag);
    }

    [Fact]
    public void SkipInterceptor_IsCarried_ThroughToolRegistration()
    {
        var registration = BuildRegistration(a =>
            a.AddTool(
                AIFunctionFactory.Create(() => "ok", "myTool"),
                opts => opts.SkipInterceptor()));

        var toolReg = registration.Tools.Single(t => t.Name == "myTool");
        Assert.True(toolReg.Options.SkipInterceptorFlag);
    }

    [Fact]
    public void WithInterceptorTimeout_IsWiredIntoResolvedWorkerConfig()
    {
        // Arrange: agent with one tool carrying a custom interceptor timeout and
        // one without, plus an interceptor registered so the config is populated.
        var customTimeout = TimeSpan.FromSeconds(45);
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", a =>
        {
            a.ChatClient = _ => new StubChatClient();
            a.AddToolInterceptor(_ => new StubInterceptor());
            a.AddTool(
                AIFunctionFactory.Create(() => "timed", "timedTool"),
                opts => opts.WithInterceptorTimeout(customTimeout));
            a.AddTool(
                AIFunctionFactory.Create(() => "default", "defaultTool"));
        });

        // Act: build the workflow input the same way the client does.
        var input = DefaultTemporalAgentClient.BuildAgentWorkflowInputCore(
            "MyAgent", options, "test-queue");

        var config = input.ResolvedWorkerConfig;
        Assert.NotNull(config);

        // timedTool must have its own entry with the custom timeout.
        Assert.NotNull(config.InterceptorToolActivityOptions);
        Assert.True(config.InterceptorToolActivityOptions.TryGetValue("timedTool", out var timedOpts));
        Assert.Equal(customTimeout, timedOpts!.StartToCloseTimeout);

        // defaultTool must NOT have a per-tool entry — it falls back to the shared opts.
        Assert.False(config.InterceptorToolActivityOptions.ContainsKey("defaultTool"));

        // The shared interceptor options are still present as the fallback.
        Assert.NotNull(config.InterceptorActivityOptions);
    }

    // ── Stubs ──────────────────────────────────────────────────────────────────

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub");

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

        public TService? GetService<TService>(object? key = null) where TService : class => null;

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose() { }
    }

    private sealed class StubServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
