using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableAIServiceCollectionExtensionsTests
{
    [Fact]
    public void AddDurableTools_ThrowsOnNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(
            () => DurableAIServiceCollectionExtensions.AddDurableTools(null!));
    }

    [Fact]
    public void AddDurableTools_ThrowsInvalidOperation_WhenAddDurableAINotCalled()
    {
        var services = new ServiceCollection();
        var workerBuilder = services.AddHostedTemporalWorker("my-queue");
        var tool = AIFunctionFactory.Create(() => "ok", "my_tool");

        var ex = Assert.Throws<InvalidOperationException>(
            () => workerBuilder.AddDurableTools(tool));

        Assert.Equal(
            "AddDurableTools requires AddDurableAI to be called first on the same worker builder.",
            ex.Message);
    }

    [Fact]
    public void AddDurableTools_Succeeds_WhenAddDurableAICalledFirst()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var workerBuilder = services
            .AddHostedTemporalWorker("my-queue")
            .AddDurableAI();
        var tool = AIFunctionFactory.Create(() => "ok", "my_tool");

        var returned = workerBuilder.AddDurableTools(tool);

        Assert.Same(workerBuilder, returned);

        // Verify the registry resolves and contains the tool.
        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DurableFunctionRegistry>();
        Assert.True(registry.ContainsKey("my_tool"));
        var declarations = provider.GetRequiredService<DurableFunctionDeclarationRegistry>();
        Assert.True(declarations.ContainsKey("my_tool"));
    }

    [Fact]
    public void AddDurableTool_FreezesDeclarationWithoutCallingInvocationFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var worker = services.AddHostedTemporalWorker("my-queue").AddDurableAI();
        var declaration = AIFunctionFactory.Create(
            (string value) => string.Empty,
            "contextual_tool").AsDeclarationOnly();
        var factoryCalls = 0;

        worker.AddDurableTool<RequestData, TurnState>(
            declaration,
            context =>
            {
                factoryCalls++;
                return new DurableToolActivation<TurnState>
                {
                    Function = AIFunctionFactory.Create(
                        (string value) => $"{context.RequestData.Tenant}:{value}",
                        "contextual_tool"),
                };
            });

        using var provider = services.BuildServiceProvider();
        var input = provider.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();

        Assert.Equal(0, factoryCalls);
        var frozen = Assert.Single(input.ToolDeclarations!);
        Assert.Equal("contextual_tool", frozen.Name);
        Assert.NotNull(provider.GetRequiredService<DurableToolFactoryRegistry>()["contextual_tool"]);
    }

    [Fact]
    public void ClientOnlyRegistration_FreezesDeclarationWithoutWorkerOrImplementation()
    {
        var services = new ServiceCollection();
        var declaration = AIFunctionFactory.Create(
            (string value) => string.Empty,
            "client_tool").AsDeclarationOnly();

        services
            .AddDurableChatWorkflowInputFactory("implementation-queue")
            .AddDurableToolDeclaration(
                declaration,
                options => options.WithMaxAttempts(2));

        using var provider = services.BuildServiceProvider();
        var input = provider.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();
        var frozen = Assert.Single(input.ToolDeclarations!);

        Assert.Equal("client_tool", frozen.Name);
        Assert.Equal(2, input.ToolActivityOptions!["client_tool"].RetryPolicy!.MaximumAttempts);
        Assert.Empty(provider.GetRequiredService<DurableToolFactoryRegistry>());
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.FullName?.Contains(
                "IHostedService",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ClientDeclaration_RequiresWorkflowInputRegistration()
    {
        var services = new ServiceCollection();
        var declaration = AIFunctionFactory.Create(
            (string value) => string.Empty,
            "client_tool").AsDeclarationOnly();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddDurableToolDeclaration(declaration));

        Assert.Equal(
            "AddDurableToolDeclaration requires AddDurableChatWorkflowInputFactory to be called first.",
            exception.Message);
    }

    private sealed record RequestData(string Tenant);
    private sealed record TurnState(int Count);
}
