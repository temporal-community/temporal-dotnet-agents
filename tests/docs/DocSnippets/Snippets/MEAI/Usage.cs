// Application-local configuration and services are supplied outside the verbatim markers.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;

namespace DocSnippets.MEAI;

internal static class Usage
{
    internal static void Register(HostApplicationBuilder builder, IChatClient innerChatClient, Weather weather)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#built-in-managed-session
        builder.Services.AddTemporalClient("localhost:7233", "default");
        builder.Services.AddChatClient(innerChatClient);

        var weatherTool = AIFunctionFactory.Create(
            (string city) => weather.GetCurrent(city),
            name: "get_weather",
            description: "Gets current weather for a city.");

        builder.Services
            .AddHostedTemporalWorker("durable-chat")
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromMinutes(5);
                options.SessionTimeToLive = TimeSpan.FromHours(24);
                options.MaxToolCallsPerTurn = 10;
            })
            .AddDurableTool(weatherTool, tool => tool.WithTimeout(TimeSpan.FromSeconds(30)));
        // END SNIPPET docs/how-to/MEAI/usage.md#built-in-managed-session
    }

    internal static async Task ManualClientAsync(HostApplicationBuilder builder, IChatClient innerChatClient)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#worker-setup-with-a-manual-client
        var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions("localhost:7233")
        {
            DataConverter = DurableAIDataConverter.Instance,
        });

        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddChatClient(innerChatClient);

        builder.Services
            .AddHostedTemporalWorker("durable-chat")
            .AddDurableAI(options => options.SessionTimeToLive = TimeSpan.FromHours(24));
        // END SNIPPET docs/how-to/MEAI/usage.md#worker-setup-with-a-manual-client
    }

    internal static async Task SharedWorkerClientAsync()
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#sharing-a-worker-with-non-ai-workflows
        var options = ClientEnvConfig.LoadClientConnectOptions();
        options.DataConverter = DurableAIDataConverter.Instance;
        var client = await TemporalClient.ConnectAsync(options);
        // END SNIPPET docs/how-to/MEAI/usage.md#sharing-a-worker-with-non-ai-workflows
    }

    internal static async Task SendAsync(IServiceProvider services)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#send-a-turn-and-read-history
        var sessionClient = services.GetRequiredService<DurableChatSessionClient>();

        var response = await sessionClient.SendAsync(
            "customer-42",
            [new ChatMessage(ChatRole.User, "What is the weather in Seattle?")]);

        Console.WriteLine(response.Text);

        var history = await sessionClient.GetHistoryAsync("customer-42");
        // END SNIPPET docs/how-to/MEAI/usage.md#send-a-turn-and-read-history
    }
}

// BEGIN SNIPPET docs/how-to/MEAI/usage.md#durable-tool-calls-with-aifunctionasdurable
[Workflow]
public sealed class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(ResearchRequest request)
    {
        // Look up a fact with a durable tool call — dispatches to DurableFunctionActivities and
        // resolves "get_current_weather" from the worker's AddDurableTool/AddDurableTools registry.
        // AsDurable() always runs on this workflow's own task queue, so its worker must be the
        // same one registering the tool.
        var weatherTool = AIFunctionFactory.Create(
            (string city) => "unreachable stub — only invoked outside a workflow",
            name: "get_current_weather",
            description: "Returns the current weather conditions for a given city.")
            .AsDurable();

        var weather = await weatherTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = request.City }));

        // Feed the tool result into a durable LLM call via a hand-written Activity —
        // ResearchActivities.SummarizeWeatherAsync is constructor-injected with the real,
        // worker-side IChatClient and dispatched as a standard Temporal activity.
        var activityOptions = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2) };
        var weatherText = weather?.ToString() ?? string.Empty;
        return await Workflow.ExecuteActivityAsync(
            (ResearchActivities a) => a.SummarizeWeatherAsync(request.City, weatherText),
            activityOptions);
    }
}
// END SNIPPET docs/how-to/MEAI/usage.md#durable-tool-calls-with-aifunctionasdurable

internal sealed class Weather
{
    public string GetCurrent(string city) => $"Weather for {city}";
}

internal static class ClientEnvConfig
{
    public static TemporalClientConnectOptions LoadClientConnectOptions() => new("localhost:7233");
}

public sealed record ResearchRequest(string City);

public class ResearchActivities(IChatClient chatClient)
{
    [Activity]
    public async Task<string> SummarizeWeatherAsync(string city, string weather)
    {
        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, $"Summarize weather for {city}: {weather}")],
            cancellationToken: ActivityExecutionContext.Current.CancellationToken);
        return response.Text;
    }
}
