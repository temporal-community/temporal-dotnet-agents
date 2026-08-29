using ExtensibleDurableTurns;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

const string taskQueue = "extensible-durable-turns";
var builder = Host.CreateApplicationBuilder(args);

var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions("localhost:7233")
{
    Namespace = "default",
    // This client is created manually, so DI option configurators cannot apply the converter.
    DataConverter = DurableAIDataConverter.Instance,
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);
builder.Services.AddSingleton<IChatClient>(new ScriptedChatClient());
builder.Services.AddHttpClient("processing-attempt", client =>
    client.BaseAddress = new Uri("https://activity-attempt.invalid/"));
builder.Services.AddScoped<IAuthoritativeAuthorizationService, AuthoritativeAuthorizationService>();
builder.Services.AddScoped<ProcessingAttemptServices>();
builder.Services.AddSingleton<IdempotentExternalSink>();
builder.Services.AddSingleton<ExecutionAdapterAudit>();

var worker = builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddWorkflow<SharedWorkerStatusWorkflow>()
    .AddDurableAI(options =>
    {
        options.RegisterDefaultWorkflow = false;
        options.MaximumConsecutiveErrorsPerRequest = 0;
    })
    .AddWorkflow<ContextualTurnWorkflow>();

var firstDeclaration = AIFunctionFactory.Create(
    (string value) => string.Empty,
    "apply_first",
    "Applies the first value.").AsDeclarationOnly();
var secondDeclaration = AIFunctionFactory.Create(
    (string value) => string.Empty,
    "apply_second",
    "Applies the second value.").AsDeclarationOnly();

// The custom workflow's protected baseline selects these two worker-owned groups once. The
// declaration and receiver activator are built once; activity attempts still get fresh scopes.
worker.AddDurableToolset("reference", tools => tools.AddDurableToolFactory<ReferenceTools>(
    nameof(ReferenceTools.ReadReference),
    new AIFunctionFactoryOptions
    {
        Name = "read_reference",
        Description = "Reads a reference value.",
    }));
worker.AddDurableToolset("processing", tools =>
{
    RegisterStatefulTool(tools, firstDeclaration, "first", requireApproval: true);
    RegisterStatefulTool(tools, secondDeclaration, "second", requireApproval: false);
});

var host = builder.Build();
await host.StartAsync();

var statusHandle = await temporalClient.StartWorkflowAsync(
    (SharedWorkerStatusWorkflow workflow) => workflow.RunAsync(),
    new WorkflowOptions($"shared-worker-status-{Guid.NewGuid():N}", taskQueue));
var status = await statusHandle.GetResultAsync<SharedWorkerStatus>();
Console.WriteLine(
    $"Shared worker status: {status.Setup.Message}; " +
    $"{status.Inventory.Categories} categories, {status.Inventory.Products} products ({status.Status})");

var startInput = host.Services.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();
var workflowId = $"extensible-turn-{Guid.NewGuid():N}";
var handle = await temporalClient.StartWorkflowAsync(
    (ContextualTurnWorkflow workflow) => workflow.RunAsync(startInput),
    new WorkflowOptions(workflowId, taskQueue));

var request = new DurableTurnRequest<ProcessingRequest, ProcessingState>
{
    Messages = [new ChatMessage(ChatRole.User, "Run the sample processing turn.")],
    RequestData = new ProcessingRequest("business-operation-1", "trusted-user", "resource-7"),
    // Deliberately forged-looking flag: AuthorizingFunction ignores it and consults the
    // authoritative service before each stateful effect.
    InitialTurnState = new ProcessingState(0, ClaimedAuthorized: true, Receipts: []),
};

var turnTask = handle.ExecuteUpdateAsync<DurableTurnResult<ProcessingState>>(
    "Turn",
    [request],
    new WorkflowUpdateOptions { Id = request.RequestData.OperationId });

DurableApprovalRequest? pendingApproval = null;
while (!turnTask.IsCompleted && pendingApproval is null)
{
    await Task.Delay(100);
    pendingApproval = await handle.QueryAsync<ContextualTurnWorkflow, DurableApprovalRequest?>(
        workflow => workflow.GetPendingApproval());
}

if (pendingApproval is not null)
{
    Console.WriteLine($"Approving {pendingApproval.FunctionName} ({pendingApproval.RequestId})");
    await handle.ExecuteUpdateAsync<ContextualTurnWorkflow, DurableApprovalResolutionResult>(
        workflow => workflow.ResolveApprovalAsync(new DurableApprovalDecision
        {
            RequestId = pendingApproval.RequestId,
            Approved = true,
            Reason = "Approved by the sample reviewer.",
        }));
}

var result = await turnTask;

Console.WriteLine(result.Response.Messages.Last().Text);
Console.WriteLine($"Completion: {result.CompletionReason}; revision: {result.FinalTurnState?.Revision}");
if (result.CompletionReason != DurableTurnCompletionReason.FinalResponse)
{
    Console.WriteLine("The non-final turn state is diagnostic only and will not be applied.");
    await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
    await host.StopAsync();
    return;
}

