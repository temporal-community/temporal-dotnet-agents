using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.State;

namespace TemporalCommunity.Extensions.Agents.Internal;

/// <summary>
/// Verifies that the DI Temporal client preserves MAF-specific durable session entries before
/// the agent worker begins dispatching work.
/// </summary>
/// <remarks>
/// This is behavioral rather than an instance check, permitting application-owned codecs and
/// converter composition while rejecting a client that would erase agent entry fields on replay.
/// </remarks>
internal sealed class TemporalAgentDataConverterValidator
    : IPostConfigureOptions<TemporalWorkerServiceOptions>
{
    private readonly IServiceProvider serviceProvider;

    public TemporalAgentDataConverterValidator(IServiceProvider serviceProvider) =>
        this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <inheritdoc/>
    public void PostConfigure(string? name, TemporalWorkerServiceOptions options)
    {
        var converter = serviceProvider.GetRequiredService<ITemporalClient>().Options.DataConverter;
        DurableSessionEntry entry = new AgentSessionRequest
        {
            CorrelationId = "agent-converter-validation",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Messages = [],
            OrchestrationId = "durable-agent",
        };

        try
        {
            var payload = converter.PayloadConverter.ToPayload(entry);
            var roundTripped = converter.PayloadConverter.ToValue(payload, typeof(DurableSessionEntry));

            if (roundTripped is AgentSessionRequest agentRequest &&
                agentRequest.OrchestrationId == "durable-agent")
            {
                return;
            }
        }
        catch (Exception exception)
        {
            throw CreateException(exception);
        }

        throw CreateException();
    }

    private static DurableConfigurationException CreateException(Exception? innerException = null) =>
        new(
            "The ITemporalClient registered in DI uses a DataConverter that cannot preserve " +
            "Temporal agent session entries. Configure the client with " +
            "TemporalAgentDataConverter.Instance before calling AddTemporalAgents().",
            innerException);
}
