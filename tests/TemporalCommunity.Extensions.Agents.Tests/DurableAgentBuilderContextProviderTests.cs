#pragma warning disable TA001 // IDurableToolSource is experimental; intentional consumption in these tests
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Tools;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Tests for <see cref="DurableAgentBuilder.AddContextProvider"/> paths that interact with
/// <see cref="IDurableToolSource"/> auto-detection and the explicit <c>durableTools</c>
/// parameter overload.
/// </summary>
public class DurableAgentBuilderContextProviderTests
{
    // ── helpers ─────────────────────────────────────────────────────────────────

    private static DurableAgentBuilder NewBuilder(string name = "Agent") => new(name);

    private static AIFunction NewTool(string name) => AIFunctionFactory.Create(() => "ok", name);

    // ── 1. IDurableToolSource auto-detection registers tools ─────────────────────

    [Fact]
    public void AddContextProvider_IDurableToolSource_AutoDetect_RegistersTool()
    {
        var fn = NewTool("auto_tool");
        var provider = new SelfDeclaringProvider(new DurableToolRegistrationSpec(fn));

        var builder = NewBuilder();
        builder.AddContextProvider(provider);

        Assert.Single(builder.ToolRegistrations);
        Assert.Equal("auto_tool", builder.ToolRegistrations[0].Name);
    }

    [Fact]
    public void AddContextProvider_IDurableToolSource_AutoDetect_MultipleTools_AllRegistered()
    {
        var fn1 = NewTool("tool_a");
        var fn2 = NewTool("tool_b");
        var provider = new SelfDeclaringProvider(
            new DurableToolRegistrationSpec(fn1),
            new DurableToolRegistrationSpec(fn2));

        var builder = NewBuilder();
        builder.AddContextProvider(provider);

        Assert.Equal(2, builder.ToolRegistrations.Count);
        Assert.Contains(builder.ToolRegistrations, r => r.Name == "tool_a");
        Assert.Contains(builder.ToolRegistrations, r => r.Name == "tool_b");
    }

    [Fact]
    public void AddContextProvider_IDurableToolSource_AutoDetect_RegistrationHasExpectedName()
    {
        var fn = NewTool("my_durable_tool");
        var provider = new SelfDeclaringProvider(new DurableToolRegistrationSpec(fn));

        var builder = NewBuilder();
        builder.AddContextProvider(provider);

        Assert.Equal("my_durable_tool", builder.ToolRegistrations[0].Name);
    }

    // ── 2. Explicit durableTools parameter wraps and registers ──────────────────

    [Fact]
    public void AddContextProvider_ExplicitSpecs_RegistersTool()
    {
        var fn = NewTool("explicit_tool");
        var provider = new PlainContextProvider();

        var builder = NewBuilder();
        builder.AddContextProvider(provider, [new DurableToolRegistrationSpec(fn)]);

        Assert.Single(builder.ToolRegistrations);
        Assert.Equal("explicit_tool", builder.ToolRegistrations[0].Name);
    }

    [Fact]
    public void AddContextProvider_ExplicitSpecs_ContextProviderFactoryReturnsIDurableToolSource()
    {
        // When explicit specs are supplied the provider is wrapped in DurableContextProviderWrapper,
        // which implements IDurableToolSource. Verify via the registered factory.
        var fn = NewTool("wrapped_tool");
        var provider = new PlainContextProvider();

        var builder = NewBuilder();
        builder.AddContextProvider(provider, [new DurableToolRegistrationSpec(fn)]);

        Assert.Single(builder.ContextProviderFactories);
        var resolved = builder.ContextProviderFactories[0](null!);
        Assert.IsAssignableFrom<IDurableToolSource>(resolved);
    }

