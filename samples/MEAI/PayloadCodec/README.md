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

Compression is not encryption. Secrets still require an authenticated encryption codec, composed
by the application in the intended encode order and reverse decode order.
