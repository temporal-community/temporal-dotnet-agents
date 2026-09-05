# Opt-in durable AI payload compression

This sample configures the same `DurableAIDataConverter` plus bounded gzip codec on the Temporal
client and worker, then round-trips a compressible workflow payload.

```bash
dotnet run --project samples/MEAI/PayloadCodec/PayloadCodec.csproj
```

The Temporal service must be reachable at `TEMPORAL_ADDRESS` or `localhost:7233`.

The sample intentionally constructs the converter in one method. A production split deployment
must use an equivalent decoder on every client, workflow worker, activity worker, replayer, codec
server, and administrative reader before any writer enables compression. Removing the decoder while
encoded histories remain is unsafe.

The codec is disabled by default. Compression reduces stored payload bytes, unlike gRPC transport
compression, but it costs CPU and allocations and changes what Temporal Web and CLI tools can read
without a codec server. It preserves an input uncompressed when the complete encoded payload does
not meet the configured savings ratio.

The sample sets `CompressionLevel.Fastest`, which is also the library default. Other levels trade
more CPU for potentially smaller payloads; benchmark representative inputs before changing it.
Internal measurements found material size reduction (a representative declaration snapshot
compressed to about 1.24% of its original size) at an acceptable per-payload cost — which is why
the codec is opt-in and threshold-gated (only stored compressed when smaller than the original) and
requires the decoder-first rollout described above, rather than being enabled by default.

Compression is not encryption. Secrets still require an authenticated encryption codec, composed
by the application in the intended encode order and reverse decode order.
