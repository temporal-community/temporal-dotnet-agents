using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.AI.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob(RuntimeMoniker.Net10_0)]
[JsonExporterAttribute.Full]
public class ManagedHistoryFlatteningBenchmarks
{
    private IReadOnlyList<DurableSessionEntry> history = null!;

    [Params(0, 20, 200)]
    public int EntryCount { get; set; }

    [Params(1, 4, 20)]
    public int MessagesPerEntry { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        history = Enumerable.Range(0, EntryCount)
            .Select(entryIndex => (DurableSessionEntry)DurableSessionRequest.FromMessages(
                Enumerable.Range(0, MessagesPerEntry)
                    .Select(messageIndex => new ChatMessage(
                        ChatRole.User,
                        $"entry-{entryIndex:D4}-message-{messageIndex:D2}"))
                    .ToArray()))
            .ToArray();
    }

    [Benchmark(Baseline = true)]
    public List<ChatMessage> LinqSelectMany() => history
        .SelectMany(entry => entry.Messages)
        .ToList();

    [Benchmark]
    public List<ChatMessage> PrecountedAddRange()
    {
        var count = 0;
        foreach (var entry in history)
        {
            count = checked(count + entry.Messages.Count);
        }

        var messages = new List<ChatMessage>(count);
        foreach (var entry in history)
        {
            messages.AddRange(entry.Messages);
        }

        return messages;
    }
}
