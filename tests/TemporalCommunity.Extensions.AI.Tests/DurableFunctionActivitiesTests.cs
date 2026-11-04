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
    public async Task InvokeFunctionAsync_BoundsMissingFunctionFailure()
    {
        var registry = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);
        var activities = new DurableFunctionActivities(registry, null);

        var input = new DurableFunctionInput
        {
            FunctionName = "nonexistent_tool",
        };

        var env = new ActivityEnvironment();
        var ex = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => env.RunAsync(() => activities.InvokeFunctionAsync(input)));

        Assert.Equal(nameof(InvalidOperationException), ex.ErrorType);
        Assert.False(ex.NonRetryable);
        Assert.DoesNotContain("nonexistent_tool", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeFunctionAsync_BoundsFunctionFailureWithoutChangingRetryability()
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
        var ex = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => env.RunAsync(() => activities.InvokeFunctionAsync(input)));

        Assert.Equal(nameof(InvalidOperationException), ex.ErrorType);
        Assert.False(ex.NonRetryable);
        Assert.Equal("Durable activity failed; failure details were omitted.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeFunctionAsync_HeartbeatsDuringLongToolAndPropagatesActivityCancellation(
        bool synchronousTool)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken receivedToken = default;
        var heartbeatsDuringTool = 0;
        ActivityEnvironment? env = null;
        env = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                HeartbeatTimeout = TimeSpan.FromMilliseconds(100),
            },
            Heartbeater = _ =>
            {
                if (started.Task.IsCompleted
                    && Interlocked.Increment(ref heartbeatsDuringTool) == 3)
                {
                    env!.Cancel();
                }
            },
        };
        var function = synchronousTool
            ? AIFunctionFactory.Create(
                (CancellationToken cancellationToken) =>
                {
                    receivedToken = cancellationToken;
                    started.SetResult();
                    if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Activity cancellation was not delivered.");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    return "unexpected success";
                },
                "long_tool")
            : AIFunctionFactory.Create(
                async (CancellationToken cancellationToken) =>
                {
                    receivedToken = cancellationToken;
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    return "unexpected success";
                },
                "long_tool");
        var activities = new DurableFunctionActivities(
            new Dictionary<string, AIFunction> { [function.Name] = function });

        try
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => env.RunAsync(() => activities.InvokeFunctionAsync(
                    new DurableFunctionInput { FunctionName = function.Name })));

            Assert.True(started.Task.IsCompletedSuccessfully);
            Assert.True(Volatile.Read(ref heartbeatsDuringTool) >= 3);
            Assert.True(receivedToken.IsCancellationRequested);
            Assert.Equal(env.CancellationTokenSource.Token, receivedToken);
            Assert.Equal(receivedToken, exception.CancellationToken);
        }
        finally
        {
            env.Cancel();
        }
    }

    [Fact]
    public async Task InvokeFunctionAsync_ToolInternalTimeoutIsBoundedRetryableFailure()
    {
        using var toolTimeout = new CancellationTokenSource();
        toolTimeout.Cancel();
        var function = AIFunctionFactory.Create(
            async (CancellationToken cancellationToken) =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                await Task.Delay(Timeout.InfiniteTimeSpan, toolTimeout.Token);
                return "unexpected success";
            },
            "timeout_tool");
        var activities = new DurableFunctionActivities(
            new Dictionary<string, AIFunction> { [function.Name] = function });
        var env = new ActivityEnvironment();

        var exception = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => env.RunAsync(() => activities.InvokeFunctionAsync(
                new DurableFunctionInput { FunctionName = function.Name })));

        Assert.False(env.CancellationTokenSource.IsCancellationRequested);
        Assert.Equal(nameof(TaskCanceledException), exception.ErrorType);
        Assert.False(exception.NonRetryable);
        Assert.Equal("Durable activity failed; failure details were omitted.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task InvokeFunctionAsync_ReturnsTypedResultTooLargeAfterToolEffectCompletes()
    {
        var invoked = false;
        var largeFunction = AIFunctionFactory.Create(
            () =>
            {
                invoked = true;
                return new string('x', 10_000);
            },
            "large_result");
        var activities = new DurableFunctionActivities(
            new Dictionary<string, AIFunction> { [largeFunction.Name] = largeFunction });
        var input = new DurableFunctionInput
        {
            FunctionName = largeFunction.Name,
            EnableTransportPayloadGuard = true,
            MaximumTransportPayloadSizeBytes = 2_048,
        };
        var env = new ActivityEnvironment
        {
            PayloadConverter = DurableAIDataConverter.Instance.PayloadConverter,
        };

        var output = await env.RunAsync(() => activities.InvokeFunctionAsync(input));

        Assert.True(invoked);
        Assert.Equal(DurableFunctionOutputStatus.ResultTooLarge, output.Status);
        Assert.True(output.EffectCompleted);
        Assert.Equal(DurableFunctionOutput.ResultTooLargeMessage, output.Result);
        Assert.False(output.HasStateReplacement);
    }

    [Fact]
    public async Task InvokeFunctionAsync_OversizedFailureMessageIsNotReturned()
    {
        Func<string> throwing = () => throw new InvalidOperationException(new string('s', 10_000));
        var function = AIFunctionFactory.Create(throwing, "large_failure");
        var activities = new DurableFunctionActivities(
            new Dictionary<string, AIFunction> { [function.Name] = function });
        var input = new DurableFunctionInput { FunctionName = function.Name };
        var env = new ActivityEnvironment();

        var exception = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => env.RunAsync(() => activities.InvokeFunctionAsync(input)));

        Assert.Equal(nameof(InvalidOperationException), exception.ErrorType);
        Assert.False(exception.NonRetryable);
        Assert.DoesNotContain(new string('s', 100), exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
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
            SessionEpoch = "epoch-a",
            RequestId = "request-a",
            TurnNumber = 3,
            IsIdempotent = true,
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
        Assert.Equal("epoch-a", received.Metadata.SessionEpoch);
        Assert.Equal("request-a", received.Metadata.RequestId);
        Assert.Equal(3, received.Metadata.TurnNumber);
        Assert.Equal(3, received.Metadata.Attempt);
        Assert.Equal("queue-a", received.Metadata.TaskQueue);
        Assert.Equal("call-7", received.Metadata.ToolCallId);
        Assert.Equal(2, received.Metadata.ModelIteration);
        Assert.Equal(4, received.Metadata.CallIndex);
        Assert.Equal("conversation", received.Metadata.ConversationId);
        Assert.Equal("correlation", received.Metadata.CorrelationId);
        Assert.StartsWith("tai-v2:", received.Metadata.IdempotencyKey, StringComparison.Ordinal);
        Assert.True(received.Metadata.IsIdempotent);
        Assert.Equal("tenant-a:3:model", Assert.IsType<JsonElement>(output.Result).GetString());
        Assert.False(output.HasStateReplacement);
    }

    private sealed record RequestData(string Tenant);
    private sealed record TurnState(int Count);
}
