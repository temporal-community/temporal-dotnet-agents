using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public class DurableToolIdempotencyKeyTests
{
    [Theory]
    [InlineData(
        "default",
        "workflow-123",
        "01234567-89ab-cdef-0123-456789abcdef",
        "7",
        "tai-v1:4fd719d1966cbf5585d884d8c0dd3c791d9c0737decebd0caa765ad467d36139")]
    [InlineData(
        "a",
        "bc",
        "d",
        "e",
        "tai-v1:10559fa1cac98ba828bef35ede88b560d09e0e3dd1b342314cd8412d7051c9e3")]
    [InlineData(
        "ab",
        "c",
        "d",
        "e",
        "tai-v1:217994d41e7460eabce1db7c0509a659e39bb430ace765792c64eaf21ea7f819")]
    public void Create_MatchesFrozenV1Vectors(
        string @namespace,
        string workflowId,
        string runId,
        string activityId,
        string expected) =>
        Assert.Equal(
            expected,
            DurableToolIdempotencyKey.Create(
                1,
                @namespace,
                workflowId,
                runId,
                activityId));

    [Fact]
    public void Create_IsStableAcrossAttemptsBecauseAttemptIsNotAnInput()
    {
        var firstAttempt = DurableToolIdempotencyKey.Create(1, "ns", "wf", "run", "activity");
        var laterAttempt = DurableToolIdempotencyKey.Create(1, "ns", "wf", "run", "activity");

        Assert.Equal(firstAttempt, laterAttempt);
    }

    [Fact]
    public async Task MissingVersion_FailsNonRetryablyBeforeFactoryOrFunctionInvocation()
    {
        var factoryCount = 0;
        var functionCount = 0;
        var declarationFunction = AIFunctionFactory.Create(() => "ok", "tool");
        var factories = new DurableToolFactoryRegistry(
        [
            registry => registry["tool"] =
                new DurableToolActivationFactory<RequestData, object?>((_, _) =>
                {
                    factoryCount++;
                    return new DurableToolActivation<object?>
                    {
                        Function = AIFunctionFactory.Create(
                            () =>
                            {
                                functionCount++;
                                return "ok";
                            },
                            "tool"),
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
            FunctionName = "tool",
            Declaration = DurableFunctionDeclarationSnapshot.Create(
                declarationFunction.AsDeclarationOnly()),
            RequestData = JsonSerializer.SerializeToElement(new RequestData("operation")),
            IdempotencyKeyVersion = 0,
        };
        var environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                WorkflowId = "workflow",
                WorkflowRunId = "run",
                ActivityId = "activity",
            },
        };

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => environment.RunAsync(() => activities.InvokeFunctionAsync(input)));

        Assert.True(exception.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), exception.ErrorType);
        Assert.Equal(0, factoryCount);
        Assert.Equal(0, functionCount);
    }

    private sealed record RequestData(string OperationId);
}
