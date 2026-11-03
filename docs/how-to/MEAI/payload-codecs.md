# Durable AI payload codecs

Durable model loops can store repeated tool declarations, growing message history, tool arguments,
and Continue-as-New state. `DurableAIGzipPayloadCodec` is an opt-in Temporal `IPayloadCodec` that
compresses the complete serialized `Payload`, including its original metadata.

```csharp
var codec = new DurableAIGzipPayloadCodec(new DurableAIGzipPayloadCodecOptions
{
    CompressionLevel = CompressionLevel.Fastest,
    MinimumPayloadSizeBytes = 1024,
    MaximumEncodedPayloadSizeBytes = 2 * 1024 * 1024,
    MaximumDecodedPayloadSizeBytes = 4 * 1024 * 1024,
    MinimumCompressionSavingsRatio = 0.05,
});

var dataConverter = DurableAIDataConverter.CreateDataConverter(codec);
```

## Configure every client and worker

The client that starts or updates the workflow and the worker that handles it must use compatible
converters. Configure both explicitly when using a custom codec; `AddDurableAI` only supplies the
default MEAI converter when the worker converter is still the SDK default. In split deployments,
use the same codec composition, key material, version, and size limits in every process:

```csharp
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
```

In split processes, create a compatible converter in each process rather than sharing an in-memory
instance. Compression or encryption keys and codec ordering must match. This example configures the
worker's owned Temporal client separately from the application client; if the host intentionally
shares one `ITemporalClient`, configure that client's converter before the worker starts.

The codec passes through
small payloads and payloads whose compressed representation does not meet the requested savings.
Decode rejects unsupported versions, corrupt data, and payloads that cross either configured bound.

Managed durable chat also checks the configured payload converter's complete serialized payload
against `DurableExecutionOptions.MaximumTransportPayloadSizeBytes` (1 MiB by default), reserving
`PayloadCodecExpansionAllowanceBytes` for codec growth. The gzip codec is non-expanding and uses
the default zero allowance. If a custom codec can expand payloads, declare a verified upper bound;
the workflow cannot invoke the codec during deterministic preflight. For history summarization, the
guard checks the complete summarizer activity input and result, the compacted retained-history
projection (including its summary marker), and the complete Continue-as-New workflow input. An
oversized summary input or result leaves the last valid retained history intact and blocks
compaction with a queryable recovery reason; the library does not silently truncate the summary.
The guard is per payload and does not replace the codec's own bounds or prove that aggregate
Temporal request envelopes, later SDK encoding, or server/deployment limits fit. In particular,
the expansion allowance is an application-supplied bound, not a measurement of the codec output.

`CompressionLevel` defaults to `CompressionLevel.Fastest`. Faster compression generally spends less
CPU but may produce larger payloads; denser levels may reduce stored bytes at higher CPU cost. Measure
representative application payloads before changing it. Internal BenchmarkDotNet measurements on a
representative 50-tool/4-KiB declaration snapshot compressed to about 1.24% of its original size at a
locally acceptable per-payload CPU/allocation cost, which is why the codec ships opt-in and
threshold-gated rather than on by default; that evidence is repository-fixture-specific, not a
universal recommendation.

## Decoder-first rollout

1. Deploy decode-capable configuration to every client, worker, replayer, codec server, and
   operational reader while writers remain uncompressed.
2. Verify old uncompressed histories and operational tooling.
3. Enable encoding only after incompatible readers are gone. Use Worker Versioning or drain old
   readers when they cannot be upgraded independently.
4. To roll back, disable new encoding first but retain decoding until encoded histories have aged
   out or have been deliberately drained.

A reader without the codec fails when it encounters the library-owned encoding; it cannot infer or
repair the configuration. This includes workflow replay: a history produced with the gzip codec
replays with the compatible converter and fails explicitly with the default/no-codec converter; it
does not deserialize encoded values as empty or default data. Temporal Web and CLI require a
compatible codec server to display encoded payloads.

## Composition and security

Temporal exposes one `IPayloadCodec` slot. If encryption or another transformation is required,
the application owns a small composite codec: apply codecs in declared order during encode and in
reverse order during decode (for example, compress then encrypt on encode; decrypt then decompress
on decode), then pass that composite to `CreateDataConverter`. The library does not replace or wrap
an existing codec automatically.

Compression is neither encryption nor authentication. Encrypt secrets with an appropriate
authenticated encryption codec and apply normal Temporal namespace, transport, and application
authorization controls.

The codec protects serialized Temporal payloads; it does not encrypt workflow IDs, activity
summaries, search attributes, or memo fields. Keep prompts, tool arguments/results, credentials, and
other sensitive content out of those UI-visible metadata fields. The built-in activity summary
contains only `ChatOptions.ModelId` when configured; managed search attributes contain session
creation time and completed-turn count. Custom workflows remain responsible for keeping their own
IDs, summaries, memo, and custom search attributes content-free.

Payload codecs also do not protect OpenTelemetry output. MEAI inference message capture and the
durable library's optional tool-argument/result span attributes are governed separately by
`DurableExecutionOptions.CaptureTelemetryContent` and
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`. Keep that policy disabled unless telemetry
exporters, access controls, and retention are approved for the captured content.

Worker configuration snapshots contain routing keys and execution policy, not provider clients or
credentials. `DurableChatWorkflowInput.DefaultChatClientKey` is a worker-side DI route name, not a
secret store. Keep API keys in worker-owned secret configuration and inject the provider-backed
`IChatClient` on the worker; do not place credentials in workflow input, tool arguments, or model
messages. Any future history-summary wire types must use the same `DurableAIDataConverter` and
payload-codec path as other session entries.

The codec is opt-in, threshold-gated (it only stores the compressed form when it is smaller than
the original payload, so incompressible data isn't stored compressed), and requires a decoder-first
rollout across every reader before any writer enables compression. See the
[runnable sample](../../../samples/MEAI/PayloadCodec/README.md).
