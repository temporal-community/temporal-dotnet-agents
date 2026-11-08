// HARNESS for docs/how-to/MAF/usage.md § "MAF-options worker example".
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.Usage;

internal static class MafOptionsWorkerExample
{
    internal static void Configure(
        HostApplicationBuilder builder,
        OpenAIClient openAiClient,
        string model,
        string taskQueue)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#maf-options-worker-example
        static string GetWeather(string city, IServiceProvider services) =>
            services.GetRequiredService<WeatherService>().GetForecast(city);

        var weatherTool = AIFunctionFactory.Create(
            GetWeather,
            name: "get_weather",
            description: "Returns a forecast for a city.");

        builder.Services.AddScoped<WeatherService>();
        builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(agents =>
                agents.Agent(
                    "WeatherAgent",
                    new ChatClientAgentOptions
                    {
                        Description = "Answers weather questions.",
                        ChatOptions = new ChatOptions
                        {
                            Instructions = "Use get_weather for forecasts.",
                            Tools = [weatherTool],
                        },
                    },
                    temporal => temporal.TimeToLive = TimeSpan.FromHours(24)));
        // END SNIPPET docs/how-to/MAF/usage.md#maf-options-worker-example
    }
}
