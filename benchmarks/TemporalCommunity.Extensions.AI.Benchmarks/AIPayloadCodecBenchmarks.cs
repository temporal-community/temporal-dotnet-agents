using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace TemporalCommunity.Extensions.AI.Benchmarks;

public enum AIPayloadKind
{
    DeclarationSnapshot,
    InvokeFunction,
    MessageHistory,
    ContinueAsNew,
}

[MemoryDiagnoser]
[ShortRunJob(RuntimeMoniker.Net10_0)]
[JsonExporterAttribute.Full]
public class AIPayloadCodecBenchmarks
{
    private Payload payload = null!;
    private Payload encoded = null!;

    [Params(1, 50, 250)]
    public int ToolCount { get; set; }

    [Params(256, 4096)]
    public int SchemaBytes { get; set; }

    [Params(false, true)]
    public bool Incompressible { get; set; }

    [ParamsAllValues]
    public AIPayloadKind PayloadKind { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var fixture = AIPayloadFixture.Create(
            PayloadKind,
            ToolCount,
            SchemaBytes,
            Incompressible);
        payload = DurableAIDataConverter.Instance.PayloadConverter.ToPayload(fixture);
        encoded = BenchmarkGzip.Encode(payload, thresholdBytes: 256);
    }

    [Benchmark(Baseline = true)]
    public Payload CurrentConverter() => payload.Clone();

    [Benchmark]
    public Payload EncodeThresholdedGzip() => BenchmarkGzip.Encode(payload, thresholdBytes: 256);

    [Benchmark]
    public Payload DecodeThresholdedGzip() => BenchmarkGzip.Decode(encoded);
}

internal static class BenchmarkGzip
{
    private const string Encoding = "binary/gzip-temporal-ai-benchmark-v1";

    internal static Payload Encode(Payload payload, int thresholdBytes)
    {
        if (payload.CalculateSize() < thresholdBytes)
        {
            return payload.Clone();
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            payload.WriteTo(gzip);
        }

        var encoded = new Payload { Data = ByteString.CopyFrom(output.ToArray()) };
        encoded.Metadata["encoding"] = ByteString.CopyFromUtf8(Encoding);
        return encoded;
    }

    internal static Payload Decode(Payload payload)
    {
        if (!payload.Metadata.TryGetValue("encoding", out var encoding)
            || encoding.ToStringUtf8() != Encoding)
        {
            return payload.Clone();
        }

        using var input = new MemoryStream(payload.Data.ToByteArray());
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return Payload.Parser.ParseFrom(gzip);
    }
}

internal sealed record AIPayloadFixture(
    string Kind,
    IReadOnlyList<AIPayloadTool> Tools,
    IReadOnlyList<string> Messages,
    string Arguments,
    string State)
{
    internal static AIPayloadFixture Create(
        AIPayloadKind kind,
        int toolCount,
        int schemaBytes,
        bool incompressible)
    {
        var tools = Enumerable.Range(0, toolCount)
            .Select(index => new AIPayloadTool(
                $"tool_{index:D4}",
                CreateContent(schemaBytes, incompressible, index)))
            .ToArray();
        var messages = Enumerable.Range(0, Math.Max(1, toolCount / 2))
            .Select(index => $"message-{index:D4}:{CreateContent(512, incompressible, index + 1000)}")
            .ToArray();

        return kind switch
        {
            AIPayloadKind.DeclarationSnapshot => new(
                kind.ToString(), tools, [], string.Empty, string.Empty),
            AIPayloadKind.InvokeFunction => new(
                kind.ToString(), [], [], CreateContent(schemaBytes, incompressible, 2000), string.Empty),
            AIPayloadKind.MessageHistory => new(
                kind.ToString(), [], messages, string.Empty, string.Empty),
            AIPayloadKind.ContinueAsNew => new(
                kind.ToString(), tools, messages, string.Empty,
                CreateContent(Math.Max(schemaBytes, toolCount * 256), incompressible, 3000)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static string CreateContent(int length, bool incompressible, int seed)
    {
        if (!incompressible)
        {
            return new string('x', length);
        }

        const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random(seed);
        return string.Create(length, random, static (span, state) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = Alphabet[state.Next(Alphabet.Length)];
            }
        });
    }
}

internal sealed record AIPayloadTool(string Name, string JsonSchema);
