using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScheduledChat;
using TemporalCommunity.Extensions.AI;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Extensions.Hosting;
using Temporalio.Exceptions;

var builder = Host.CreateApplicationBuilder(args);
var address = builder.Configuration["TEMPORAL_ADDRESS"] ?? "localhost:7233";
var client = await TemporalClient.ConnectAsync(new(address)
{
    DataConverter = DurableAIDataConverter.Instance,
});
var suffix = Guid.NewGuid().ToString("N");
var queue = $"scheduled-chat-{suffix}";
var scheduleId = $"scheduled-chat-{suffix}";
var conversationId = $"scheduled-session-{suffix}";
builder.Services.AddSingleton<ITemporalClient>(client);
// Local, deterministic demo model: no provider credentials or inference charges.
builder.Services.AddSingleton<IChatClient, DemoChatClient>();
builder.Services.AddHostedTemporalWorker(queue)
    .AddWorkflow<ScheduledTurnWorkflow>()
    .AddSingletonActivities<ScheduledTurnActivities>()
    .AddDurableAI(options => options.SessionTimeToLive = TimeSpan.FromMinutes(10));
using var host = builder.Build();
await host.StartAsync();
var sessions = host.Services.GetRequiredService<DurableChatSessionClient>();
ScheduleHandle? schedule = null;
try
{
    schedule = await client.CreateScheduleAsync(scheduleId, new Schedule(
        ScheduleActionStartWorkflow.Create(
            (ScheduledTurnWorkflow workflow) => workflow.RunAsync(
                new ScheduledTurnInput(conversationId, "Report the service status.")),
            new WorkflowOptions($"trigger-{scheduleId}", queue)),
        new ScheduleSpec { Intervals = [new ScheduleIntervalSpec(TimeSpan.FromHours(1))] })
    {
        Policy = new SchedulePolicy
        {
            Overlap = ScheduleOverlapPolicy.Skip,
            CatchupWindow = TimeSpan.FromMinutes(10),
            PauseOnFailure = true,
        },
        // Keep the demo bounded. Backfill works while paused; production can unpause it.
        State = new ScheduleState { Paused = true },
    });

    var now = DateTime.UtcNow;
    var end = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
    await schedule.BackfillAsync(
        [new ScheduleBackfill(end.AddHours(-2).AddSeconds(1), end, ScheduleOverlapPolicy.BufferAll)]);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    ScheduleDescription description;
    do
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
        description = await schedule.DescribeAsync(
            new RpcOptions { CancellationToken = deadline.Token });
    } while (description.Info.NumActions < 2 || description.Info.RunningActions.Count > 0);

    foreach (var action in description.Info.RecentActions)
    {
        var started = (ScheduleActionExecutionStartWorkflow)action.Action;
        var receipt = await client.GetWorkflowHandle<ScheduledTurnWorkflow, ScheduledTurnReceipt>(
            started.WorkflowId, started.FirstExecutionRunId).GetResultAsync(
                rpcOptions: new RpcOptions { CancellationToken = deadline.Token });
        Console.WriteLine($"Occurrence {receipt.RequestId}: {receipt.Text}");
    }
    var history = await sessions.GetHistoryAsync(conversationId);
    Console.WriteLine($"Scheduled turns retained: {history.Count / 2}");
}
finally
{
    try
    {
        if (schedule is not null)
            await schedule.DeleteAsync();
        try
        {
            await sessions.ShutdownAsync(conversationId);
        }
        catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
        {
            Console.Error.WriteLine("No running demo session remains to shut down.");
        }
    }
    finally
    {
        await host.StopAsync();
    }
}

internal sealed class DemoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The demo uses streaming inference.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return new(ChatRole.Assistant, "All services are operational.")
        {
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
