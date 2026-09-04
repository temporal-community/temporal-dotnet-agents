using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.Agents.Benchmarks;

/// <summary>
/// Compares the current full-history extraction with an output-equivalent incremental model.
/// The model is benchmark-only: production adoption still requires StateBag cursor and
/// continue-as-new/compaction equivalence tests.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob(RuntimeMoniker.Net10_0)]
[JsonExporterAttribute.Full]
public class WorkingSetContextProviderBenchmarks
{
    private IReadOnlyList<ChatMessage> history = [];
    private IReadOnlyList<ChatMessage> tail = [];
    private IReadOnlyList<string> priorWorkingSet = [];

    /// <summary>Gets or sets how many accumulated messages are scanned at the next model step.</summary>
    [Params(100, 1000, 10_000)]
    public int HistoryMessageCount { get; set; }

    /// <summary>Initializes a history whose final message represents the next incremental step.</summary>
    [GlobalSetup]
    public void Setup()
    {
        history = Enumerable.Range(0, HistoryMessageCount)
            .Select(index => new ChatMessage(
                ChatRole.Assistant,
                $"Updated src/project/Feature{index:D5}.cs with analysis {index}."))
            .ToArray();
        tail = [history[^1]];
        priorWorkingSet = WorkingSetContextProvider.ExtractFilePaths(
            history.Take(history.Count - 1),
            maxPaths: 20);
    }

    /// <summary>Measures the current implementation, which re-scans all accumulated history.</summary>
    [Benchmark(Baseline = true)]
    public IReadOnlyList<string> FullHistory() =>
        WorkingSetContextProvider.ExtractFilePaths(history, maxPaths: 20);

    /// <summary>
    /// Measures only the new-message scan plus recency-window merge. This is not production code;
    /// it establishes the maximum benefit before adding durable cursor state.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<string> IncrementalPrototype() =>
        MergeRecentPaths(
            priorWorkingSet,
            WorkingSetContextProvider.ExtractFilePaths(tail, maxPaths: 20),
            maxPaths: 20);

    private static IReadOnlyList<string> MergeRecentPaths(
        IReadOnlyList<string> prior,
        IReadOnlyList<string> additions,
        int maxPaths)
    {
        var seen = new HashSet<string>(prior, StringComparer.OrdinalIgnoreCase);
        var ordered = prior.ToList();

        foreach (var path in additions)
        {
            if (!seen.Add(path))
            {
                ordered.Remove(path);
            }

            ordered.Add(path);
        }

        return ordered.Count <= maxPaths
            ? ordered
            : ordered.GetRange(ordered.Count - maxPaths, maxPaths);
    }
}
