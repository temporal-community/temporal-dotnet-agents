using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using Google.Protobuf;
using Temporalio.Api.Common.V1;

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
    private IReadOnlyCollection<Payload> input = null!;
    private IReadOnlyCollection<Payload> encoded = null!;
    private DurableAIGzipPayloadCodec codec = null!;

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
        input = [payload];
        codec = CreateCodec(minimumPayloadSizeBytes: 256, minimumSavingsRatio: 0.05);
        encoded = codec.EncodeAsync(input).GetAwaiter().GetResult();

        var encodedPayload = encoded.Single();
        Console.WriteLine(
            $"[production-codec-fixture] kind={PayloadKind}; tools={ToolCount}; schema={SchemaBytes}; " +
            $"incompressible={Incompressible}; raw={payload.CalculateSize()}; " +
            $"encoded={encodedPayload.CalculateSize()}; passThrough={ReferenceEquals(payload, encodedPayload)}");
    }

    [Benchmark(Baseline = true)]
    public Payload CloneUnencodedPayload() => payload.Clone();

    [Benchmark]
    public Task<IReadOnlyCollection<Payload>> EncodeProductionCodec() => codec.EncodeAsync(input);

    [Benchmark]
    public Task<IReadOnlyCollection<Payload>> DecodeProductionCodec() => codec.DecodeAsync(encoded);

    private static DurableAIGzipPayloadCodec CreateCodec(
        int minimumPayloadSizeBytes,
        double minimumSavingsRatio) => new(new DurableAIGzipPayloadCodecOptions
        {
            MinimumPayloadSizeBytes = minimumPayloadSizeBytes,
            MaximumEncodedPayloadSizeBytes = 16 * 1024 * 1024,
            MaximumDecodedPayloadSizeBytes = 32 * 1024 * 1024,
            MinimumCompressionSavingsRatio = minimumSavingsRatio,
        });
}

[MemoryDiagnoser]
[ShortRunJob(RuntimeMoniker.Net10_0)]
[JsonExporterAttribute.Full]
public class AIPayloadCodecBenchmarksPassThrough
{
    private IReadOnlyCollection<Payload> belowThreshold = null!;
    private IReadOnlyCollection<Payload> insufficientSavings = null!;
    private DurableAIGzipPayloadCodec thresholdCodec = null!;
    private DurableAIGzipPayloadCodec savingsCodec = null!;

    [GlobalSetup]
    public void Setup()
    {
        var small = DurableAIDataConverter.Instance.PayloadConverter.ToPayload("small");
        var random = new Random(1729);
        var bytes = new byte[64 * 1024];
        random.NextBytes(bytes);
        var highEntropy = new Payload { Data = ByteString.CopyFrom(bytes) };

        belowThreshold = [small];
        insufficientSavings = [highEntropy];
        thresholdCodec = new(new DurableAIGzipPayloadCodecOptions
        {
            MinimumPayloadSizeBytes = small.CalculateSize() + 1,
            MaximumEncodedPayloadSizeBytes = 1024 * 1024,
            MaximumDecodedPayloadSizeBytes = 1024 * 1024,
        });
        savingsCodec = new(new DurableAIGzipPayloadCodecOptions
        {
            MinimumPayloadSizeBytes = 1,
            MaximumEncodedPayloadSizeBytes = 1024 * 1024,
            MaximumDecodedPayloadSizeBytes = 1024 * 1024,
            MinimumCompressionSavingsRatio = 0.05,
        });

        var thresholdResult = thresholdCodec.EncodeAsync(belowThreshold).GetAwaiter().GetResult().Single();
        var savingsResult = savingsCodec.EncodeAsync(insufficientSavings).GetAwaiter().GetResult().Single();
        Console.WriteLine(
            $"[production-codec-pass-through] belowThreshold={ReferenceEquals(small, thresholdResult)}; " +
            $"insufficientSavings={ReferenceEquals(highEntropy, savingsResult)}");
    }

    [Benchmark(Baseline = true)]
    public Task<IReadOnlyCollection<Payload>> EncodeBelowThreshold() =>
        thresholdCodec.EncodeAsync(belowThreshold);

    [Benchmark]
    public Task<IReadOnlyCollection<Payload>> EncodeInsufficientSavings() =>
        savingsCodec.EncodeAsync(insufficientSavings);
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
