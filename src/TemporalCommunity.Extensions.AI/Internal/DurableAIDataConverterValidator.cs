using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Verifies that the DI Temporal client preserves the durable AI wire contracts before the
/// worker begins dispatching work.
/// </summary>
/// <remarks>
/// The check is behavioral instead of requiring <see cref="DurableAIDataConverter.Instance"/>,
/// so application-owned converter and payload-codec composition remains supported. The three
/// probes cover the durable roots that require library JSON support: polymorphic
/// <see cref="ChatMessage"/> content, polymorphic <see cref="DurableSessionEntry"/> history,
/// and the <see cref="GeneratedEmbeddings{TEmbedding}"/> wrapper properties used by the
/// embedding activity. Other durable AI DTOs are composed from primitives, collections, and
/// these roots.
/// </remarks>
internal sealed class DurableAIDataConverterValidator : IPostConfigureOptions<TemporalWorkerServiceOptions>
{
    private static readonly float[] ValidatorVector = [1.0f];

    private readonly IServiceProvider serviceProvider;

    public DurableAIDataConverterValidator(IServiceProvider serviceProvider) =>
        this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <inheritdoc/>
    public void PostConfigure(string? name, TemporalWorkerServiceOptions options)
    {
        // The three-argument AddHostedTemporalWorker overload owns its client and does not put an
        // ITemporalClient in DI. That path is protected by DurableAIWorkerClientConfigurator;
        // this validator is specifically for the manually registered DI-client path.
        var client = serviceProvider.GetService<ITemporalClient>();
        if (client is null)
        {
            return;
        }

        var converter = client.Options.DataConverter;

        try
        {
            ValidatePolymorphicChatContent(converter);
            ValidateSessionEntry(converter);
            ValidateEmbeddingWrapper(converter);
        }
        catch (DurableConfigurationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateException(exception);
        }
    }

    private static void ValidatePolymorphicChatContent(Temporalio.Converters.DataConverter converter)
    {
        var message = new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent("converter-validation-call", "converter_validation"),
                new FunctionResultContent("converter-validation-call", "ok"),
            ]);

        var payload = converter.PayloadConverter.ToPayload(message);
        var roundTripped = converter.PayloadConverter.ToValue(payload, typeof(ChatMessage)) as ChatMessage;

        if (roundTripped?.Contents[0] is not FunctionCallContent ||
            roundTripped.Contents[1] is not FunctionResultContent)
        {
            throw CreateException();
        }
    }

    private static void ValidateSessionEntry(Temporalio.Converters.DataConverter converter)
    {
        DurableSessionEntry entry = new DurableSessionRequest
        {
            CorrelationId = "converter-validation-session",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Messages = [new ChatMessage(ChatRole.User, "validation")],
        };

        var payload = converter.PayloadConverter.ToPayload(entry);
        var roundTripped = converter.PayloadConverter.ToValue(payload, typeof(DurableSessionEntry));

        if (roundTripped is not DurableSessionRequest request ||
            request.CorrelationId != entry.CorrelationId)
        {
            throw CreateException();
        }
    }

    private static void ValidateEmbeddingWrapper(Temporalio.Converters.DataConverter converter)
    {
        var output = new DurableEmbeddingOutput
        {
            Embeddings = new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(ValidatorVector)])
            {
                Usage = new UsageDetails { InputTokenCount = 1, TotalTokenCount = 1 },
                AdditionalProperties = new AdditionalPropertiesDictionary { ["validator"] = "present" },
            },
        };

        var payload = converter.PayloadConverter.ToPayload(output);
        var roundTripped = converter.PayloadConverter.ToValue(payload, typeof(DurableEmbeddingOutput))
            as DurableEmbeddingOutput;

        if (roundTripped?.Embeddings.Usage?.InputTokenCount != 1 ||
            roundTripped.Embeddings.AdditionalProperties?.ContainsKey("validator") != true)
        {
            throw CreateException();
        }
    }

    private static DurableConfigurationException CreateException(Exception? innerException = null) =>
        new(
            "The ITemporalClient registered in DI uses a DataConverter that cannot preserve durable AI " +
            "workflow data. Configure the client with DurableAIDataConverter.Instance, or compose its " +
            "PayloadConverter with DurableAIDataConverter.Instance.PayloadConverter before calling " +
            "AddDurableAI().",
            innerException);
}
