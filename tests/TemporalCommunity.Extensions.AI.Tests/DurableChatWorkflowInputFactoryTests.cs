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
                .Idempotent()
                .WithApprovalTimeout(TimeSpan.FromMinutes(33)),
            ["read_record"] = new DurableChatToolOptions().SkipInterceptor().Idempotent(),
        };
        var factory = new DurableChatWorkflowInputFactory(options, functions, toolOptions);

        var actual = factory.Create();

        Assert.Equal(options.SessionTimeToLive, actual.TimeToLive);
        Assert.Equal("primary-profile", actual.DefaultChatClientKey);
        Assert.Equal("summary-profile", actual.HistorySummarizerClientKey);
        Assert.Equal(options.HistorySummarizerMaxContextTokens, actual.HistorySummarizerMaxContextTokens);
        Assert.Equal(
            options.HistorySummarizerOutputTokenReserve,
            actual.HistorySummarizerOutputTokenReserve);
        Assert.Equal(options.ActivityTimeout, actual.ActivityTimeout);
        Assert.Equal(options.HeartbeatTimeout, actual.HeartbeatTimeout);
        Assert.Equal(9, actual.RetryPolicy!.MaximumAttempts);
        Assert.Equal("history-v1", actual.HistoryReducerKey);
        Assert.Equal(13, actual.MaxToolCallsPerTurn);
        Assert.Equal(4, actual.MaximumConsecutiveErrorsPerRequest);
        Assert.Equal(options.MaxTerminalOutcomeCount, actual.MaxTerminalOutcomeCount);
        Assert.Equal(options.MaxTerminalOutcomeBytes, actual.MaxTerminalOutcomeBytes);
        Assert.True(actual.IncludeDetailedErrors);

        Assert.Equal(2, actual.ToolActivityOptions!.Count);
        Assert.Equal(
            TimeSpan.FromSeconds(31),
            actual.ToolActivityOptions["write_record"].StartToCloseTimeout);
        Assert.Equal(1, actual.ToolActivityOptions["write_record"].RetryPolicy!.MaximumAttempts);
        Assert.Equal(
            ["read_record", "write_record"],
            actual.IdempotentTools!.OrderBy(name => name, StringComparer.Ordinal));
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
            ["tool"] = new DurableChatToolOptions().Idempotent().WithMaxAttempts(2),
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
    public void Create_DefaultsSummarizerClientToTheFrozenSessionProfile()
    {
        var options = CreateOptions();
        options.DefaultHistorySummarizerClientKey = null;
        var factory = new DurableChatWorkflowInputFactory(
            options,
            new DurableFunctionRegistry(),
            new DurableChatToolOptionsRegistry());

        var input = factory.Create();

        Assert.Equal(options.DefaultChatClientKey, input.HistorySummarizerClientKey);
    }

    [Fact]
    public void Create_UnmarkedToolDefaultsToOneAttempt()
    {
        var options = CreateOptions();
        options.RetryPolicy = null;
        var functions = new DurableFunctionRegistry();
        functions.Register(AIFunctionFactory.Create(() => "effect", "write_record"));
        var factory = new DurableChatWorkflowInputFactory(
            options,
            functions,
            new DurableChatToolOptionsRegistry());

        var input = factory.Create();

        Assert.Equal(1, input.ToolActivityOptions!["write_record"].RetryPolicy!.MaximumAttempts);
    }

    [Fact]
    public void Create_RejectsMultipleAttemptsForUnmarkedTool()
    {
        var functions = new DurableFunctionRegistry();
        functions.Register(AIFunctionFactory.Create(() => "effect", "write_record"));
        var toolOptions = new DurableChatToolOptionsRegistry
        {
            ["write_record"] = new DurableChatToolOptions().WithMaxAttempts(2),
        };
        var factory = new DurableChatWorkflowInputFactory(CreateOptions(), functions, toolOptions);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create());

        Assert.Contains("Idempotent()", exception.Message);
        Assert.Contains("write_record", exception.Message);
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
        Assert.Equal(input.DefaultChatClientKey, actual.DefaultChatClientKey);
        Assert.Equal(input.HistorySummarizerClientKey, actual.HistorySummarizerClientKey);
        Assert.Equal(input.HistorySummarizerMaxContextTokens, actual.HistorySummarizerMaxContextTokens);
        Assert.Equal(
            input.HistorySummarizerOutputTokenReserve,
            actual.HistorySummarizerOutputTokenReserve);
        Assert.Equal(input.RetryPolicy!.MaximumAttempts, actual.RetryPolicy!.MaximumAttempts);
        Assert.Equal(input.HistoryReducerKey, actual.HistoryReducerKey);
        Assert.Equal(input.MaxToolCallsPerTurn, actual.MaxToolCallsPerTurn);
    }

    [Fact]
    public void Create_WithNoCallerOwnedTools_DoesNotSerializeLegacyEmptyAuthority()
    {
        var factory = new DurableChatWorkflowInputFactory(
            CreateOptions(),
            new DurableFunctionRegistry(),
            new DurableChatToolOptionsRegistry());

        var input = factory.Create();

        Assert.Null(input.ToolDeclarations);
        Assert.Null(input.ToolActivityOptions);
    }

    [Fact]
    public void Create_WhenRetryPolicyUnset_KeepsModelSettingUnsetAndBoundsToolActivities()
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "durable-ai",
            ActivityTimeout = TimeSpan.FromMinutes(6),
            HeartbeatTimeout = TimeSpan.FromMinutes(2),
        };
        var functions = new DurableFunctionRegistry();
        functions.Register(AIFunctionFactory.Create(() => "ok", "read_record"));
        var factory = new DurableChatWorkflowInputFactory(
            options,
            functions,
            new DurableChatToolOptionsRegistry
            {
                ["read_record"] = new DurableChatToolOptions().Idempotent(),
            });

        var input = factory.Create();

        Assert.Null(input.RetryPolicy);
        var retry = input.ToolActivityOptions!["read_record"].RetryPolicy!;
        Assert.Equal(
            global::TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.DefaultMaximumAttempts,
            retry.MaximumAttempts);
        Assert.Equal(
            TimeSpan.FromSeconds(
                global::TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.DefaultToolMaximumIntervalSeconds),
            retry.MaximumInterval);
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
        MaxTerminalOutcomeCount = 37,
        MaxTerminalOutcomeBytes = 98_765,
        DefaultHistoryReducerKey = "history-v1",
        DefaultContextReducerKey = "context-v1",
        DefaultChatClientKey = "primary-profile",
        DefaultHistorySummarizerClientKey = "summary-profile",
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
        Assert.Equal(expected.DefaultChatClientKey, actual.DefaultChatClientKey);
        Assert.Equal(expected.HistorySummarizerClientKey, actual.HistorySummarizerClientKey);
        Assert.Equal(expected.HistorySummarizerMaxContextTokens, actual.HistorySummarizerMaxContextTokens);
        Assert.Equal(
            expected.HistorySummarizerOutputTokenReserve,
            actual.HistorySummarizerOutputTokenReserve);
        Assert.Equal(expected.ActivityTimeout, actual.ActivityTimeout);
        Assert.Equal(expected.HeartbeatTimeout, actual.HeartbeatTimeout);
        Assert.Equal(expected.RetryPolicy!.MaximumAttempts, actual.RetryPolicy!.MaximumAttempts);
        Assert.Equal(expected.ApprovalTimeout, actual.ApprovalTimeout);
        Assert.Equal(expected.EnableSearchAttributes, actual.EnableSearchAttributes);
        Assert.Equal(expected.MaxEntryCount, actual.MaxEntryCount);
        Assert.Equal(expected.ContextReducerKey, actual.ContextReducerKey);
        Assert.Equal(expected.MaxTerminalOutcomeCount, actual.MaxTerminalOutcomeCount);
        Assert.Equal(expected.MaxTerminalOutcomeBytes, actual.MaxTerminalOutcomeBytes);
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
