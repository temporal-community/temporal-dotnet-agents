using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableActivityRoutingOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void ModelQueue_RejectsEmptyOrWhitespace(string queue)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddDurableAIClient("sessions", options =>
                options.ModelTaskQueue = queue));
        Assert.Contains(nameof(DurableExecutionOptions.ModelTaskQueue), exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void ToolQueue_RejectsEmptyOrWhitespaceAtRegistration(string queue)
    {
        var services = new ServiceCollection();
        services.AddDurableChatWorkflowInputFactory("sessions");
        var tool = AIFunctionFactory.Create(() => "ok", "read_status");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddDurableToolDeclaration(tool.AsDeclarationOnly(), options => options.TaskQueue = queue));
        Assert.Contains(nameof(DurableChatToolOptions.TaskQueue), exception.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("inference", "tools")]
    public void InputFactory_RoundTripsAndSnapshotsRoutes(string? modelQueue, string? toolQueue)
    {
        var services = new ServiceCollection();
        services.AddDurableChatWorkflowInputFactory("sessions", options =>
            options.ModelTaskQueue = modelQueue);
        DurableChatToolOptions? retainedOptions = null;
        services.AddDurableToolDeclaration(
            AIFunctionFactory.Create(() => "ok", "read_status").AsDeclarationOnly(),
            options =>
            {
                options.TaskQueue = toolQueue;
                retainedOptions = options;
            });
        using var provider = services.BuildServiceProvider();
        var input = provider.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();
        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var restored = (DurableChatWorkflowInput)converter.ToValue(
            converter.ToPayload(input), typeof(DurableChatWorkflowInput))!;

        Assert.Equal(modelQueue, restored.ModelTaskQueue);
        Assert.Equal(toolQueue, restored.ToolActivityOptions!["read_status"].TaskQueue);
        retainedOptions!.TaskQueue = "changed-after-snapshot";
        provider.GetRequiredService<DurableExecutionOptions>().ModelTaskQueue = "changed-model";
        Assert.Equal(modelQueue, input.ModelTaskQueue);
        Assert.Equal(toolQueue, input.ToolActivityOptions!["read_status"].TaskQueue);
    }

    [Fact]
    public void Manifest_FreezesToolQueue_AndFingerprintIncludesIt()
    {
        var services = new ServiceCollection();
        var worker = services.AddHostedTemporalWorker("sessions")
            .AddDurableAIWorker();
        DurableChatToolOptions? retained = null;
        worker.AddDurableTool(AIFunctionFactory.Create(() => "ok", "read_status"), options =>
        {
            options.TaskQueue = "tools-first";
            retained = options;
        });
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<DurableToolsetCatalog>();
        var first = catalog.Resolve(new() { UseWorkerDefaults = true });
        retained!.TaskQueue = "tools-second";
        var second = catalog.Resolve(new() { UseWorkerDefaults = true });
        Assert.Equal("tools-first", Assert.Single(first.Members).ToolActivityOptions.TaskQueue);
        Assert.Equal("tools-second", Assert.Single(second.Members).ToolActivityOptions.TaskQueue);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);

        var member = first.Members.Single();
        var changedOptions = (Temporalio.Workflows.ActivityOptions)member.ToolActivityOptions.Clone();
        changedOptions.TaskQueue = "tampered";
        var tampered = first with { Members = [member with { ToolActivityOptions = changedOptions }] };
        Assert.Throws<ApplicationFailureException>(() => tampered.Validate());
        changedOptions.TaskQueue = " ";
        Assert.Throws<ApplicationFailureException>(() => tampered.Validate());
    }
}
