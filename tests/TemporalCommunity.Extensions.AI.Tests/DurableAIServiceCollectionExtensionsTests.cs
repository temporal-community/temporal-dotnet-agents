using System.Reflection;
using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableAIServiceCollectionExtensionsTests
{
    [Fact]
    public void DurableToolFactories_ReceiveServiceProvider_AndFunctionActivitiesAreScoped()
    {
        var factoryMethods = typeof(DurableAIServiceCollectionExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name is nameof(DurableAIServiceCollectionExtensions.AddDurableToolFactory)
                or nameof(DurableAIServiceCollectionExtensions.AddDurableToolImplementation))
            .Where(method => method.GetParameters().Any(parameter => parameter.Name == "factory"))
            .ToArray();

        Assert.Equal(2, factoryMethods.Length);
        Assert.All(factoryMethods, method =>
        {
            var factoryParameter = Assert.Single(
                method.GetParameters(),
                parameter => parameter.Name == "factory");
            var genericArguments = factoryParameter.ParameterType.GetGenericArguments();
            Assert.Equal(3, genericArguments.Length);
            Assert.Equal(typeof(IServiceProvider), genericArguments[0]);
        });

        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        services.AddHostedTemporalWorker("my-queue").AddDurableAI();

        var activityRegistration = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(DurableFunctionActivities));
        Assert.Equal(ServiceLifetime.Scoped, activityRegistration.Lifetime);
    }

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
    public void AddDurableToolFactory_FreezesDeclarationWithoutCallingInvocationFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var worker = services.AddHostedTemporalWorker("my-queue").AddDurableAI();
        var declaration = AIFunctionFactory.Create(
            (string value) => string.Empty,
            "contextual_tool").AsDeclarationOnly();
        var factoryCalls = 0;

        worker.AddDurableToolFactory<RequestData, TurnState>(
            declaration,
            (_, context) =>
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
    public void AddDurableToolset_RegistersOrderedMembersAndPolicies()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var worker = services.AddHostedTemporalWorker("my-queue").AddDurableAI();
        var read = AIFunctionFactory.Create(() => "read", "read_item");
        var write = AIFunctionFactory.Create(() => "write", "write_item");

        var returned = worker.AddDurableToolset("inventory", tools => tools
            .Add(read)
            .Add(write, options => options.RequireApproval()));

        Assert.Same(worker, returned);
        using var provider = services.BuildServiceProvider();
        var toolset = Assert.Single(provider.GetServices<DurableToolsetRegistration>());
        Assert.Equal("inventory", toolset.Id);
        Assert.False(toolset.IsImplicitDefault);
        Assert.Equal(["read_item", "write_item"], toolset.FunctionNames);
        Assert.Same(read, provider.GetRequiredService<DurableFunctionRegistry>()["read_item"]);
        Assert.True(provider.GetRequiredService<DurableChatToolOptionsRegistry>()["write_item"].RequireApprovalFlag);
    }

    [Fact]
    public void AddDurableTools_UsesOneImplicitDefaultToolset()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var worker = services.AddHostedTemporalWorker("my-queue").AddDurableAI();

        worker.AddDurableTools(AIFunctionFactory.Create(() => "a", "a"));
        worker.AddDurableTools(AIFunctionFactory.Create(() => "b", "b"));

        using var provider = services.BuildServiceProvider();
        var toolset = Assert.Single(provider.GetServices<DurableToolsetRegistration>());
        Assert.True(toolset.IsImplicitDefault);
        Assert.Equal(["a", "b"], toolset.FunctionNames);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddDurableToolset_RejectsMissingIdentifier(string? toolsetId)
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var worker = services.AddHostedTemporalWorker("my-queue").AddDurableAI();

        Assert.ThrowsAny<ArgumentException>(() =>
            worker.AddDurableToolset(toolsetId!, tools =>
                tools.Add(AIFunctionFactory.Create(() => "ok", "tool"))));
    }

    [Fact]
    public void AddDurableToolset_RejectsEmptyNamedToolset()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var worker = services.AddHostedTemporalWorker("my-queue").AddDurableAI();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            worker.AddDurableToolset("empty", _ => { }));

        Assert.Contains("must contain at least one tool", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDurableToolset_UsesOrdinalToolsetAndFunctionNames()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var worker = services.AddHostedTemporalWorker("my-queue").AddDurableAI();

        worker.AddDurableToolset("Catalog", tools =>
            tools.Add(AIFunctionFactory.Create(() => "upper", "Find")));
        worker.AddDurableToolset("catalog", tools =>
            tools.Add(AIFunctionFactory.Create(() => "lower", "find")));

        using var provider = services.BuildServiceProvider();
        Assert.Equal(2, provider.GetServices<DurableToolsetRegistration>().Count());
        var registry = provider.GetRequiredService<DurableFunctionRegistry>();
        Assert.Equal(2, registry.Count);
        Assert.Equal("Find", registry["Find"].Name);
        Assert.Equal("find", registry["find"].Name);
    }

    [Fact]
    public void AddDurableToolset_RejectsDuplicateIdentifierAndMember()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var worker = services.AddHostedTemporalWorker("my-queue").AddDurableAI();
        worker.AddDurableToolset("catalog", tools =>
            tools.Add(AIFunctionFactory.Create(() => "ok", "find")));

        Assert.Throws<InvalidOperationException>(() =>
            worker.AddDurableToolset("catalog", tools =>
                tools.Add(AIFunctionFactory.Create(() => "other", "other"))));

        Assert.Throws<InvalidOperationException>(() =>
            worker.AddDurableToolset("duplicate-members", tools =>
            {
                tools.Add(AIFunctionFactory.Create(() => "one", "same"));
                tools.Add(AIFunctionFactory.Create(() => "two", "same"));
            }));
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
    public void ClientOnlyRegistration_RegistersClientConfiguratorExactlyOnce()
    {
        var services = new ServiceCollection();

        services.AddDurableChatWorkflowInputFactory("implementation-queue");
        services.AddDurableChatWorkflowInputFactory("implementation-queue");

        Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IConfigureOptions<TemporalClientConnectOptions>) &&
                descriptor.ImplementationType == typeof(DurableAIClientOptionsConfigurator));
        Assert.DoesNotContain(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IPostConfigureOptions<TemporalWorkerServiceOptions>) &&
                descriptor.ImplementationType == typeof(DurableAIWorkerClientConfigurator));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClientOnlyRegistration_ConfiguresDefaultConverter_RegardlessOfOrder(
        bool addTemporalClientFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        if (addTemporalClientFirst)
        {
            services.AddTemporalClient("localhost:7233", "default");
        }

        services.AddDurableChatWorkflowInputFactory("implementation-queue");

        if (!addTemporalClientFirst)
        {
            services.AddTemporalClient("localhost:7233", "default");
        }

        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<IOptions<TemporalClientConnectOptions>>()
            .Value;

        Assert.Same(DurableAIDataConverter.Instance, options.DataConverter);
    }

    [Fact]
    public void ClientOnlyRegistration_PreservesCustomConverter()
    {
        var customConverter = new DataConverter(
            new DefaultPayloadConverter(),
            new DefaultFailureConverter());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTemporalClient(options => options.DataConverter = customConverter);
        services.AddDurableChatWorkflowInputFactory("implementation-queue");

        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<IOptions<TemporalClientConnectOptions>>()
            .Value;

        Assert.Same(customConverter, options.DataConverter);
    }

    [Fact]
    public void ClientOnlyRegistration_SelectedConverter_RoundTripsFrozenWorkflowInput()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTemporalClient("localhost:7233", "default");
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
        var converter = provider
            .GetRequiredService<IOptions<TemporalClientConnectOptions>>()
            .Value
            .DataConverter
            .PayloadConverter;

        var payload = converter.ToPayload(input);
        var actual = (DurableChatWorkflowInput)converter.ToValue(
            payload,
            typeof(DurableChatWorkflowInput))!;

        var frozen = Assert.Single(actual.ToolDeclarations!);
        Assert.Equal("client_tool", frozen.Name);
        Assert.Equal(2, actual.ToolActivityOptions!["client_tool"].RetryPolicy!.MaximumAttempts);
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
