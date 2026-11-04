using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI;
using Temporalio.Extensions.Hosting;

namespace DocSnippets.MEAI;

internal static class Operations
{
    internal static void Register(
        IServiceCollection services, IChatClient innerClient, AIFunction lookupTool)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/operations.md#register-workers
        services.AddTemporalClient("localhost:7233", "default");
        services.AddChatClient(innerClient);

        services.AddHostedTemporalWorker("chat-sessions")
            .AddDurableAI(options => options.ModelTaskQueue = "chat-model")
            .AddDurableTool(lookupTool, options =>
            {
                options.TaskQueue = "chat-tools";
                options.Idempotent().WithMaxAttempts(2);
            });

        services.AddHostedTemporalWorker("chat-model")
            .ConfigureOptions(options => options.MaxConcurrentActivities = 8)
            .AddDurableAIWorker(options => options.RegisterDefaultWorkflow = false);

        services.AddHostedTemporalWorker("chat-tools")
            .ConfigureOptions(options => options.MaxConcurrentActivities = 16)
            .AddDurableAIWorker(options => options.RegisterDefaultWorkflow = false);
        // END SNIPPET docs/how-to/MEAI/operations.md#register-workers
    }

    internal static void RegisterStarter(IServiceCollection clientServices)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/operations.md#freeze-routing-at-session-creation
        clientServices.AddTemporalClient("localhost:7233", "default");
        clientServices.AddDurableAIClient("chat-sessions", options =>
        {
            options.ModelTaskQueue = "chat-model";
        });
        // END SNIPPET docs/how-to/MEAI/operations.md#freeze-routing-at-session-creation
    }
}
