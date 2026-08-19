# Durable AI payload compression benchmark

## Question

Durable model loops repeat tool declarations, grow message history, and carry state through
Continue-as-New. This benchmark measures whether a thresholded gzip `IPayloadCodec` materially
reduces those stored payloads, and what CPU/allocation cost it adds. It does not justify a compact
activity DTO or establish a timing gate for CI.

## Workloads

`AIPayloadCodecBenchmarks` uses the library's current MEAI-aware payload converter and deterministic
fixtures for declaration snapshots, function inputs, message history, and Continue-as-New state.
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

| Payload | Content | Encode mean | Decode mean | Encode allocation | Decode allocation |
|---|---|---:|---:|---:|---:|
| Declaration snapshot | Repetitive | 56.5 us | 64.4 us | 17.8 KiB | 416.4 KiB |
| Continue-as-New | Repetitive | 64.2 us | 72.1 us | 18.5 KiB | 468.0 KiB |
| Declaration snapshot | High entropy | 1.23 ms | 301.8 us | 894.3 KiB | 615.9 KiB |
| Continue-as-New | High entropy | 1.47 ms | 358.2 us | 944.5 KiB | 692.3 KiB |

The deterministic structural fixture (100 tools with 4-KiB schemas) also records size directly:

| Content | Raw bytes | Gzip bytes | Encoded/raw |
|---|---:|---:|---:|
| Repetitive | 412,590 | 4,770 | 1.16% |
| High entropy | 412,590 | 410,314 | 99.45% |

Both fixtures round-trip byte-for-byte. The adverse case establishes that gzip can consume material
CPU and allocation for effectively no history reduction.

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
