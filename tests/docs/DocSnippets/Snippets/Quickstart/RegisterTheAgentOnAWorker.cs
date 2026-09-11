// HARNESS for docs/how-to/MAF/quickstart.md § "1. Register the agent on a worker".
//
// The doc block is a top-level-statements program; the only edits below are the ones a library
// harness forces — `args` becomes a parameter and the body sits in a method. Everything the
// compiler actually judges (overload binding, factory shapes, host wiring) is unchanged.
//
// The second method is NOT part of the snippet. The doc's third "bites people" bullet claims a
// specific name-first call compiles; ProseDiFactoryForm proves that claim rather than asserting it.
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.Quickstart;

internal static class RegisterTheAgentOnAWorker
{
    internal static async Task ConfigureAsync(string[] args)
    {
        // BEGIN SNIPPET docs/how-to/MAF/quickstart.md#1-register-the-agent-on-a-worker (lines 40-79)
        var builder = Host.CreateApplicationBuilder(args);

        var apiKey = builder.Configuration["OPENAI_API_KEY"]
            ?? throw new InvalidOperationException("OPENAI_API_KEY is not configured.");
        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey));

        // The tool the agent may call. Each invocation the model requests becomes its own
        // InvokeAgentTool activity.
        static string GetWeather(string city) => $"It is sunny in {city}.";
        var weatherTool = AIFunctionFactory.Create(
            GetWeather,
            name: "get_weather",
            description: "Returns the current weather for a city.");

        builder.Services.AddChatClient(openAiClient.GetChatClient("gpt-4o-mini").AsIChatClient());
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("Assistant", agent =>
                {
                    agent.Instructions = "You are a helpful assistant.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(weatherTool);
                });
            });

        var host = builder.Build();
        await host.StartAsync();
        // END SNIPPET docs/how-to/MAF/quickstart.md#1-register-the-agent-on-a-worker
    }

    // Pins the inline example in the doc's "a tool that needs a service from DI" bullet.
    internal static void ProseDiFactoryForm(DurableAgentBuilder agent) =>
        agent.AddTool("get_weather", sp =>
            AIFunctionFactory.Create(sp.GetRequiredService<WeatherService>().GetWeather, name:
                "get_weather"));
}