    [Fact]
    public void AddContextProvider_ExplicitSpecs_WithConfigureCallback_OptionsApplied()
    {
        var fn = NewTool("write_tool");
        var provider = new PlainContextProvider();

        var builder = NewBuilder();
        builder.AddContextProvider(
            provider,
            [new DurableToolRegistrationSpec(fn, opts => opts.NoRetry())]);

        var reg = builder.ToolRegistrations[0];
        Assert.NotNull(reg.Options.RetryPolicy);
        Assert.Equal(1, reg.Options.RetryPolicy!.MaximumAttempts);
    }

    // ── 3. Collision via AddContextProvider throws with both sources named ───────

    [Fact]
    public void AddContextProvider_ExplicitSpec_CollidesWithAddTool_ThrowsArgumentException()
    {
        var builder = NewBuilder("CollisionAgent");
        builder.AddTool(NewTool("foo"));

        var fooFn = NewTool("foo");
        var provider = new PlainContextProvider();

        var ex = Assert.Throws<ArgumentException>(() =>
            builder.AddContextProvider(provider, [new DurableToolRegistrationSpec(fooFn)]));

        Assert.Contains("AddTool", ex.Message);
        Assert.Contains("AddContextProvider", ex.Message);
    }

    [Fact]
    public void AddContextProvider_AutoDetect_CollidesWithAddTool_ThrowsArgumentException()
    {
        var builder = NewBuilder("AutoCollisionAgent");
        builder.AddTool(NewTool("shared_name"));

        var provider = new SelfDeclaringProvider(
            new DurableToolRegistrationSpec(NewTool("shared_name")));

        var ex = Assert.Throws<ArgumentException>(() =>
            builder.AddContextProvider(provider));

        Assert.Contains("AddTool", ex.Message);
        Assert.Contains("AddContextProvider", ex.Message);
    }

    [Fact]
    public void AddContextProvider_ExplicitSpec_CollidesBetweenTwoProviders_ThrowsArgumentException()
    {
        var builder = NewBuilder("TwoProviderCollision");
        var provider1 = new PlainContextProvider();
        var provider2 = new PlainContextProvider();

        builder.AddContextProvider(provider1, [new DurableToolRegistrationSpec(NewTool("shared"))]);

        var ex = Assert.Throws<ArgumentException>(() =>
            builder.AddContextProvider(provider2, [new DurableToolRegistrationSpec(NewTool("shared"))]));

        Assert.Contains("AddTool", ex.Message);
        Assert.Contains("AddContextProvider", ex.Message);
    }

    // ── 4. ToRegistration() populates ProviderContributedTools ──────────────────

    [Fact]
    public void ToRegistration_ExplicitSpecs_PopulatesProviderContributedTools()
    {
        var fn = NewTool("contrib_tool");
        var provider = new PlainContextProvider();

        var builder = NewBuilder();
        builder.ChatClient = _ => new StubChatClient();
        builder.AddContextProvider(provider, [new DurableToolRegistrationSpec(fn)]);

        var reg = builder.ToRegistration();

        Assert.NotNull(reg.ProviderContributedTools);
        Assert.Single(reg.ProviderContributedTools!);
        Assert.Equal("contrib_tool", reg.ProviderContributedTools![0].ToolName);
        Assert.Equal(nameof(PlainContextProvider), reg.ProviderContributedTools![0].SourceProviderType);
    }

    [Fact]
    public void ToRegistration_AutoDetect_PopulatesProviderContributedTools()
    {
        var fn = NewTool("auto_contrib");
        var provider = new SelfDeclaringProvider(new DurableToolRegistrationSpec(fn));

        var builder = NewBuilder();
        builder.ChatClient = _ => new StubChatClient();
        builder.AddContextProvider(provider);

        var reg = builder.ToRegistration();

        Assert.NotNull(reg.ProviderContributedTools);
        Assert.Single(reg.ProviderContributedTools!);
        Assert.Equal("auto_contrib", reg.ProviderContributedTools![0].ToolName);
        Assert.Equal(nameof(SelfDeclaringProvider), reg.ProviderContributedTools![0].SourceProviderType);
    }

