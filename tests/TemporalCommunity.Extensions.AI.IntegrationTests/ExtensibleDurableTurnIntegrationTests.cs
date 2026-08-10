using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.Approvals;
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
        var taskQueue = $"extensible-turn-{Guid.NewGuid():N}";

        using var host = BuildHost(
            env.Client,
            taskQueue,
            chatClient,
            observations,
            externalEffects);
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

        var second = Assert.Single(observations, observation => observation.ToolName == "apply_second");
        Assert.Equal(1, second.ObservedRevision);
        Assert.Equal(2, externalEffects.Count);

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

    private static DurableTurnRequest<IntegrationRequestData, IntegrationTurnState> CreateRequest() =>
        new()
        {
            Messages = [new ChatMessage(ChatRole.User, "Run the decision tool.")],
            RequestData = new IntegrationRequestData("operation-decision", "trusted-subject"),
            InitialTurnState = new IntegrationTurnState(0, []),
        };

    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        ConcurrentBag<ToolObservation> observations,
        ConcurrentDictionary<string, byte> externalEffects)
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

        RegisterTool(worker, "apply_first", observations, externalEffects, failFirstAttempt: true);
        RegisterTool(worker, "apply_second", observations, externalEffects, failFirstAttempt: false);
        return builder.Build();
    }

    private static void RegisterTool(
        ITemporalWorkerServiceOptionsBuilder worker,
        string name,
        ConcurrentBag<ToolObservation> observations,
        ConcurrentDictionary<string, byte> externalEffects,
        bool failFirstAttempt)
    {
        var declaration = AIFunctionFactory.Create(
            (string value) => string.Empty,
            name,
            $"Runs {name}.").AsDeclarationOnly();

        worker.AddDurableTool<IntegrationRequestData, IntegrationTurnState>(
            declaration,
            context => new DurableToolActivation<IntegrationTurnState>
            {
                Function = AIFunctionFactory.Create(
                    (string value) =>
                    {
                        observations.Add(new ToolObservation(
                            name,
                            context.Metadata.Attempt,
                            context.Metadata.IdempotencyKey,
                            context.RequestData.OperationId,
                            context.TurnState?.Revision ?? 0,
                            value));
                        externalEffects.TryAdd(context.Metadata.IdempotencyKey, 0);
                        if (failFirstAttempt && context.Metadata.Attempt == 1)
                        {
                            throw new InvalidOperationException("Injected lost completion.");
                        }

                        return $"{name}:{value}";
                    },
                    name,
                    declaration.Description),
                CompleteState = (_, _) => ValueTask.FromResult(
                    DurableStateUpdate<IntegrationTurnState>.Replace(
                        new IntegrationTurnState(
                            (context.TurnState?.Revision ?? 0) + 1,
                            [.. context.TurnState?.Receipts ?? [], name]))),
            },
            options => options.WithMaxAttempts(2));
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
        string Value);

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
