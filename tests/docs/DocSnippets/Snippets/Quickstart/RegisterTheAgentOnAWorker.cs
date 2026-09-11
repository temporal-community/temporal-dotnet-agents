// HARNESS for docs/how-to/MAF/quickstart.md § "1. Register the agent on a worker".
//
// DOC DEFECT (as of this file's commit): the doc registers the tool as
//     agent.AddTool(sp => AIFunctionFactory.Create(..., "get_weather"));
// There is no AddTool(Func<IServiceProvider, AIFunction>) overload. The factory overload takes the
// NAME FIRST: AddTool(string name, Func<IServiceProvider, AIFunction> factory, ...). The snippet
// below is the corrected form, so this harness is green before the doc is fixed; the doc-side fix
// is tracked in README.md next to this project.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.Quickstart;

internal static class RegisterTheAgentOnAWorker
{
    internal static void Configure(HostApplicationBuilder builder, OpenAIClient openAiClient, string model)
    {
        // BEGIN SNIPPET docs/how-to/MAF/quickstart.md#1-register-the-agent-on-a-worker (lines 36-55)
        builder.Services.AddSingleton<WeatherService>();
        builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("Assistant", agent =>
                {
                    agent.Instructions = "You are a helpful assistant.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

                    agent.AddTool("get_weather", sp => AIFunctionFactory.Create(
                        sp.GetRequiredService<WeatherService>().GetWeather,
                        name: "get_weather"));
                });
            });
        // END SNIPPET docs/how-to/MAF/quickstart.md#1-register-the-agent-on-a-worker
    }
}
