using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

/// <summary>
/// Step 4d validator tests: pins the trigger matrix for the MEAI mixed-pattern A-check.
/// </summary>
public class DurableMixedPatternValidatorTests
{
    [Fact]
    public void NoDurableFunctionRegistry_IsNoOp()
    {
        // No DurableFunctionRegistry registered at all (e.g., AddDurableAI was never called).
        // Validator must short-circuit silently — it is wired generically and may run in
        // contexts where the registry isn't yet populated.
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new MarkerChatClient());
        var provider = services.BuildServiceProvider();

        var validator = new DurableMixedPatternValidator(provider);

        // Should not throw.
        validator.PostConfigure(name: null, BuildOptions());
    }

    [Fact]
    public void EmptyRegistry_IsNoOp_EvenWithFunctionInvokingChatClient()
    {
        // Registry exists but is empty (no AddDurableTools calls) → Pattern 1 is valid,
        // FunctionInvokingChatClient is harmless. Must not throw.
        var services = new ServiceCollection();
        services.AddSingleton<DurableFunctionRegistry>();
        services.AddSingleton<IChatClient>(BuildClientWithFunctionInvocation());
        var provider = services.BuildServiceProvider();

        var validator = new DurableMixedPatternValidator(provider);

        validator.PostConfigure(name: null, BuildOptions());
    }

    [Fact]
    public void RegistryPopulated_NoFunctionInvokingChatClient_IsNoOp()
    {
        // Pattern 2 done correctly: durable tools registered, IChatClient has no
        // FunctionInvokingChatClient. No conflict.
        var services = new ServiceCollection();
        services.AddSingleton<Action<DurableFunctionRegistry>>(r => r.Register(BuildAIFunction()));
        services.AddSingleton<DurableFunctionRegistry>();
        services.AddSingleton<IChatClient>(new MarkerChatClient());
        var provider = services.BuildServiceProvider();

        var validator = new DurableMixedPatternValidator(provider);

        validator.PostConfigure(name: null, BuildOptions());
    }

    [Fact]
    public void RegistryPopulated_NoIChatClient_IsNoOp()
    {
        // Durable tools registered but no unkeyed IChatClient. Common keyed-only setup.
        // A-check skips; B-check at first invocation will validate.
        var services = new ServiceCollection();
        services.AddSingleton<Action<DurableFunctionRegistry>>(r => r.Register(BuildAIFunction()));
        services.AddSingleton<DurableFunctionRegistry>();
        var provider = services.BuildServiceProvider();

        var validator = new DurableMixedPatternValidator(provider);

        validator.PostConfigure(name: null, BuildOptions());
    }

    [Fact]
    public void RegistryPopulated_FunctionInvokingChatClientInChain_Throws()
    {
        // The exact misconfiguration the validator exists to catch: Pattern 1 + Pattern 2.
        var services = new ServiceCollection();
        services.AddSingleton<Action<DurableFunctionRegistry>>(r => r.Register(BuildAIFunction()));
        services.AddSingleton<DurableFunctionRegistry>();
        services.AddSingleton<IChatClient>(BuildClientWithFunctionInvocation());
        var provider = services.BuildServiceProvider();

        var validator = new DurableMixedPatternValidator(provider);

        Assert.Throws<DurableMixedPatternException>(
            () => validator.PostConfigure(name: null, BuildOptions()));
    }

    [Fact]
    public void FactoryThrows_WrappedInDurableConfigurationException()
    {
        // Per Q4: fail loudly. If the IChatClient factory throws (network call, secret
        // lookup), wrap in DurableConfigurationException so the host startup failure is
        // diagnostically clear. The original cause is preserved as InnerException.
        var services = new ServiceCollection();
        services.AddSingleton<Action<DurableFunctionRegistry>>(r => r.Register(BuildAIFunction()));
        services.AddSingleton<DurableFunctionRegistry>();
        services.AddSingleton<IChatClient>(_ => throw new InvalidOperationException("secret-not-found"));
        var provider = services.BuildServiceProvider();

        var validator = new DurableMixedPatternValidator(provider);

        var ex = Assert.Throws<DurableConfigurationException>(
            () => validator.PostConfigure(name: null, BuildOptions()));
        Assert.NotNull(ex.InnerException);
        // InnerException carries the original cause untouched.
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void Registered_ViaAddDurableAI_RunsAsPostConfigureOption()
    {
        // Contract test: the validator must be picked up as an
        // IPostConfigureOptions<TemporalWorkerServiceOptions> by the DI container after
        // DurableAIRegistrar.Register runs. Sole purpose is to pin the wiring so a future
        // refactor that removes the registrar's TryAddEnumerable line fails this test.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<ITemporalClient>());
        DurableAIRegistrar.Register(
            services,
            builder: null,
            options: new DurableExecutionOptions { TaskQueue = "test" });
        var provider = services.BuildServiceProvider();

        var registered = provider.GetServices<IPostConfigureOptions<TemporalWorkerServiceOptions>>();

        Assert.Contains(registered, r => r is DurableMixedPatternValidator);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static TemporalWorkerServiceOptions BuildOptions() =>
        new();

    private static AIFunction BuildAIFunction() =>
        AIFunctionFactory.Create(() => "ok", name: "noop");

    private static IChatClient BuildClientWithFunctionInvocation()
    {
        // .UseFunctionInvocation() wraps the inner client in FunctionInvokingChatClient.
        // That's exactly the chain the validator must detect via AgentChainWalker.
        return new ChatClientBuilder(new MarkerChatClient())
            .UseFunctionInvocation()
            .Build();
    }

    private sealed class MarkerChatClient : IChatClient
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
