using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;

namespace DocSnippets.MEAI;

internal static class PayloadCodecConfiguration
{
    // BEGIN SNIPPET docs/how-to/MEAI/payload-codecs.md#configure-every-client-and-worker
    static async Task ConfigureAsync(
        HostApplicationBuilder builder,
        string temporalAddress,
        string temporalNamespace,
        string taskQueue,
        IPayloadCodec applicationPayloadCodec)
    {
        var converter = DurableAIDataConverter.CreateDataConverter(applicationPayloadCodec);
        var client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions(temporalAddress)
            {
                Namespace = temporalNamespace,
                DataConverter = converter,
            });
        builder.Services.AddSingleton<ITemporalClient>(client);

        var worker = builder.Services.AddHostedTemporalWorker(
            temporalAddress,
            temporalNamespace,
            taskQueue);
        worker.ConfigureOptions(
            options => options.ClientOptions!.DataConverter = converter);
    }
    // END SNIPPET docs/how-to/MEAI/payload-codecs.md#configure-every-client-and-worker
}
