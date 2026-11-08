using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

namespace TemporalCommunity.Extensions.Agents.Benchmarks;

/// <summary>
/// Measures failed-turn StateBag restoration independently of Temporal server and model latency.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob(RuntimeMoniker.Net10_0)]
[JsonExporterAttribute.Full]
public class StateBagRollbackBenchmarks
{
    private JsonElement? _beforeTurn;
    private JsonElement? _currentStateBag;

    /// <summary>Gets or sets the approximate ordinary StateBag payload size.</summary>
    [Params(0, 64 * 1024, 1024 * 1024)]
    public int PayloadBytes { get; set; }

    /// <summary>Gets or sets how many top-level keys divide the payload.</summary>
    [Params(1, 64)]
    public int KeyCount { get; set; }

    /// <summary>Creates deterministic pre-turn and failed-turn StateBag inputs.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var before = CreateOrdinaryEntries("before");
        var after = CreateOrdinaryEntries("failed");
        after["failed-turn-only"] = "discard";

        _beforeTurn = JsonSerializer.SerializeToElement(before);
        _currentStateBag = JsonSerializer.SerializeToElement(after);
    }

    /// <summary>Restores the immutable pre-turn snapshot, matching workflow rollback.</summary>
    [Benchmark]
    public JsonElement? RestoreTurnOwnedState()
    {
        _currentStateBag = _beforeTurn;
        return _currentStateBag;
    }

    private Dictionary<string, string> CreateOrdinaryEntries(string prefix)
    {
        if (PayloadBytes == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var valueLength = Math.Max(1, PayloadBytes / KeyCount);
        var entries = new Dictionary<string, string>(KeyCount, StringComparer.Ordinal);
        for (var index = 0; index < KeyCount; index++)
        {
            entries[$"state-{index:D4}"] = $"{prefix}:{new string('x', valueLength)}";
        }

        return entries;
    }
}
