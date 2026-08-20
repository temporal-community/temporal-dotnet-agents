# Compressing durable AI payloads

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

Set that converter on every participating Temporal client and worker. The codec passes through
small payloads and payloads whose compressed representation does not meet the requested savings.
Decode rejects unsupported versions, corrupt data, and payloads that cross either configured bound.

`CompressionLevel` defaults to `CompressionLevel.Fastest`. Faster compression generally spends less
CPU but may produce larger payloads; denser levels may reduce stored bytes at higher CPU cost. Measure
representative application payloads before changing it. The
[payload-codec benchmark](../../benchmarks/ai-payload-codecs.md) is evidence for this repository's
fixtures, not a universal recommendation.

## Decoder-first rollout

1. Deploy decode-capable configuration to every client, worker, replayer, codec server, and
   operational reader while writers remain uncompressed.
2. Verify old uncompressed histories and operational tooling.
3. Enable encoding only after incompatible readers are gone. Use Worker Versioning or drain old
   readers when they cannot be upgraded independently.
4. To roll back, disable new encoding first but retain decoding until encoded histories have aged
   out or have been deliberately drained.

A reader without the codec fails when it encounters the library-owned encoding; it cannot infer or
repair the configuration. Temporal Web and CLI require a compatible codec server to display encoded
payloads.

## Composition and security

Temporal exposes one `IPayloadCodec` slot. If encryption or another transformation is required,
the application owns a small composite codec: apply codecs in declared order during encode and in
reverse order during decode, then pass that composite to `CreateDataConverter`. The library does not
replace or wrap an existing codec automatically.

Compression is neither encryption nor authentication. Encrypt secrets with an appropriate
authenticated encryption codec and apply normal Temporal namespace, transport, and application
authorization controls.

See the [measured benchmark](../../benchmarks/ai-payload-codecs.md) and the
[runnable sample](../../../samples/MEAI/PayloadCodec/README.md).
