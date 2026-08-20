# Durable AI payload compression benchmark

## Question

Durable model loops repeat tool declarations, grow message history, and carry state through
Continue-as-New. This benchmark measures whether a thresholded gzip `IPayloadCodec` materially
reduces those stored payloads, and what CPU/allocation cost it adds. It does not justify a compact
activity DTO or establish a timing gate for CI.

## Workloads

`AIPayloadCodecBenchmarks` uses the library's current MEAI-aware payload converter and the production
`DurableAIGzipPayloadCodec` API with deterministic fixtures for declaration snapshots, function
inputs, message history, and Continue-as-New state.
It covers 1, 50, and 250 tools; approximately 256-byte and 4-KiB schema content; and both repetitive
and deterministic high-entropy text. The benchmark serializes the complete Temporal `Payload`
before gzip, including its metadata.

Run locally:

```bash
just benchmark-ai-payloads
```

BenchmarkDotNet writes detailed timing and allocation results under
`BenchmarkDotNet.Artifacts/results/`. Do not add elapsed-time or universal compression-ratio
thresholds to PR CI; hosted-runner noise and genuinely incompressible data make those misleading.

## Evidence

Measured on an Apple M4 Max running macOS 26.6.1, .NET SDK 10.0.201, .NET 10.0.5,
BenchmarkDotNet 0.15.8, using the `ShortRun` job. The representative run used 50 tools with 4-KiB
schema content. These measurements are evidence for the design decision, not portable performance
guarantees.

| Production-codec workload | Mean | Allocation | Result |
|---|---:|---:|---|
| Encode, 50-tool/4-KiB repetitive declaration snapshot | 57.4 us | 18.0 KiB | 208,082 B to 2,575 B |
| Decode, same snapshot | 87.7 us | 1,050.8 KiB | original complete `Payload` restored |
| Encode, below threshold | 31.0 ns | 216 B | original `Payload` reference retained |
| Encode, 64-KiB deterministic high entropy | 345.1 us | 398.2 KiB | savings test failed; original reference retained |

The benchmark records the complete production codec result, including whether the codec returned
the original object. The repetitive snapshot encoded to 1.24% of its original size. The adverse
case establishes that gzip can consume material CPU and allocation even when the savings check
correctly declines to store the compressed representation.

The first production-codec run also identifies a concrete optimization target: decode allocated
about five times the 208-KiB restored payload size. The implementation currently copies the encoded
`ByteString`, allocates an 80-KiB buffer, copies decompressed bytes into a `MemoryStream`, calls
`ToArray`, and then parses the protobuf. Any follow-up optimization must remove measured copies while
preserving encoded/decoded bounds, secret-data clearing, corrupt-input behavior, and cross-version
readability.

## Decision

**Proceed with an opt-in bounded codec.** The declaration and Continue-as-New workloads that
motivated the investigation receive material size reduction at a locally acceptable per-payload
cost. The codec must remain disabled by default, enforce encoded and decoded size limits, and emit
the compressed representation only when it is smaller than the original complete Temporal payload.
This last rule avoids paying stored-history overhead for incompressible input; it does not avoid the
one-time compression attempt. Applications should choose a threshold appropriate to their workload
and use the metric below before enabling encoding.

Deployment must be decoder-first across every reader. The decision does not justify a compact tool
reference or any change to activity input contracts.

The public metric `temporal.ai.toolset.declaration_snapshot.size` (`By`) reports the serialized
once-per-session manifest size without high-cardinality dimensions. It is measured during toolset
resolution, not on every model activity.
