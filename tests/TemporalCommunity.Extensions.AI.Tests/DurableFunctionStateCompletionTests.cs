using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableFunctionStateCompletionTests
{
    [Fact]
    public async Task SequentialCompletion_DistinguishesUnchangedValueAndNullReplacement()
    {
        var unchangedResult = await InvokeAsync(
            DurableToolDispatchMode.Sequential,
            (_, _) => ValueTask.FromResult(DurableStateUpdate<TurnState>.Unchanged));
        var valueResult = await InvokeAsync(
            DurableToolDispatchMode.Sequential,
            (_, _) => ValueTask.FromResult(DurableStateUpdate<TurnState>.Replace(new TurnState(8))));
        var nullResult = await InvokeAsync(
            DurableToolDispatchMode.Sequential,
            (_, _) => ValueTask.FromResult(DurableStateUpdate<TurnState>.Replace(null)));

        Assert.False(unchangedResult.HasStateReplacement);
        Assert.Null(unchangedResult.StateReplacement);
        Assert.True(valueResult.HasStateReplacement);
        Assert.Equal(8, valueResult.StateReplacement!.Value.GetProperty("count").GetInt32());
        Assert.True(nullResult.HasStateReplacement);
        Assert.Equal(JsonValueKind.Null, nullResult.StateReplacement!.Value.ValueKind);
    }

    [Fact]
    public async Task ParallelCompletion_IsRejectedBeforeOrdinaryFunctionRuns()
    {
        var invocationCount = 0;

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => InvokeAsync(
                DurableToolDispatchMode.Parallel,
                (_, _) => ValueTask.FromResult(DurableStateUpdate<TurnState>.Replace(new(1))),
                () => invocationCount++));

        Assert.True(exception.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), exception.ErrorType);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task CompletionFailure_IsNonRetryableAndOrdinaryFunctionRunsOnce()
    {
        var invocationCount = 0;

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => InvokeAsync(
                DurableToolDispatchMode.Sequential,
                (_, _) => throw new InvalidOperationException("completion failed"),
                () => invocationCount++));

        Assert.True(exception.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), exception.ErrorType);
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task CompletionInternalTimeout_IsNonRetryableAfterToolEffectCompletes()
    {
        var invocationCount = 0;
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => InvokeAsync(
                DurableToolDispatchMode.Sequential,
                (_, activityToken) =>
                {
                    Assert.False(activityToken.IsCancellationRequested);
                    throw new OperationCanceledException("completion timeout", timeout.Token);
                },
                () => invocationCount++));

        Assert.True(exception.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), exception.ErrorType);
        Assert.Null(exception.InnerException);
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task CompletionActivityCancellation_PreservesActivityCancellation()
    {
        var invocationCount = 0;
        var environment = new ActivityEnvironment();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeAsync(
                DurableToolDispatchMode.Sequential,
                (_, activityToken) =>
                {
                    environment.Cancel();
                    activityToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(DurableStateUpdate<TurnState>.Unchanged);
                },
                () => invocationCount++,
                environment));

        Assert.Equal(environment.CancellationTokenSource.Token, exception.CancellationToken);
        Assert.True(exception.CancellationToken.IsCancellationRequested);
        Assert.Equal(1, invocationCount);
    }

    private static Task<DurableFunctionOutput> InvokeAsync(
        DurableToolDispatchMode dispatchMode,
        Func<object?, CancellationToken, ValueTask<DurableStateUpdate<TurnState>>> completion,
        Action? onInvoke = null,
        ActivityEnvironment? environment = null)
    {
        var declarationFunction = AIFunctionFactory.Create(() => "ok", "stateful_tool");
        var declaration = DurableFunctionDeclarationSnapshot.Create(
            declarationFunction.AsDeclarationOnly());
        var factories = new DurableToolFactoryRegistry(
        [
            registry => registry["stateful_tool"] =
                new DurableToolActivationFactory<RequestData, TurnState>((_, _) =>
                    new DurableToolActivation<TurnState>
                    {
                        Function = AIFunctionFactory.Create(
                            () =>
                            {
                                onInvoke?.Invoke();
                                return "ok";
                            },
                            "stateful_tool"),
                        CompleteState = completion,
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
            FunctionName = "stateful_tool",
            Declaration = declaration,
            RequestData = JsonSerializer.SerializeToElement(new RequestData("operation")),
            TurnState = JsonSerializer.SerializeToElement(new TurnState(1)),
            DispatchMode = dispatchMode,
            SessionEpoch = "epoch",
            RequestId = "request",
            TurnNumber = 1,
            IdempotencyKeyVersion = DurableToolIdempotencyKey.CurrentVersion,
        };
        environment ??= new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                WorkflowId = "workflow",
                WorkflowRunId = "run",
                ActivityId = "activity",
            },
        };

        return environment.RunAsync(() => activities.InvokeFunctionAsync(input));
    }

    private sealed record RequestData(string OperationId);
    private sealed record TurnState(int Count);
}
