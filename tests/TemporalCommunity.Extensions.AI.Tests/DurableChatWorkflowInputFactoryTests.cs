using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Common;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableChatWorkflowInputFactoryTests
{
    [Fact]
    public void Create_FreezesToolInterceptorApprovalAndRetryConfiguration()
    {
        var options = CreateOptions();
        var functions = new DurableFunctionRegistry();
        functions.Register(AIFunctionFactory.Create(() => "ok", "write_record"));
        functions.Register(AIFunctionFactory.Create(() => "ok", "read_record"));

        var toolOptions = new DurableChatToolOptionsRegistry
        {
            ["write_record"] = new DurableChatToolOptions()
                .WithTimeout(TimeSpan.FromSeconds(31))
                .WithMaxAttempts(1)
                .WithInterceptorTimeout(TimeSpan.FromSeconds(32))
                .RequireApproval()
                .WithApprovalTimeout(TimeSpan.FromMinutes(33)),
            ["read_record"] = new DurableChatToolOptions().SkipInterceptor(),
        };
        var factory = new DurableChatWorkflowInputFactory(options, functions, toolOptions);

        var actual = factory.Create();

        Assert.Equal(options.SessionTimeToLive, actual.TimeToLive);
        Assert.Equal(options.ActivityTimeout, actual.ActivityTimeout);
        Assert.Equal(options.HeartbeatTimeout, actual.HeartbeatTimeout);
        Assert.Equal(9, actual.RetryPolicy!.MaximumAttempts);
        Assert.Equal("history-v1", actual.HistoryReducerKey);
        Assert.Equal(13, actual.MaxToolCallsPerTurn);
        Assert.Equal(4, actual.MaximumConsecutiveErrorsPerRequest);
        Assert.True(actual.IncludeDetailedErrors);

        Assert.Equal(2, actual.ToolActivityOptions!.Count);
        Assert.Equal(
            TimeSpan.FromSeconds(31),
            actual.ToolActivityOptions["write_record"].StartToCloseTimeout);
        Assert.Equal(1, actual.ToolActivityOptions["write_record"].RetryPolicy!.MaximumAttempts);
        Assert.Equal(
            options.ActivityTimeout,
            actual.ToolActivityOptions["read_record"].StartToCloseTimeout);
        Assert.Equal(9, actual.ToolActivityOptions["read_record"].RetryPolicy!.MaximumAttempts);

        Assert.NotNull(actual.InterceptorActivityOptions);
        Assert.Equal(
            TimeSpan.FromSeconds(32),
            actual.InterceptorToolActivityOptions!["write_record"].StartToCloseTimeout);
        Assert.Equal("read_record", Assert.Single(actual.InterceptorSkippedTools!));
        Assert.Equal("write_record", Assert.Single(actual.RequiresApprovalTools!));
        Assert.Equal(
            TimeSpan.FromMinutes(33),
            actual.ToolApprovalTimeouts!["write_record"]);
    }

    [Fact]
    public void StockClient_UsesTheSameCanonicalInputAsFactory()
    {
        var options = CreateOptions();
        var functions = new DurableFunctionRegistry();
        functions.Register(AIFunctionFactory.Create(() => "ok", "tool"));
        var toolOptions = new DurableChatToolOptionsRegistry
        {
            ["tool"] = new DurableChatToolOptions().WithMaxAttempts(2),
        };
        IDurableChatWorkflowInputFactory factory =
            new DurableChatWorkflowInputFactory(options, functions, toolOptions);
        var client = new DurableChatSessionClient(
            A.Fake<ITemporalClient>(), options, factory, logger: null);

        var expected = factory.Create();
        var actual = client.CreateWorkflowInput();

        AssertEquivalent(expected, actual);
    }

    [Fact]
    public void RegisterDefaultWorkflowFalse_CreatesThinWorkerOwnedInput()
    {
        var services = new ServiceCollection();
        var worker = services
            .AddHostedTemporalWorker("factory-only")
            .AddDurableAI(options => options.RegisterDefaultWorkflow = false);
        worker.AddDurableTools(AIFunctionFactory.Create(() => "ok", "tool"));

        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IDurableChatWorkflowInputFactory>();
        var input = factory.Create();
        Assert.Null(input.ToolDeclarations);
        Assert.Null(input.ToolActivityOptions);
        Assert.Null(provider.GetService<DurableChatSessionClient>());
        Assert.Null(provider.GetService<ITemporalClient>());
    }

    [Fact]
    public void Create_RoundTripsThroughDurableAIDataConverter()
    {
        var factory = new DurableChatWorkflowInputFactory(
            CreateOptions(),
            new DurableFunctionRegistry(),
            new DurableChatToolOptionsRegistry());
        var input = factory.Create();
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var payload = converter.ToPayload(input);
        var actual = (DurableChatWorkflowInput)converter.ToValue(
            payload, typeof(DurableChatWorkflowInput))!;

        Assert.Equal(input.TimeToLive, actual.TimeToLive);
        Assert.Equal(input.RetryPolicy!.MaximumAttempts, actual.RetryPolicy!.MaximumAttempts);
        Assert.Equal(input.HistoryReducerKey, actual.HistoryReducerKey);
        Assert.Equal(input.MaxToolCallsPerTurn, actual.MaxToolCallsPerTurn);
    }

    private static DurableExecutionOptions CreateOptions() => new()
    {
        TaskQueue = "durable-ai",
        SessionTimeToLive = TimeSpan.FromDays(5),
        ActivityTimeout = TimeSpan.FromMinutes(6),
        HeartbeatTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 9 },
        ApprovalTimeout = TimeSpan.FromHours(7),
        EnableSearchAttributes = true,
        MaxEntryCount = 123,
        DefaultHistoryReducerKey = "history-v1",
        MaxToolCallsPerTurn = 13,
        MaximumConsecutiveErrorsPerRequest = 4,
        IncludeDetailedErrors = true,
        DefaultToolInterceptor = _ => null!,
    };

    private static void AssertEquivalent(
        DurableChatWorkflowInput expected,
        DurableChatWorkflowInput actual)
    {
        Assert.Equal(expected.TimeToLive, actual.TimeToLive);
        Assert.Equal(expected.ActivityTimeout, actual.ActivityTimeout);
        Assert.Equal(expected.HeartbeatTimeout, actual.HeartbeatTimeout);
        Assert.Equal(expected.RetryPolicy!.MaximumAttempts, actual.RetryPolicy!.MaximumAttempts);
        Assert.Equal(expected.ApprovalTimeout, actual.ApprovalTimeout);
        Assert.Equal(expected.EnableSearchAttributes, actual.EnableSearchAttributes);
        Assert.Equal(expected.MaxEntryCount, actual.MaxEntryCount);
        Assert.Equal(expected.HistoryReducerKey, actual.HistoryReducerKey);
        Assert.Equal(expected.MaxToolCallsPerTurn, actual.MaxToolCallsPerTurn);
        Assert.Equal(
            expected.MaximumConsecutiveErrorsPerRequest,
            actual.MaximumConsecutiveErrorsPerRequest);
        Assert.Equal(expected.IncludeDetailedErrors, actual.IncludeDetailedErrors);
        Assert.Equal(
            expected.ToolActivityOptions!.Keys.Order(),
            actual.ToolActivityOptions!.Keys.Order());
        Assert.Equal(
            expected.ToolActivityOptions["tool"].RetryPolicy!.MaximumAttempts,
            actual.ToolActivityOptions["tool"].RetryPolicy!.MaximumAttempts);
    }
}
