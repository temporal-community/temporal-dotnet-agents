using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Extensions.Hosting;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Checks that the stock managed workflow's chat-client routes are registered.
/// </summary>
internal sealed class DurableAIRequiredServicesValidator(
    IServiceProvider serviceProvider,
    DurableExecutionOptions options)
    : IPostConfigureOptions<TemporalWorkerServiceOptions>
{
    public void PostConfigure(string? name, TemporalWorkerServiceOptions workerOptions)
    {
        if (!options.RegisterDefaultWorkflow)
        {
            return;
        }

        var clientKey = options.DefaultChatClientKey;
        var isRegistered = clientKey is null
            ? serviceProvider.GetService<IServiceProviderIsService>()?
                .IsService(typeof(IChatClient)) == true
            : serviceProvider.GetService<IServiceProviderIsKeyedService>()?
                .IsKeyedService(typeof(IChatClient), clientKey) == true;

        if (!isRegistered)
        {
            var route = clientKey is null
                ? "the unkeyed IChatClient"
                : $"the keyed IChatClient profile '{clientKey}'";
            throw new DurableConfigurationException(
                $"The default durable chat workflow requires {route} to be registered in worker DI. " +
                "Register it before starting the worker. Per-request chat-client keys are resolved " +
                "when their activities run and cannot be validated until supplied.");
        }

        var summarizerClientKey = options.DefaultHistorySummarizerClientKey;
        if (summarizerClientKey is not null
            && serviceProvider.GetService<IServiceProviderIsKeyedService>()?
                .IsKeyedService(typeof(IChatClient), summarizerClientKey) != true)
        {
            throw new DurableConfigurationException(
                $"The configured history summarizer requires the keyed IChatClient profile " +
                $"'{summarizerClientKey}' to be registered in worker DI. Register it before starting the worker.");
        }
    }
}
