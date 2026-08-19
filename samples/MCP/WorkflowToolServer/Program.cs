using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Samples.Mcp.WorkflowToolServer;

var builder = WebApplication.CreateBuilder(args);
var temporalAddress = builder.Configuration["TEMPORAL_ADDRESS"] ?? "localhost:7233";
var temporalNamespace = builder.Configuration["TEMPORAL_NAMESPACE"] ?? "default";

var temporalClient = await TemporalClient.ConnectAsync(new(temporalAddress)
{
    Namespace = temporalNamespace,
});

builder.Services.AddWorkflowToolServer(temporalClient);
builder.Services
    .AddHostedTemporalWorker(WorkflowToolServerConstants.TaskQueue)
    .AddWorkflow<WorkflowOperationWorkflow>();

var app = builder.Build();
app.MapWorkflowToolServer();

await app.RunAsync();
