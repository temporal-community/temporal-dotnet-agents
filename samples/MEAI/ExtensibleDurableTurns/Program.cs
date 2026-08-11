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

var worker = builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddDurableAI(options => options.RegisterDefaultWorkflow = false)
    .AddWorkflow<ContextualTurnWorkflow>();

// Ordinary functions remain the default and require no Temporal-specific signature.
worker.AddDurableTools(AIFunctionFactory.Create(
    SampleTools.ReadReference,
    "read_reference",
    "Reads a reference value."));

var firstDeclaration = AIFunctionFactory.Create(
    (string value) => string.Empty,
    "apply_first",
    "Applies the first value.").AsDeclarationOnly();
var secondDeclaration = AIFunctionFactory.Create(
    (string value) => string.Empty,
    "apply_second",
    "Applies the second value.").AsDeclarationOnly();

RegisterStatefulTool(worker, firstDeclaration, "first", requireApproval: true);
RegisterStatefulTool(worker, secondDeclaration, "second", requireApproval: false);

var host = builder.Build();
await host.StartAsync();

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
if (result.CompletionReason == DurableTurnCompletionReason.IterationLimitReached)
{
    Console.WriteLine("The capped turn state is diagnostic only and will not be applied.");
    await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
    await host.StopAsync();
    return;
}

foreach (var receipt in result.FinalTurnState?.Receipts ?? [])
{
    Console.WriteLine($"{receipt.Step}: {receipt.Value} ({receipt.ActivityIdempotencyKey})");
}

await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
await host.StopAsync();

void RegisterStatefulTool(
    ITemporalWorkerServiceOptionsBuilder workerBuilder,
    AIFunctionDeclaration declaration,
    string step,
    bool requireApproval)
{
    workerBuilder.AddDurableTool<ProcessingRequest, ProcessingState>(
        declaration,
        (services, context) =>
        {
            var attemptServices = services.GetRequiredService<ProcessingAttemptServices>();
            var externalSink = services.GetRequiredService<IdempotentExternalSink>();
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
                    context.RequestData.ResourceId),
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
