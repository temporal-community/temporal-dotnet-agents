using System.Reflection;
using System.Text.Json;
using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableMethodToolFactoryTests
{
    [Fact]
    public async Task MethodRegistration_CachesDeclarationAndCreatesOneOwnedHandlerPerInvocation()
    {
        var probe = new LifetimeProbe();
        var services = CreateServices(probe);
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();

        worker.AddDurableToolFactory<MethodTool>(
            nameof(MethodTool.ExecuteAsync),
            new AIFunctionFactoryOptions
            {
                Name = "execute",
                Description = "Executes one value.",
            });

        Assert.Equal(0, probe.HandlerCreated);
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DurableFunctionRegistry>();
        var function = registry["execute"];
        Assert.Same(function, provider.GetRequiredService<DurableFunctionRegistry>()["execute"]);
        Assert.Equal(0, probe.HandlerCreated);

        var first = await InvokeAsync(provider, function, "one");
        var second = await InvokeAsync(provider, function, "two");

        Assert.NotEqual(first, second);
        Assert.Equal(2, probe.HandlerCreated);
        Assert.Equal(2, probe.HandlerDisposed);
        Assert.Equal(2, probe.DependencyCreated);
        Assert.Equal(2, probe.DependencyDisposed);

        var schema = function.JsonSchema.GetRawText();
        Assert.Contains("value", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("invocation", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("cancellationToken", schema, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fail", false)]
    [InlineData("cancel", true)]
    public async Task MethodRegistration_DisposesHandlerAndScopeOnFailureOrCancellation(
        string value,
        bool cancel)
    {
        var probe = new LifetimeProbe();
        var services = CreateServices(probe);
        services
            .AddHostedTemporalWorker("queue")
            .AddDurableAI()
            .AddDurableToolFactory<MethodTool>(nameof(MethodTool.ExecuteAsync));
        using var provider = services.BuildServiceProvider();
        var function = Assert.Single(provider.GetRequiredService<DurableFunctionRegistry>()).Value;
        using var scope = provider.CreateScope();
        var arguments = new AIFunctionArguments { ["value"] = value };
        arguments.Services = scope.ServiceProvider;
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await function.InvokeAsync(arguments, cancellation.Token));
        scope.Dispose();

        Assert.Equal(1, probe.HandlerCreated);
        Assert.Equal(1, probe.HandlerDisposed);
        Assert.Equal(1, probe.DependencyCreated);
        Assert.Equal(1, probe.DependencyDisposed);
    }

    [Fact]
    public void MethodRegistration_RejectsMissingAmbiguousStaticAndGenericMethods()
    {
        var services = CreateServices(new LifetimeProbe());
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();

        Assert.Throws<ArgumentException>(() =>
            worker.AddDurableToolFactory<MethodTool>("missing"));
        Assert.Throws<ArgumentException>(() =>
            worker.AddDurableToolFactory<OverloadedTool>(nameof(OverloadedTool.Execute)));
        Assert.Throws<ArgumentException>(() =>
            worker.AddDurableToolFactory<InvalidTool>(
                typeof(InvalidTool).GetMethod(nameof(InvalidTool.Static))!));
        Assert.Throws<ArgumentException>(() =>
            worker.AddDurableToolFactory<InvalidTool>(
                typeof(InvalidTool).GetMethod(nameof(InvalidTool.Generic))!));
    }

    [Fact]
    public void ToolsetMethodRegistration_UsesConfiguredModelName()
    {
        var services = CreateServices(new LifetimeProbe());
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();

        worker.AddDurableToolset("operations", tools =>
            tools.AddDurableToolFactory<MethodTool>(
                nameof(MethodTool.ExecuteAsync),
                new AIFunctionFactoryOptions { Name = "run_operation" }));

        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<DurableFunctionRegistry>().ContainsKey("run_operation"));
        Assert.Equal(
            ["run_operation"],
            Assert.Single(provider.GetServices<DurableToolsetRegistration>()).FunctionNames);
    }

    private static ServiceCollection CreateServices(LifetimeProbe probe)
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        services.AddSingleton(probe);
        services.AddScoped<ScopedDependency>();
        return services;
    }

    private static async Task<string> InvokeAsync(
        ServiceProvider provider,
        AIFunction function,
        string value)
    {
        using var scope = provider.CreateScope();
        var arguments = new AIFunctionArguments { ["value"] = value };
        arguments.Services = scope.ServiceProvider;
        var result = await function.InvokeAsync(arguments);
        var element = Assert.IsType<JsonElement>(result);
        return element.GetString()!;
    }

    private sealed class MethodTool(ScopedDependency dependency, LifetimeProbe probe) : IDisposable
    {
        private readonly int handlerId = probe.HandlerCreated++;

        public Task<string> ExecuteAsync(
            string value,
            AIFunctionArguments invocation,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(invocation.Services);
            cancellationToken.ThrowIfCancellationRequested();
            if (value == "fail")
            {
                throw new InvalidOperationException("expected failure");
            }

            return Task.FromResult($"{handlerId}:{dependency.Id}:{value}");
        }

        public void Dispose() => probe.HandlerDisposed++;
    }

    private sealed class ScopedDependency : IDisposable
    {
        private readonly LifetimeProbe probe;

        public ScopedDependency(LifetimeProbe probe)
        {
            this.probe = probe;
            Id = probe.DependencyCreated++;
        }

        public int Id { get; }

        public void Dispose() => probe.DependencyDisposed++;
    }

    private sealed class LifetimeProbe
    {
        public int HandlerCreated;
        public int HandlerDisposed;
        public int DependencyCreated;
        public int DependencyDisposed;
    }

    private sealed class OverloadedTool
    {
        public string Execute(string value) => value;
        public string Execute(int value) => value.ToString();
    }

    private sealed class InvalidTool
    {
        public static string Static() => string.Empty;
        public T Generic<T>(T value) => value;
    }
}
