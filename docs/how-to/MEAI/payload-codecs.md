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

Set that converter on every participating Temporal client and worker. The codec passes through
small payloads and payloads whose compressed representation does not meet the requested savings.
Decode rejects unsupported versions, corrupt data, and payloads that cross either configured bound.

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
reverse order during decode, then pass that composite to `CreateDataConverter`. The library does not
replace or wrap an existing codec automatically.

Compression is neither encryption nor authentication. Encrypt secrets with an appropriate
authenticated encryption codec and apply normal Temporal namespace, transport, and application
authorization controls.

The codec is opt-in, threshold-gated (it only stores the compressed form when it is smaller than
the original payload, so incompressible data isn't stored compressed), and requires a decoder-first
rollout across every reader before any writer enables compression. See the
[runnable sample](../../../samples/MEAI/PayloadCodec/README.md).