    [Fact]
    public void ToRegistration_ExplicitSpecs_MultipleTools_AllInProviderContributedTools()
    {
        var fn1 = NewTool("c1");
        var fn2 = NewTool("c2");
        var provider = new PlainContextProvider();

        var builder = NewBuilder();
        builder.ChatClient = _ => new StubChatClient();
        builder.AddContextProvider(
            provider,
            [new DurableToolRegistrationSpec(fn1), new DurableToolRegistrationSpec(fn2)]);

        var reg = builder.ToRegistration();

        Assert.NotNull(reg.ProviderContributedTools);
        Assert.Equal(2, reg.ProviderContributedTools!.Count);
        Assert.Contains(reg.ProviderContributedTools!, e => e.ToolName == "c1");
        Assert.Contains(reg.ProviderContributedTools!, e => e.ToolName == "c2");
    }

    // ── 5. Empty durableTools treated same as null ───────────────────────────────

    [Fact]
    public void AddContextProvider_EmptySpecs_AddsNoTools()
    {
        var provider = new PlainContextProvider();

        var builder = NewBuilder();
        builder.AddContextProvider(provider, []);

        Assert.Empty(builder.ToolRegistrations);
    }

    [Fact]
    public void AddContextProvider_EmptySpecs_ProviderRegisteredAsIs()
    {
        // With an empty specs list the provider is NOT wrapped — the factory returns the
        // original instance directly.
        var provider = new PlainContextProvider();

        var builder = NewBuilder();
        builder.AddContextProvider(provider, []);

        Assert.Single(builder.ContextProviderFactories);
        var resolved = builder.ContextProviderFactories[0](null!);
        Assert.Same(provider, resolved);
    }

    [Fact]
    public void ToRegistration_EmptySpecs_ProviderContributedToolsIsNullOrEmpty()
    {
        var provider = new PlainContextProvider();

        var builder = NewBuilder();
        builder.ChatClient = _ => new StubChatClient();
        builder.AddContextProvider(provider, []);

        var reg = builder.ToRegistration();

        // ProviderContributedTools is null when no specs contributed (ToRegistration only
        // allocates the array when _providerContributedTools.Count > 0).
        Assert.True(
            reg.ProviderContributedTools is null || reg.ProviderContributedTools.Count == 0,
            "Expected ProviderContributedTools to be null or empty when no specs were contributed.");
    }

    [Fact]
    public void AddContextProvider_NullSpecs_AddsNoTools()
    {
        // null durableTools is the default — same behaviour as empty.
        var provider = new PlainContextProvider();

        var builder = NewBuilder();
        builder.AddContextProvider(provider, null);

        Assert.Empty(builder.ToolRegistrations);
    }

    // ── Test helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A plain <see cref="AIContextProvider"/> that does NOT implement
    /// <see cref="IDurableToolSource"/>. Used for the explicit-specs and empty-specs tests.
    /// </summary>
    private sealed class PlainContextProvider : AIContextProvider
    {
    }

    /// <summary>
    /// A combined <see cref="AIContextProvider"/> / <see cref="IDurableToolSource"/> stub.
    /// Returns the specs supplied at construction from <see cref="GetDurableTools"/>.
    /// </summary>
    private sealed class SelfDeclaringProvider : AIContextProvider, IDurableToolSource
    {
        private readonly DurableToolRegistrationSpec[] _specs;

        internal SelfDeclaringProvider(params DurableToolRegistrationSpec[] specs)
        {
            _specs = specs;
        }

        public IEnumerable<DurableToolRegistrationSpec> GetDurableTools() => _specs;
    }

    /// <summary>
    /// Minimal <see cref="IChatClient"/> stub — satisfies the <c>ChatClient != null</c>
    /// requirement in <see cref="DurableAgentBuilder.ToRegistration"/> without
    /// implementing any real behaviour.
    /// </summary>
    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