foreach (var receipt in result.FinalTurnState?.Receipts ?? [])
{
    Console.WriteLine($"{receipt.Step}: {receipt.Value} ({receipt.ActivityIdempotencyKey})");
}

var adapterAudit = host.Services.GetRequiredService<ExecutionAdapterAudit>();
Console.WriteLine("Execution adapter lifecycle:");
foreach (var observation in adapterAudit.Entries)
{
    Console.WriteLine(
        $"  {observation.ToolName} attempt {observation.Attempt}, scope {observation.ScopeId}: {observation.Stage}");
}

// A second turn uses a subject denied by the authoritative service. The decorator throws before
// the ordinary function can reach the external sink or state-completion callback.
var deniedHandle = await temporalClient.StartWorkflowAsync(
    (ContextualTurnWorkflow workflow) => workflow.RunAsync(startInput),
    new WorkflowOptions($"extensible-turn-denied-{Guid.NewGuid():N}", taskQueue));
var deniedRequest = new DurableTurnRequest<ProcessingRequest, ProcessingState>
{
    RequestData = request.RequestData with
    {
        OperationId = "business-operation-denied",
        SubjectId = "denied-user",
    },
    Messages = request.Messages,
    InitialTurnState = request.InitialTurnState,
    CorrelationId = request.CorrelationId,
    ConversationId = request.ConversationId,
    ChatOptions = request.ChatOptions,
    // Narrow this turn to processing. The baseline-known read_reference call returned by the
    // scripted model is blocked without scheduling a tool activity.
    Options = new DurableTurnOptions { ToolsetIds = ["processing"] },
};
var deniedTurn = deniedHandle.ExecuteUpdateAsync<DurableTurnResult<ProcessingState>>(
    "Turn",
    [deniedRequest],
    new WorkflowUpdateOptions { Id = deniedRequest.RequestData.OperationId });
pendingApproval = null;
while (!deniedTurn.IsCompleted && pendingApproval is null)
{
    await Task.Delay(100);
    pendingApproval = await deniedHandle.QueryAsync<ContextualTurnWorkflow, DurableApprovalRequest?>(
        workflow => workflow.GetPendingApproval());
}
if (pendingApproval is not null)
{
    await deniedHandle.ExecuteUpdateAsync<ContextualTurnWorkflow, DurableApprovalResolutionResult>(
        workflow => workflow.ResolveApprovalAsync(new DurableApprovalDecision
        {
            RequestId = pendingApproval.RequestId,
            Approved = true,
            Reason = "Approved to demonstrate effect-time authorization denial.",
        }));
}
var deniedTurnFailed = false;
try
{
    await deniedTurn;
}
catch (Exception exception)
{
    deniedTurnFailed = true;
    Console.WriteLine($"Denied turn failed before the ordinary function effect: {exception.GetType().Name}");
}
if (!deniedTurnFailed)
{
    throw new InvalidOperationException("The denied sample turn unexpectedly succeeded.");
}

await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
await deniedHandle.SignalAsync(workflow => workflow.RequestShutdownAsync());
await host.StopAsync();

void RegisterStatefulTool(
    DurableToolsetBuilder toolset,
    AIFunctionDeclaration declaration,
    string step,
    bool requireApproval)
{
    toolset.AddDurableToolFactory<ProcessingRequest, ProcessingState>(
        declaration,
        (services, context) =>
        {
            var attemptServices = services.GetRequiredService<ProcessingAttemptServices>();
            var externalSink = services.GetRequiredService<IdempotentExternalSink>();
            var adapterAudit = services.GetRequiredService<ExecutionAdapterAudit>();
            var inner = AIFunctionFactory.Create(
                (string value) =>
                {
                    var firstWrite = externalSink.Record(context.Metadata.IdempotencyKey);
                    // Simulate a worker failure after an external write. Temporal retries the
                    // activity; the same key makes the second sink write a no-op.
                    if (context.Metadata.Attempt == 1)
                    {
                        throw new InvalidOperationException("Injected post-write activity failure.");
                    }

                    return $"{SampleTools.Apply(context.RequestData, context.TurnState, value)}:" +
                        $"new-external-write={firstWrite}";
                },
                declaration.Name,
                declaration.Description);
            return new DurableToolActivation<ProcessingState>
            {
                Function = new AuthorizingFunction(
                    inner,
                    attemptServices.Authorization,
                    context.RequestData.SubjectId,
                    context.RequestData.ResourceId,
                    declaration.Name,
                    context.Metadata.Attempt,
                    attemptServices.InstanceId,
                    adapterAudit),
                CompleteState = (_, _) => ValueTask.FromResult(
                    DurableStateUpdate<ProcessingState>.Replace(
                        SampleTools.Complete(context, step, step))),
            };
        },
        options =>
        {
            options.WithMaxAttempts(2);
            if (requireApproval)
            {
                options.RequireApproval();
            }
        });
}
