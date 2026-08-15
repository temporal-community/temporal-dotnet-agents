using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public class ExtensibleDurableTurnIntegrationTests
{
    private const string GetChatStepActivity = "TemporalCommunity.Extensions.AI.GetChatStep";
    private const string InvokeFunctionActivity = "TemporalCommunity.Extensions.AI.InvokeFunction";

    [Fact]
    public async Task WorkerOwnedBaseline_CanBeNarrowedPerTurnWithoutExpansion()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("excluded", "first_tool")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "excluded call handled")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("included", "first_tool")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "included call handled")),
        ]);
        var firstInvocations = 0;
        var taskQueue = $"typed-toolset-narrowing-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(env.Client);
        builder.Services.AddChatClient(chatClient).Build();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());
        var worker = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options => options.RegisterDefaultWorkflow = false)
            .AddWorkflow<NarrowingDurableTurnWorkflow>();
        worker.AddDurableToolset("first", tools => tools.Add(
            AIFunctionFactory.Create(() =>
            {
                Interlocked.Increment(ref firstInvocations);
                return "first";
            }, "first_tool")));
        worker.AddDurableToolset("second", tools => tools.Add(
            AIFunctionFactory.Create(() => "second", "second_tool")));

        using var host = builder.Build();
        await host.StartAsync();
        var input = host.Services.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();
        var handle = await env.Client.StartWorkflowAsync(
            (NarrowingDurableTurnWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions($"typed-toolset-narrowing-{Guid.NewGuid():N}", taskQueue));

        var excluded = CreateRequestWithToolsets("first tool is outside this turn", ["second"]);
        await handle.ExecuteUpdateAsync<NarrowingDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
            workflow => workflow.TurnAsync(excluded),
            new WorkflowUpdateOptions { Id = "narrow-second" });
        Assert.Equal(0, firstInvocations);

        var included = CreateRequestWithToolsets("first tool is enabled", ["first"]);
        await handle.ExecuteUpdateAsync<NarrowingDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
            workflow => workflow.TurnAsync(included),
            new WorkflowUpdateOptions { Id = "narrow-first" });

        Assert.Equal(1, firstInvocations);
        Assert.All(chatClient.Calls.Take(2), call =>
            Assert.Equal(["second_tool"], call.Options!.Tools!.Select(tool => tool.Name)));
        Assert.All(chatClient.Calls.Skip(2), call =>
            Assert.Equal(["first_tool"], call.Options!.Tools!.Select(tool => tool.Name)));
        Assert.Equal(
            1,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, InvokeFunctionActivity));

        var attemptedExpansion = CreateRequestWithToolsets("expand authority", ["missing"]);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            handle.ExecuteUpdateAsync<NarrowingDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
                workflow => workflow.TurnAsync(attemptedExpansion),
                new WorkflowUpdateOptions { Id = "attempt-expansion" }));
        Assert.Equal(4, chatClient.CallCount);
        Assert.Equal(
            4,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, GetChatStepActivity));

        chatClient.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "queued turn complete")));
        chatClient.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "queued turn complete")));
        var queuedFirstRequest = CreateRequestWithToolsets("queued-first", ["first"]);
        var queuedSecondRequest = CreateRequestWithToolsets("queued-second", ["second"]);
        var queuedFirst = handle.ExecuteUpdateAsync<NarrowingDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
            workflow => workflow.TurnAsync(queuedFirstRequest),
            new WorkflowUpdateOptions { Id = "queued-first" });
        var queuedSecond = handle.ExecuteUpdateAsync<NarrowingDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
            workflow => workflow.TurnAsync(queuedSecondRequest),
            new WorkflowUpdateOptions { Id = "queued-second" });
        await Task.WhenAll(queuedFirst, queuedSecond);

        var queuedCalls = chatClient.Calls.Skip(4).ToArray();
        Assert.Equal(2, queuedCalls.Length);
        var firstCall = Assert.Single(queuedCalls, call =>
            call.Messages.Last(message => message.Role == ChatRole.User).Text == "queued-first");
        var secondCall = Assert.Single(queuedCalls, call =>
            call.Messages.Last(message => message.Role == ChatRole.User).Text == "queued-second");
        Assert.Equal(["first_tool"], firstCall.Options!.Tools!.Select(tool => tool.Name));
        Assert.Equal(["second_tool"], secondCall.Options!.Tools!.Select(tool => tool.Name));

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task ActivityOnlyWorker_ExecutesToolWithoutDITemporalClient()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var taskQueue = $"activity-only-durable-tool-{Guid.NewGuid():N}";
        var observations = new ConcurrentBag<ToolObservation>();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddChatClient(OneToolThenFinal()).Build();
        builder.Services.AddHttpClient("durable-tool-attempt");
        builder.Services.AddSingleton(new AttemptScopeTracker());
        builder.Services.AddScoped<AttemptScopedToolServices>();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());
        var worker = builder.Services
            .AddHostedTemporalWorker(
                env.Client.Connection.Options.TargetHost
                    ?? throw new InvalidOperationException("Test server target host is unavailable."),
                env.Client.Options.Namespace,
                taskQueue)
            .AddDurableAI(options => options.RegisterDefaultWorkflow = false)
            .AddWorkflow<IntegrationDurableTurnWorkflow>();
        RegisterTool(
            worker,
            "decision_tool",
            observations,
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal),
            failFirstAttempt: false);

        using var host = builder.Build();
        Assert.Null(host.Services.GetService<ITemporalClient>());
        await host.StartAsync();

        var handle = await StartWorkflowAsync(env.Client, host.Services, taskQueue);
        var result = await handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
            workflow => workflow.TurnAsync(CreateRequest()),
            new WorkflowUpdateOptions { Id = "activity-only-worker" });

        Assert.Equal(DurableTurnCompletionReason.FinalResponse, result.CompletionReason);
        Assert.Single(observations);
        Assert.Equal(
            1,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, InvokeFunctionActivity));

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task StateCompletionFailure_FailsTurnWithoutRepeatingOrdinaryFunction()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var chatClient = ScriptedChatClient.WithRepeatingToolThenFinal(
            index => new FunctionCallContent(
                $"state-failure-{index}",
                "state_failure",
                new Dictionary<string, object?>()),
            repeatCount: 2,
            finalText: "The model should never reach this response.");
        var invocationCount = 0;
        var taskQueue = $"extensible-state-failure-{Guid.NewGuid():N}";
        using var host = BuildStateCompletionFailureHost(
            env.Client,
            taskQueue,
            chatClient,
            () => Interlocked.Increment(ref invocationCount));
        await host.StartAsync();

        var handle = await StartWorkflowAsync(env.Client, host.Services, taskQueue);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
                workflow => workflow.TurnAsync(CreateRequest()),
                new WorkflowUpdateOptions { Id = "state-completion-failure" }));

        Assert.Equal(1, invocationCount);
        Assert.Equal(1, chatClient.CallCount);
        var history = await handle.QueryAsync<IntegrationDurableTurnWorkflow, IReadOnlyList<DurableSessionEntry>>(
            workflow => workflow.GetHistory());
        Assert.Empty(history);

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task SequentialStateCompletionFailure_DoesNotScheduleLaterTool()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "fatal-first",
                    "state_failure",
                    new Dictionary<string, object?>()),
                new FunctionCallContent(
                    "must-not-run",
                    "later_tool",
                    new Dictionary<string, object?>()),
            ])),
        ]);
        var firstInvocationCount = 0;
        var laterInvocationCount = 0;
        var taskQueue = $"extensible-state-failure-order-{Guid.NewGuid():N}";
        using var host = BuildStateCompletionFailureHost(
            env.Client,
            taskQueue,
            chatClient,
            () => Interlocked.Increment(ref firstInvocationCount),
            () => Interlocked.Increment(ref laterInvocationCount));
        await host.StartAsync();

        var handle = await StartWorkflowAsync(env.Client, host.Services, taskQueue);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
                workflow => workflow.TurnAsync(CreateRequest()),
                new WorkflowUpdateOptions { Id = "sequential-fatal-stop" }));

        Assert.Equal(1, firstInvocationCount);
        Assert.Equal(0, laterInvocationCount);
        Assert.Equal(
            1,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, InvokeFunctionActivity));

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task FailedTurn_RollsBackHistoryAndTurnCountBeforeNextUpdate()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var chatClient = new ScriptedChatClient([]);
        var taskQueue = $"extensible-rollback-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            chatClient,
            new ConcurrentBag<ToolObservation>(),
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal),
            options => options.MaximumConsecutiveErrorsPerRequest = 0);
        await host.StartAsync();

        var handle = await StartWorkflowAsync(env.Client, host.Services, taskQueue);
        var failedRequest = CreateRequest(
            "This failed request must be rolled back.");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
                workflow => workflow.TurnAsync(failedRequest),
                new WorkflowUpdateOptions { Id = "failed-turn" }));

        var historyAfterFailure = await handle.QueryAsync<IntegrationDurableTurnWorkflow, IReadOnlyList<DurableSessionEntry>>(
            workflow => workflow.GetHistory());
        Assert.Empty(historyAfterFailure);

        chatClient.Enqueue(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "The next turn succeeded.")));
        var successfulRequest = CreateRequest("This request should be retained.");
        var result = await handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
            workflow => workflow.TurnAsync(successfulRequest),
            new WorkflowUpdateOptions { Id = "successful-turn" });

        Assert.Equal(DurableTurnCompletionReason.FinalResponse, result.CompletionReason);
        var modelCall = Assert.Single(chatClient.Calls);
        var modelInput = string.Join("\n", modelCall.Messages.Select(message => message.Text));
        Assert.Contains("This request should be retained.", modelInput, StringComparison.Ordinal);
        Assert.DoesNotContain("This failed request must be rolled back.", modelInput, StringComparison.Ordinal);

        var historyAfterSuccess = await handle.QueryAsync<IntegrationDurableTurnWorkflow, IReadOnlyList<DurableSessionEntry>>(
            workflow => workflow.GetHistory());
        Assert.Collection(
            historyAfterSuccess,
            entry => Assert.IsType<DurableSessionRequest>(entry),
            entry => Assert.IsType<DurableSessionResponse>(entry));

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task TypedSequentialTurn_RetriesWithStableIdentity_AndReturnsFinalState()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var chatClient = ScriptedChatClient.WithToolCallsThenFinal(
            [
                new FunctionCallContent(
                    "call-first",
                    "apply_first",
                    new Dictionary<string, object?> { ["value"] = "one" }),
                new FunctionCallContent(
                    "call-second",
                    "apply_second",
                    new Dictionary<string, object?> { ["value"] = "two" }),
            ],
            "The durable turn completed.");
        var observations = new ConcurrentBag<ToolObservation>();
        var externalEffects = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var attemptScopes = new AttemptScopeTracker();
        var taskQueue = $"extensible-turn-{Guid.NewGuid():N}";

        using var host = BuildHost(
            env.Client,
            taskQueue,
            chatClient,
            observations,
            externalEffects,
            attemptScopes: attemptScopes);
        await host.StartAsync();

        var input = host.Services.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();
        var workflowId = $"extensible-turn-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (IntegrationDurableTurnWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions(workflowId, taskQueue));
        var request = new DurableTurnRequest<IntegrationRequestData, IntegrationTurnState>
        {
            Messages = [new ChatMessage(ChatRole.User, "Run both tools.")],
            RequestData = new IntegrationRequestData("operation-1", "trusted-subject"),
            InitialTurnState = new IntegrationTurnState(0, []),
        };

        var result = await handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
            workflow => workflow.TurnAsync(request),
            new WorkflowUpdateOptions { Id = "turn-update-1" });

        Assert.Equal(DurableTurnCompletionReason.FinalResponse, result.CompletionReason);
        Assert.Equal("The durable turn completed.", result.Response.Text);
        Assert.Equal(2, result.FinalTurnState?.Revision);
        Assert.Equal(["apply_first", "apply_second"], result.FinalTurnState?.Receipts);

        var firstAttempts = observations
            .Where(observation => observation.ToolName == "apply_first")
            .OrderBy(observation => observation.Attempt)
            .ToArray();
        Assert.Equal([1, 2], firstAttempts.Select(observation => observation.Attempt));
        Assert.Single(firstAttempts.Select(observation => observation.IdempotencyKey).Distinct());
        Assert.All(firstAttempts, observation => Assert.Equal("operation-1", observation.OperationId));
        Assert.Equal(0, firstAttempts[0].ObservedRevision);
        Assert.Equal(0, firstAttempts[1].ObservedRevision);
        Assert.Equal(2, firstAttempts.Select(observation => observation.ScopeId).Distinct().Count());

        var second = Assert.Single(observations, observation => observation.ToolName == "apply_second");
        Assert.Equal(1, second.ObservedRevision);
        Assert.Equal(2, externalEffects.Count);
        Assert.DoesNotContain(second.ScopeId, firstAttempts.Select(observation => observation.ScopeId));

        await WaitUntilAsync(
            () => attemptScopes.Disposed.Count == 3,
            "activity-attempt scopes to be disposed");
        Assert.Equal(3, attemptScopes.Created.Count);
        Assert.Equal(attemptScopes.Created.Order(), attemptScopes.Disposed.Order());

        var firstModelCall = Assert.Single(chatClient.Calls.Take(1));
        Assert.NotNull(firstModelCall.Options?.Tools);
        Assert.Equal(["apply_first", "apply_second"], firstModelCall.Options!.Tools!.Select(tool => tool.Name));
        Assert.All(firstModelCall.Options.Tools, tool =>
        {
            var schema = Assert.IsAssignableFrom<AIFunctionDeclaration>(tool).JsonSchema.GetRawText();
            Assert.DoesNotContain(nameof(IntegrationRequestData), schema, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(IntegrationTurnState), schema, StringComparison.Ordinal);
            Assert.DoesNotContain("operationId", schema, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("revision", schema, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Equal(
            2,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, GetChatStepActivity));
        Assert.Equal(
            2,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, InvokeFunctionActivity));

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task ActivityExecutionAdapter_Retry_RecordsErrorFinallyThenFreshScopeSuccess()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var chatClient = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-first", "apply_first",
                new Dictionary<string, object?> { ["value"] = "one" })],
            "done");
        var lifecycle = new AdapterLifecycleRecorder();
        var taskQueue = $"extensible-adapter-retry-{Guid.NewGuid():N}";

        using var host = BuildHost(
            env.Client,
            taskQueue,
            chatClient,
            new ConcurrentBag<ToolObservation>(),
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal),
            adapterLifecycle: lifecycle);
        await host.StartAsync();

        var handle = await StartWorkflowAsync(env.Client, host.Services, taskQueue);
        var result = await handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
            workflow => workflow.TurnAsync(CreateRequest()),
            new WorkflowUpdateOptions { Id = "adapter-retry" });

        Assert.Equal(DurableTurnCompletionReason.FinalResponse, result.CompletionReason);
        var firstAttempt = lifecycle.Entries
            .Where(entry => entry.ToolName == "apply_first" && entry.Attempt == 1)
            .ToArray();
        var secondAttempt = lifecycle.Entries
            .Where(entry => entry.ToolName == "apply_first" && entry.Attempt == 2)
            .ToArray();
        Assert.Equal(["before", "error", "finally"], firstAttempt.Select(entry => entry.Stage));
        Assert.Equal(["before", "success", "finally"], secondAttempt.Select(entry => entry.Stage));
        Assert.Single(firstAttempt.Select(entry => entry.ScopeId).Distinct());
        Assert.Single(secondAttempt.Select(entry => entry.ScopeId).Distinct());
        Assert.NotEqual(firstAttempt[0].ScopeId, secondAttempt[0].ScopeId);

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task ActivityExecutionAdapter_Denial_DoesNotInvokeInnerEffectOrStateCompletion()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var lifecycle = new AdapterLifecycleRecorder();
        var innerCalls = 0;
        var externalEffects = 0;
        var stateCompletions = 0;
        var taskQueue = $"extensible-adapter-denied-{Guid.NewGuid():N}";
        var chatClient = OneToolThenFinal();
        using var host = BuildDeniedAdapterHost(
            env.Client,
            taskQueue,
            chatClient,
            lifecycle,
            () => Interlocked.Increment(ref innerCalls),
            () => Interlocked.Increment(ref externalEffects),
            () => Interlocked.Increment(ref stateCompletions));
        await host.StartAsync();

        var handle = await StartWorkflowAsync(env.Client, host.Services, taskQueue);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
                workflow => workflow.TurnAsync(CreateRequest()),
                new WorkflowUpdateOptions { Id = "adapter-denied" }));

        Assert.Equal(0, innerCalls);
        Assert.Equal(0, externalEffects);
        Assert.Equal(0, stateCompletions);
        Assert.Equal(
            ["before", "denied", "error", "finally"],
            lifecycle.Entries.Select(entry => entry.Stage));
        Assert.Equal(
            1,
            await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, InvokeFunctionActivity));

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task SequentialTurn_InterceptorProceed_InvokesTool()
    {
        var outcome = await RunInterceptorOutcomeAsync(DurableToolDecision.Proceed());

        var observation = Assert.Single(outcome.Observations);
        Assert.Equal("original", observation.Value);
    }

    [Fact]
    public async Task SequentialTurn_InterceptorArgumentRewrite_UsesReplacementArguments()
    {
        var outcome = await RunInterceptorOutcomeAsync(DurableToolDecision.Proceed(
            modifiedArguments: new Dictionary<string, object?> { ["value"] = "rewritten" }));

        var observation = Assert.Single(outcome.Observations);
        Assert.Equal("rewritten", observation.Value);
    }

    [Fact]
    public async Task SequentialTurn_InterceptorSkip_DoesNotInvokeTool()
    {
        var outcome = await RunInterceptorOutcomeAsync(DurableToolDecision.Skip("cached-result"));

        Assert.Empty(outcome.Observations);
        Assert.Contains("cached-result", outcome.ModelInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SequentialTurn_InterceptorBlock_DoesNotInvokeTool()
    {
        var outcome = await RunInterceptorOutcomeAsync(DurableToolDecision.Block("policy-denied"));

        Assert.Empty(outcome.Observations);
        Assert.Contains("policy-denied", outcome.ModelInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SequentialTurn_InterceptorApproval_ParksBeforeInvokingTool()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var observations = new ConcurrentBag<ToolObservation>();
        var taskQueue = $"extensible-approval-{Guid.NewGuid():N}";
        var chatClient = OneToolThenFinal();
        var interceptor = new DelegateInterceptor((_, _) => Task.FromResult(
            DurableToolDecision.PauseForApproval("Review decision_tool.")));

        using var host = BuildSingleToolHost(
            env.Client,
            taskQueue,
            chatClient,
            observations,
            interceptor);
        await host.StartAsync();
        var handle = await StartWorkflowAsync(env.Client, host.Services, taskQueue);
        var turnTask = handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
            workflow => workflow.TurnAsync(CreateRequest()),
            new WorkflowUpdateOptions { Id = "approval-turn" });

        DurableApprovalRequest? pending = null;
        while (!turnTask.IsCompleted && pending is null)
        {
            await Task.Delay(20);
            pending = await handle.QueryAsync<IntegrationDurableTurnWorkflow, DurableApprovalRequest?>(
                workflow => workflow.GetPendingApproval());
        }

        Assert.NotNull(pending);
        Assert.Empty(observations);
        var resolution = await handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableApprovalResolutionResult>(
            workflow => workflow.ResolveApprovalAsync(new DurableApprovalDecision
            {
                RequestId = pending!.RequestId,
                Approved = true,
                Reason = "approved",
            }));
        var result = await turnTask;

        Assert.Equal(DurableApprovalResolutionStatus.Accepted, resolution.Status);
        Assert.Equal(DurableTurnCompletionReason.FinalResponse, result.CompletionReason);
        Assert.Single(observations);
        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    private static async Task<InterceptorOutcome> RunInterceptorOutcomeAsync(
        DurableToolDecision decision)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var observations = new ConcurrentBag<ToolObservation>();
        var taskQueue = $"extensible-decision-{Guid.NewGuid():N}";
        var chatClient = OneToolThenFinal();
        var interceptor = new DelegateInterceptor((_, _) => Task.FromResult(decision));

        using var host = BuildSingleToolHost(
            env.Client,
            taskQueue,
            chatClient,
            observations,
            interceptor);
        await host.StartAsync();
        var handle = await StartWorkflowAsync(env.Client, host.Services, taskQueue);
        var result = await handle.ExecuteUpdateAsync<IntegrationDurableTurnWorkflow, DurableTurnResult<IntegrationTurnState>>(
            workflow => workflow.TurnAsync(CreateRequest()),
            new WorkflowUpdateOptions { Id = $"decision-{Guid.NewGuid():N}" });

        Assert.Equal(DurableTurnCompletionReason.FinalResponse, result.CompletionReason);
        var secondModelInput = string.Join(
            "\n",
            chatClient.Calls[1].Messages
                .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                .Select(content => content.Result?.ToString()));
        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
        return new InterceptorOutcome(observations.ToArray(), secondModelInput);
    }

    private static IHost BuildSingleToolHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        ConcurrentBag<ToolObservation> observations,
        IDurableToolInterceptor<DurableToolContext> interceptor)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(chatClient).Build();
        builder.Services.AddHttpClient("durable-tool-attempt");
        builder.Services.AddSingleton(new AttemptScopeTracker());
        builder.Services.AddScoped<AttemptScopedToolServices>();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());
        builder.Services.AddSingleton(interceptor);

        var worker = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.RegisterDefaultWorkflow = false;
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                options.DefaultToolInterceptor = services =>
                    services.GetRequiredService<IDurableToolInterceptor<DurableToolContext>>();
            })
            .AddWorkflow<IntegrationDurableTurnWorkflow>();
        RegisterTool(
            worker,
            "decision_tool",
            observations,
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal),
            failFirstAttempt: false);
        return builder.Build();
    }

    private static async Task<WorkflowHandle<IntegrationDurableTurnWorkflow>> StartWorkflowAsync(
        ITemporalClient client,
        IServiceProvider services,
        string taskQueue)
    {
        var input = services.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();
        return await client.StartWorkflowAsync(
            (IntegrationDurableTurnWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions($"extensible-decision-{Guid.NewGuid():N}", taskQueue));
    }

    private static ScriptedChatClient OneToolThenFinal() =>
        ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent(
                "call-decision",
                "decision_tool",
                new Dictionary<string, object?> { ["value"] = "original" })],
            "done");

    private static DurableTurnRequest<IntegrationRequestData, IntegrationTurnState> CreateRequest(
        string message = "Run the decision tool.") =>
        new()
        {
            Messages = [new ChatMessage(ChatRole.User, message)],
            RequestData = new IntegrationRequestData("operation-decision", "trusted-subject"),
            InitialTurnState = new IntegrationTurnState(0, []),
        };

    private static DurableTurnRequest<IntegrationRequestData, IntegrationTurnState>
        CreateRequestWithToolsets(string message, IReadOnlyList<string> toolsetIds) =>
        new()
        {
            Messages = [new ChatMessage(ChatRole.User, message)],
            RequestData = new IntegrationRequestData("operation-decision", "trusted-subject"),
            InitialTurnState = new IntegrationTurnState(0, []),
            Options = new DurableTurnOptions { ToolsetIds = toolsetIds },
        };

    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        ConcurrentBag<ToolObservation> observations,
        ConcurrentDictionary<string, byte> externalEffects,
        Action<DurableExecutionOptions>? configureOptions = null,
        AttemptScopeTracker? attemptScopes = null,
        AdapterLifecycleRecorder? adapterLifecycle = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(chatClient).Build();
        builder.Services.AddHttpClient("durable-tool-attempt");
        builder.Services.AddSingleton(attemptScopes ?? new AttemptScopeTracker());
        builder.Services.AddScoped<AttemptScopedToolServices>();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());

        var worker = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.RegisterDefaultWorkflow = false;
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                configureOptions?.Invoke(options);
            })
            .AddWorkflow<IntegrationDurableTurnWorkflow>();

        RegisterTool(worker, "apply_first", observations, externalEffects, failFirstAttempt: true, adapterLifecycle);
        RegisterTool(worker, "apply_second", observations, externalEffects, failFirstAttempt: false, adapterLifecycle);
        return builder.Build();
    }

    private static IHost BuildDeniedAdapterHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        AdapterLifecycleRecorder lifecycle,
        Action onInner,
        Action onEffect,
        Action onStateCompletion)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(chatClient).Build();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());
        var worker = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.RegisterDefaultWorkflow = false;
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                options.MaximumConsecutiveErrorsPerRequest = 0;
            })
            .AddWorkflow<IntegrationDurableTurnWorkflow>();
        var declaration = AIFunctionFactory.Create(
            (string value) => string.Empty,
            "decision_tool",
            "A denied execution-adapter tool.").AsDeclarationOnly();
        worker.AddDurableToolFactory<IntegrationRequestData, IntegrationTurnState>(
            declaration,
            (_, context) =>
            {
                var inner = AIFunctionFactory.Create(
                    (string value) =>
                    {
                        onInner();
                        onEffect();
                        return value;
                    },
                    declaration.Name,
                    declaration.Description);
                return new DurableToolActivation<IntegrationTurnState>
                {
                    Function = new RecordingExecutionAdapter(
                        inner,
                        lifecycle,
                        declaration.Name,
                        context.Metadata.Attempt,
                        Guid.Empty,
                        allowed: false),
                    CompleteState = (_, _) =>
                    {
                        onStateCompletion();
                        return ValueTask.FromResult(DurableStateUpdate<IntegrationTurnState>.Unchanged);
                    },
                };
            },
            options => options.WithMaxAttempts(1));
        return builder.Build();
    }

    private static IHost BuildStateCompletionFailureHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        Action onInvoke,
        Action? onLaterInvoke = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(chatClient).Build();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());

        var worker = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.RegisterDefaultWorkflow = false;
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
            })
            .AddWorkflow<IntegrationDurableTurnWorkflow>();

        var declaration = AIFunctionFactory.Create(
            () => string.Empty,
            "state_failure",
            "Produces an ordinary effect before state completion fails.").AsDeclarationOnly();
        worker.AddDurableToolFactory<IntegrationRequestData, IntegrationTurnState>(
            declaration,
            (_, _) => new DurableToolActivation<IntegrationTurnState>
            {
                Function = AIFunctionFactory.Create(
                    () =>
                    {
                        onInvoke();
                        return "ordinary effect completed";
                    },
                    declaration.Name,
                    declaration.Description),
                CompleteState = (_, _) =>
                    throw new InvalidOperationException("Injected state-completion failure."),
            },
            options => options.WithMaxAttempts(1));

        if (onLaterInvoke is not null)
        {
            var laterDeclaration = AIFunctionFactory.Create(
                () => string.Empty,
                "later_tool",
                "Must not run after a fatal sequential tool failure.").AsDeclarationOnly();
            worker.AddDurableToolFactory<IntegrationRequestData, IntegrationTurnState>(
                laterDeclaration,
                (_, _) => new DurableToolActivation<IntegrationTurnState>
                {
                    Function = AIFunctionFactory.Create(
                        () =>
                        {
                            onLaterInvoke();
                            return "later tool completed";
                        },
                        laterDeclaration.Name,
                        laterDeclaration.Description),
                },
                options => options.WithMaxAttempts(1));
        }

        return builder.Build();
    }

    private static void RegisterTool(
        ITemporalWorkerServiceOptionsBuilder worker,
        string name,
        ConcurrentBag<ToolObservation> observations,
        ConcurrentDictionary<string, byte> externalEffects,
        bool failFirstAttempt,
        AdapterLifecycleRecorder? adapterLifecycle = null)
    {
        var declaration = AIFunctionFactory.Create(
            (string value) => string.Empty,
            name,
            $"Runs {name}.").AsDeclarationOnly();

        worker.AddDurableToolFactory<IntegrationRequestData, IntegrationTurnState>(
            declaration,
            (services, context) =>
            {
                var attemptServices = services.GetRequiredService<AttemptScopedToolServices>();
                AIFunction function = AIFunctionFactory.Create(
                    (string value) =>
                    {
                        observations.Add(new ToolObservation(
                            name,
                            context.Metadata.Attempt,
                            context.Metadata.IdempotencyKey,
                            context.RequestData.OperationId,
                            context.TurnState?.Revision ?? 0,
                            value,
                            attemptServices.InstanceId));
                        externalEffects.TryAdd(context.Metadata.IdempotencyKey, 0);
                        if (failFirstAttempt && context.Metadata.Attempt == 1)
                        {
                            throw new InvalidOperationException("Injected lost completion.");
                        }

                        return $"{name}:{value}:{attemptServices.Client.BaseAddress}";
                    },
                    name,
                    declaration.Description);
                if (adapterLifecycle is not null)
                {
                    function = new RecordingExecutionAdapter(
                        function,
                        adapterLifecycle,
                        name,
                        context.Metadata.Attempt,
                        attemptServices.InstanceId,
                        allowed: true);
                }

                return new DurableToolActivation<IntegrationTurnState>
                {
                    Function = function,
                    CompleteState = (_, _) => ValueTask.FromResult(
                        DurableStateUpdate<IntegrationTurnState>.Replace(
                            new IntegrationTurnState(
                                (context.TurnState?.Revision ?? 0) + 1,
                                [.. context.TurnState?.Receipts ?? [], name]))),
                };
            },
            options => options.WithMaxAttempts(2));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), $"Timed out waiting for {description}.");
    }

    private sealed class AttemptScopeTracker
    {
        public ConcurrentBag<Guid> Created { get; } = [];
        public ConcurrentBag<Guid> Disposed { get; } = [];
    }

    private sealed record AdapterLifecycleEntry(
        string ToolName,
        int Attempt,
        Guid ScopeId,
        string Stage);

    private sealed class AdapterLifecycleRecorder
    {
        private readonly ConcurrentQueue<AdapterLifecycleEntry> _entries = new();

        public IReadOnlyList<AdapterLifecycleEntry> Entries => _entries.ToArray();

        public void Record(string toolName, int attempt, Guid scopeId, string stage) =>
            _entries.Enqueue(new AdapterLifecycleEntry(toolName, attempt, scopeId, stage));
    }

    private sealed class RecordingExecutionAdapter(
        AIFunction innerFunction,
        AdapterLifecycleRecorder lifecycle,
        string toolName,
        int attempt,
        Guid scopeId,
        bool allowed) : DelegatingAIFunction(innerFunction)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            lifecycle.Record(toolName, attempt, scopeId, "before");
            try
            {
                if (!allowed)
                {
                    lifecycle.Record(toolName, attempt, scopeId, "denied");
                    throw new UnauthorizedAccessException("Denied by the authoritative test service.");
                }

                var result = await base.InvokeCoreAsync(arguments, cancellationToken);
                lifecycle.Record(toolName, attempt, scopeId, "success");
                return result;
            }
            catch
            {
                lifecycle.Record(toolName, attempt, scopeId, "error");
                throw;
            }
            finally
            {
                lifecycle.Record(toolName, attempt, scopeId, "finally");
            }
        }
    }

    private sealed class AttemptScopedToolServices : IDisposable
    {
        private readonly AttemptScopeTracker _tracker;

        public AttemptScopedToolServices(
            IHttpClientFactory httpClientFactory,
            AttemptScopeTracker tracker)
        {
            _tracker = tracker;
            Client = httpClientFactory.CreateClient("durable-tool-attempt");
            Client.BaseAddress = new Uri("https://activity-attempt.invalid/");
            InstanceId = Guid.NewGuid();
            _tracker.Created.Add(InstanceId);
        }

        public Guid InstanceId { get; }
        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _tracker.Disposed.Add(InstanceId);
        }
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

    private sealed record ToolObservation(
        string ToolName,
        int Attempt,
        string IdempotencyKey,
        string OperationId,
        int ObservedRevision,
        string Value,
        Guid ScopeId);

    private sealed record InterceptorOutcome(
        IReadOnlyList<ToolObservation> Observations,
        string ModelInput);

    private sealed class DelegateInterceptor(
        Func<DurableToolContext, CancellationToken, Task<DurableToolDecision>> handler)
        : IDurableToolInterceptor<DurableToolContext>
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            DurableToolContext context,
            CancellationToken cancellationToken) =>
            handler(context, cancellationToken);
    }
}

public sealed record IntegrationRequestData(string OperationId, string SubjectId);

public sealed record IntegrationTurnState(int Revision, IReadOnlyList<string> Receipts);

[Workflow("TemporalCommunity.Extensions.AI.Tests.IntegrationDurableTurnWorkflow")]
public sealed class IntegrationDurableTurnWorkflow
    : DurableToolWorkflowBase<IntegrationRequestData, IntegrationTurnState>
{
    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdate("Turn")]
    public Task<DurableTurnResult<IntegrationTurnState>> TurnAsync(
        DurableTurnRequest<IntegrationRequestData, IntegrationTurnState> request) =>
        RunDurableTurnAsync(request);
}

[Workflow("TemporalCommunity.Extensions.AI.Tests.NarrowingDurableTurnWorkflow")]
public sealed class NarrowingDurableTurnWorkflow
    : DurableToolWorkflowBase<IntegrationRequestData, IntegrationTurnState>
{
    protected override IReadOnlyList<string>? DurableToolsetBaselineIds => ["first", "second"];

    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdate("Turn")]
    public Task<DurableTurnResult<IntegrationTurnState>> TurnAsync(
        DurableTurnRequest<IntegrationRequestData, IntegrationTurnState> request) =>
        RunDurableTurnAsync(request);
}
