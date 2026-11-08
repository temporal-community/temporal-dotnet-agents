// HARNESS for docs/how-to/MAF/quickstart.md § "1. Register the agent on a worker".
//
// The doc block is a top-level-statements program; only `args` becomes a method parameter and the
// body is wrapped in a method. The WeatherService implementation is supplied by HarnessServices.
using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.Quickstart;

internal static class RegisterTheAgentOnAWorker
{
    internal static async Task ConfigureAsync(string[] args)
    {
        // BEGIN SNIPPET docs/how-to/MAF/quickstart.md#1-register-the-agent-on-a-worker
        var builder = Host.CreateApplicationBuilder(args);

        var apiKey = builder.Configuration["OPENAI_API_KEY"]
            ?? throw new InvalidOperationException("OPENAI_API_KEY is not configured.");
        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey));

        // Each model-requested invocation becomes its own durable tool activity. Resolve scoped
        // dependencies through the IServiceProvider parameter rather than capturing them here.
        static string GetWeather(string city, IServiceProvider services) =>
            services.GetRequiredService<WeatherService>().GetForecast(city);
        var weatherTool = AIFunctionFactory.Create(
            GetWeather,
            name: "get_weather",
            description: "Returns the current weather for a city.");

        builder.Services.AddChatClient(openAiClient.GetChatClient("gpt-4o-mini").AsIChatClient());
        builder.Services.AddScoped<WeatherService>();
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(agents =>
                agents.Agent(
                    "Assistant",
                    new ChatClientAgentOptions
                    {
                        Description = "A helpful weather assistant.",
                        ChatOptions = new ChatOptions
                        {
                            Instructions = "You are a helpful assistant. Use get_weather for forecasts.",
                            Tools = [weatherTool],
                        },
                    },
                    temporal => temporal.TimeToLive = TimeSpan.FromHours(1)));

        var host = builder.Build();
        await host.StartAsync();
        // END SNIPPET docs/how-to/MAF/quickstart.md#1-register-the-agent-on-a-worker
    }
}
