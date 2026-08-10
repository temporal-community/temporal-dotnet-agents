using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Activities;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableFunctionActivitiesTests
{
    [Fact]
    public void Constructor_AcceptsEmptyRegistry()
    {
        var registry = new Dictionary<string, AIFunction>();
        var activities = new DurableFunctionActivities(registry, null);
        Assert.NotNull(activities);
    }

    [Fact]
    public async Task InvokeFunctionAsync_ThrowsInvalidOperationException_WhenFunctionNotInRegistry()
    {
        var registry = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);
        var activities = new DurableFunctionActivities(registry, null);

        var input = new DurableFunctionInput
        {
            FunctionName = "nonexistent_tool",
        };

        var env = new ActivityEnvironment();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.RunAsync(() => activities.InvokeFunctionAsync(input)));

        Assert.Contains("nonexistent_tool", ex.Message);
    }

    [Fact]
    public async Task InvokeFunctionAsync_PropagatesException_WhenFunctionInvocationThrows()
    {
        // Use an explicit Func<string> to avoid delegate ambiguity with the throwing expression.
        Func<string> throwingDelegate = () => throw new InvalidOperationException("boom");
        var throwingFunction = AIFunctionFactory.Create(throwingDelegate, "throwing_tool");

        var registry = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase)
        {
            [throwingFunction.Name] = throwingFunction,
        };
        var activities = new DurableFunctionActivities(registry, null);

        var input = new DurableFunctionInput
        {
            FunctionName = "throwing_tool",
        };

        var env = new ActivityEnvironment();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.RunAsync(() => activities.InvokeFunctionAsync(input)));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task InvokeFunctionAsync_InvocationFactoryReceivesTypedContextAndRuntimeMetadata()
    {
        DurableToolInvocationContext<RequestData, TurnState>? received = null;
        IServiceProvider? receivedServices = null;
        var declarationFunction = AIFunctionFactory.Create(
            (string value) => string.Empty,
            "contextual_tool");
        var declaration = DurableFunctionDeclarationSnapshot.Create(
            declarationFunction.AsDeclarationOnly());
        var factories = new DurableToolFactoryRegistry(
        [
            registry => registry["contextual_tool"] =
                new DurableToolActivationFactory<RequestData, TurnState>((services, context) =>
                {
                    receivedServices = services;
                    received = context;
                    return new DurableToolActivation<TurnState>
                    {
                        Function = AIFunctionFactory.Create(
                            (string value) => $"{context.RequestData.Tenant}:{context.TurnState!.Count}:{value}",
                            "contextual_tool"),
                    };
                }),
        ]);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var activities = new DurableFunctionActivities(
            new Dictionary<string, AIFunction>(),
            loggerFactory: null,
            factories,
            serviceProvider);
        var input = new DurableFunctionInput
        {
            FunctionName = "contextual_tool",
            Arguments = new Dictionary<string, object?> { ["value"] = "model" },
            Declaration = declaration,
            RequestData = JsonSerializer.SerializeToElement(new RequestData("tenant-a")),
            TurnState = JsonSerializer.SerializeToElement(new TurnState(3)),
            DispatchMode = DurableToolDispatchMode.Sequential,
            ToolCallId = "call-7",
            ModelIteration = 2,
            CallIndex = 4,
            ConversationId = "conversation",
            CorrelationId = "correlation",
            IdempotencyKeyVersion = DurableToolIdempotencyKey.CurrentVersion,
        };
        var env = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                Namespace = "namespace-a",
                WorkflowId = "workflow-a",
                WorkflowRunId = "run-a",
                ActivityId = "activity-a",
                Attempt = 3,
                TaskQueue = "queue-a",
            },
        };

        var output = await env.RunAsync(() => activities.InvokeFunctionAsync(input));

        Assert.Same(serviceProvider, receivedServices);
        Assert.Equal("tenant-a", received!.RequestData.Tenant);
        Assert.Equal(3, received.TurnState!.Count);
        Assert.Equal(DurableToolDispatchMode.Sequential, received.DispatchMode);
        Assert.Equal("namespace-a", received.Metadata.Namespace);
        Assert.Equal("workflow-a", received.Metadata.WorkflowId);
        Assert.Equal("run-a", received.Metadata.WorkflowRunId);
        Assert.Equal("activity-a", received.Metadata.ActivityId);
        Assert.Equal(3, received.Metadata.Attempt);
        Assert.Equal("queue-a", received.Metadata.TaskQueue);
        Assert.Equal("call-7", received.Metadata.ToolCallId);
        Assert.Equal(2, received.Metadata.ModelIteration);
        Assert.Equal(4, received.Metadata.CallIndex);
        Assert.Equal("conversation", received.Metadata.ConversationId);
        Assert.Equal("correlation", received.Metadata.CorrelationId);
        Assert.StartsWith("tai-v1:", received.Metadata.IdempotencyKey, StringComparison.Ordinal);
        Assert.Equal("tenant-a:3:model", Assert.IsType<JsonElement>(output.Result).GetString());
        Assert.False(output.HasStateReplacement);
    }

    private sealed record RequestData(string Tenant);
    private sealed record TurnState(int Count);
}
