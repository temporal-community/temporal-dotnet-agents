using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tests.Compat;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public class TypedDurableTurnLifecycleTests
{
    private const string GetChatStepActivity = "TemporalCommunity.Extensions.AI.GetChatStep";
    private const string InvokeFunctionActivity = "TemporalCommunity.Extensions.AI.InvokeFunction";
    private const string ReducerKey = "typed-lifecycle-v1";

    [Fact]
    public async Task ContinueAsNew_PreservesTypedWorkflowAndFrozenConfiguration()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "before continue-as-new")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "post-can-call",
                    "state_tool",
                    new Dictionary<string, object?> { ["value"] = "after" }),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "after continue-as-new")),
        ]);
        var taskQueue = $"typed-lifecycle-can-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, chatClient, maxEntryCount: 2);
        await host.StartAsync();

        var input = host.Services.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();
        var workflowId = $"typed-lifecycle-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (TypedDurableTurnWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions(workflowId, taskQueue));

        await handle.ExecuteUpdateAsync(
            workflow => workflow.TurnAsync(CreateRequest("before")),
            new WorkflowUpdateOptions { Id = "turn-before-can" });
        var initialRunId = (await handle.DescribeAsync()).RunId;
        var postCanRunId = await WaitForNewRunAsync(handle, initialRunId);
        var postCanHandle = env.Client.GetWorkflowHandle<TypedDurableTurnWorkflow>(
            workflowId,
            postCanRunId);

        var configuration = await postCanHandle.QueryAsync(
            workflow => workflow.GetConfiguration());
        Assert.Equal(
            "TemporalCommunity.Extensions.AI.Tests.TypedDurableTurnWorkflow",
            configuration.WorkflowType);
        Assert.Equal(ReducerKey, configuration.HistoryReducerKey);
        Assert.Equal(["approval_only", "state_tool"], configuration.ToolNames.Order());
        Assert.Equal(2, configuration.ToolMaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(9), configuration.ToolTimeout);
        Assert.Equal(6, configuration.MaxToolCallsPerTurn);
        Assert.Equal(2, configuration.MaximumConsecutiveErrorsPerRequest);
        Assert.True(configuration.IncludeDetailedErrors);
        Assert.Equal("approval_only", Assert.Single(configuration.RequiredApprovalTools));
        Assert.Equal(TimeSpan.FromSeconds(77), configuration.ApprovalTimeout);

        var result = await postCanHandle.ExecuteUpdateAsync(
            workflow => workflow.TurnAsync(CreateRequest("after")),
            new WorkflowUpdateOptions { Id = "turn-after-can" });
        Assert.Equal(1, result.FinalTurnState!.Revision);
        Assert.Equal("state_tool", Assert.Single(result.FinalTurnState.Receipts));
        Assert.Equal(
            1,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(
                postCanHandle,
                InvokeFunctionActivity));

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task OneManagedTurnGuard_RejectsSecondCallButAllowsDifferentUpdateIds()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "first")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "second update")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "third update")),
        ]);
        var taskQueue = $"typed-turn-guard-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            chatClient,
            maximumConsecutiveErrors: 0);
        await host.StartAsync();
        var handle = await StartAsync(env.Client, host.Services, taskQueue);

        await Assert.ThrowsAnyAsync<Exception>(() => handle.ExecuteUpdateAsync(
            workflow => workflow.DoubleTurnAsync(CreateRequest("double")),
            new WorkflowUpdateOptions { Id = "same-update" }));
        Assert.Equal(1, chatClient.CallCount);
        Assert.Equal(
            1,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, GetChatStepActivity));

        await handle.ExecuteUpdateAsync(
            workflow => workflow.TurnAsync(CreateRequest("different-1")),
            new WorkflowUpdateOptions { Id = "different-update-1" });
        await handle.ExecuteUpdateAsync(
            workflow => workflow.TurnAsync(CreateRequest("different-2")),
            new WorkflowUpdateOptions { Id = "different-update-2" });
        Assert.Equal(3, chatClient.CallCount);

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task OneManagedTurnGuard_RemainsConsumedAfterFirstTurnFails()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var chatClient = new AlwaysFailingChatClient();
        var taskQueue = $"typed-turn-failure-guard-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            chatClient,
            maximumConsecutiveErrors: 0);
        await host.StartAsync();
        var handle = await StartAsync(env.Client, host.Services, taskQueue);

        await Assert.ThrowsAnyAsync<Exception>(() => handle.ExecuteUpdateAsync(
            workflow => workflow.FailThenSecondTurnAsync(CreateRequest("failure")),
            new WorkflowUpdateOptions { Id = "failed-same-update" }));

        Assert.True(chatClient.CallCount >= 1);
        Assert.Equal(
            1,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, GetChatStepActivity));
        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task UnknownDispatch_FailsUpdateBeforeModelOrToolDispatch()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "must not run")),
        ]);
        var taskQueue = $"typed-turn-invalid-dispatch-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, chatClient);
        await host.StartAsync();
        var handle = await StartAsync(env.Client, host.Services, taskQueue);
        var request = new DurableTurnRequest<TypedTurnRequestData, TypedTurnState>
        {
            Messages = [new ChatMessage(ChatRole.User, "invalid-dispatch")],
            RequestData = new TypedTurnRequestData("invalid-dispatch"),
            InitialTurnState = new TypedTurnState(0, []),
            CorrelationId = "invalid-dispatch",
            Options = new DurableTurnOptions { DispatchMode = (DurableToolDispatchMode)99 },
        };

        await Assert.ThrowsAnyAsync<Exception>(() => handle.ExecuteUpdateAsync(
            workflow => workflow.TurnAsync(request),
            new WorkflowUpdateOptions { Id = "invalid-dispatch" }));

        Assert.Equal(0, chatClient.CallCount);
        Assert.Equal(
            0,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, GetChatStepActivity));
        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task InvalidRequests_FailPromptlyWithoutDispatchAndLaterValidUpdateSucceeds()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "valid-after-rejections")),
        ]);
        var taskQueue = $"typed-turn-invalid-request-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, chatClient);
        await host.StartAsync();
        var handle = await StartAsync(env.Client, host.Services, taskQueue);
        DurableTurnRequest<TypedTurnRequestData, TypedTurnState>?[] invalidRequests =
        [
            null,
            new DurableTurnRequest<TypedTurnRequestData, TypedTurnState>
            {
                Messages = [],
                RequestData = new TypedTurnRequestData("empty-messages"),
                InitialTurnState = new TypedTurnState(0, []),
                CorrelationId = "empty-messages",
            },
        ];

        for (var index = 0; index < invalidRequests.Length; index++)
        {
            var request = invalidRequests[index];
            var exception = await Record.ExceptionAsync(() =>
                handle.ExecuteUpdateAsync(
                    workflow => workflow.TurnAsync(request!),
                    new WorkflowUpdateOptions { Id = $"invalid-request-{index}" })
                .WaitAsync(TimeSpan.FromSeconds(5)));
            AssertInvalidRequestFailure(exception, $"invalid request {index}");
        }

        var nullOptionsException = await Record.ExceptionAsync(() =>
            handle.ExecuteUpdateAsync(
                workflow => workflow.TurnWithNullOptionsAsync(CreateRequest("null-options")),
                new WorkflowUpdateOptions { Id = "invalid-request-null-options" })
            .WaitAsync(TimeSpan.FromSeconds(5)));
        AssertInvalidRequestFailure(nullOptionsException, "null options");

        Assert.Equal(0, chatClient.CallCount);
        Assert.Equal(
            0,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, GetChatStepActivity));
        Assert.Equal(
            0,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, InvokeFunctionActivity));

        var result = await handle.ExecuteUpdateAsync(
            workflow => workflow.TurnAsync(CreateRequest("valid-after-rejections")),
            new WorkflowUpdateOptions { Id = "valid-after-rejections" });
        Assert.Equal("valid-after-rejections", result.Response.Messages[^1].Text);
        Assert.Equal(1, chatClient.CallCount);

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();

        static void AssertInvalidRequestFailure(Exception? exception, string scenario)
        {
            Assert.True(exception is not null, $"The {scenario} Update completed successfully.");
            var failure = Assert.IsType<WorkflowUpdateFailedException>(exception);
            var applicationFailure = Assert.IsType<ApplicationFailureException>(
                failure.InnerException);
            Assert.Equal(
                DurableToolWorkflowBase<TypedTurnRequestData, TypedTurnState>
                    .InvalidRequestErrorType,
                applicationFailure.ErrorType);
            Assert.True(applicationFailure.NonRetryable);
        }
    }

    [Fact]
    public async Task IterationLimit_PersistsOnlySentinelForTheNextTurn()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "discarded-call",
                    "state_tool",
                    new Dictionary<string, object?> { ["value"] = "discarded" }),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "second turn complete")),
        ]);
        var taskQueue = $"typed-turn-limit-history-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            chatClient,
            maxToolCallsPerTurn: 1);
        await host.StartAsync();
        var handle = await StartAsync(env.Client, host.Services, taskQueue);

        var capped = await handle.ExecuteUpdateAsync(
            workflow => workflow.TurnAsync(CreateRequest("capped-turn")),
            new WorkflowUpdateOptions { Id = "capped-turn" });

        Assert.Equal(DurableTurnCompletionReason.IterationLimitReached, capped.CompletionReason);
        Assert.Contains(
            capped.Response.Messages.SelectMany(message => message.Contents),
            content => content is FunctionCallContent);
        Assert.Contains(
            capped.Response.Messages.SelectMany(message => message.Contents),
            content => content is FunctionResultContent);
        Assert.Equal(1, capped.FinalTurnState!.Revision);

        var history = await handle.QueryAsync(workflow => workflow.GetHistory());
        var storedResponse = Assert.IsType<DurableSessionResponse>(history[1]);
        var sentinel = Assert.Single(storedResponse.Messages);
        Assert.Equal(ChatRole.Assistant, sentinel.Role);
        Assert.Contains("Maximum tool-call iterations", sentinel.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            storedResponse.Messages.SelectMany(message => message.Contents),
            content => content is FunctionCallContent or FunctionResultContent);

        var next = await handle.ExecuteUpdateAsync(
            workflow => workflow.TurnAsync(CreateRequest("next-turn")),
            new WorkflowUpdateOptions { Id = "next-turn" });

        Assert.Equal(DurableTurnCompletionReason.FinalResponse, next.CompletionReason);
        var nextModelInput = chatClient.Calls[1].Messages;
        Assert.Contains(nextModelInput, message =>
            message.Text.Contains("Maximum tool-call iterations", StringComparison.Ordinal));
        Assert.DoesNotContain(
            nextModelInput.SelectMany(message => message.Contents),
            content => content is FunctionCallContent or FunctionResultContent);

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        int maxEntryCount = 1000,
        int maximumConsecutiveErrors = 2,
        int maxToolCallsPerTurn = 6)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(chatClient).Build();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());
        builder.Services.AddKeyedSingleton<Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
            ReducerKey,
            (_, _) => history => history.TakeLast(1).ToList());

        var worker = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.RegisterDefaultWorkflow = false;
                options.MaxEntryCount = maxEntryCount;
                options.DefaultHistoryReducerKey = ReducerKey;
                options.MaxToolCallsPerTurn = maxToolCallsPerTurn;
                options.MaximumConsecutiveErrorsPerRequest = maximumConsecutiveErrors;
                options.IncludeDetailedErrors = true;
                options.ApprovalTimeout = TimeSpan.FromSeconds(77);
                options.RetryPolicy = new RetryPolicy { MaximumAttempts = 1 };
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
            })
            .AddWorkflow<TypedDurableTurnWorkflow>();

        var stateDeclaration = AIFunctionFactory.Create(
            (string value) => string.Empty,
            "state_tool",
            "Updates typed state.").AsDeclarationOnly();
        worker.AddDurableToolFactory<TypedTurnRequestData, TypedTurnState>(
            stateDeclaration,
            (_, context) => new DurableToolActivation<TypedTurnState>
            {
                Function = AIFunctionFactory.Create(
                    (string value) => value,
                    "state_tool",
                    "Updates typed state."),
                CompleteState = (_, _) => ValueTask.FromResult(
                    DurableStateUpdate<TypedTurnState>.Replace(
                        new TypedTurnState(
                            (context.TurnState?.Revision ?? 0) + 1,
                            [.. context.TurnState?.Receipts ?? [], "state_tool"]))),
            },
            options => options
                .WithTimeout(TimeSpan.FromSeconds(9))
                .WithMaxAttempts(2));

        var approvalDeclaration = AIFunctionFactory.Create(
            () => string.Empty,
            "approval_only",
            "Freezes approval policy without invoking it.").AsDeclarationOnly();
        worker.AddDurableToolFactory<TypedTurnRequestData, TypedTurnState>(
            approvalDeclaration,
            (_, _) => new DurableToolActivation<TypedTurnState>
            {
                Function = AIFunctionFactory.Create(
                    () => "unused",
                    "approval_only",
                    "Freezes approval policy without invoking it."),
            },
            options => options.RequireApproval().WithApprovalTimeout(TimeSpan.FromSeconds(33)));

        return builder.Build();
    }

    private static DurableTurnRequest<TypedTurnRequestData, TypedTurnState> CreateRequest(
        string operationId) => new()
        {
            Messages = [new ChatMessage(ChatRole.User, operationId)],
            RequestData = new TypedTurnRequestData(operationId),
            InitialTurnState = new TypedTurnState(0, []),
            CorrelationId = operationId,
        };

    private static async Task<WorkflowHandle<TypedDurableTurnWorkflow>> StartAsync(
        ITemporalClient client,
        IServiceProvider services,
        string taskQueue)
    {
        var input = services.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();
        return await client.StartWorkflowAsync(
            (TypedDurableTurnWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions($"typed-turn-{Guid.NewGuid():N}", taskQueue));
    }

    private static async Task<string> WaitForNewRunAsync(
        WorkflowHandle<TypedDurableTurnWorkflow> handle,
        string initialRunId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var current = (await handle.DescribeAsync()).RunId;
            if (!string.Equals(current, initialRunId, StringComparison.Ordinal))
            {
                return current;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Typed workflow did not continue as new.");
    }

    private sealed class AlwaysFailingChatClient : IChatClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public ChatClientMetadata Metadata { get; } = new("always-failing");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            throw new ApplicationFailureException(
                "Injected model failure.",
                errorType: "TypedLifecycleFailure",
                nonRetryable: true);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            throw new ApplicationFailureException(
                "Injected model failure.",
                errorType: "TypedLifecycleFailure",
                nonRetryable: true);
#pragma warning disable CS0162 // Required to keep this method an async iterator.
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoopEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } = new("noop", null, null, 1);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new[] { 0f })).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
