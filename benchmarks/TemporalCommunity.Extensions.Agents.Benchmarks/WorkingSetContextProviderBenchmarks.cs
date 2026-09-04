using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.Agents.Benchmarks;

/// <summary>
/// Compares the current full-history extraction with an output-equivalent incremental model.
/// The model is benchmark-only evidence, not shipment evidence.
/// Cursor work is conditional on ≥2x improvement demonstrated with realistic data.
/// 
/// Phase 4 gate: This benchmark validates that cursor complexity is justified by comparing
/// synthetic one-liner messages against realistic messages (code fences, varied lengths,
/// multiple file references per message).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob(RuntimeMoniker.Net10_0)]
[JsonExporterAttribute.Full]
public class WorkingSetContextProviderBenchmarks
{
    private IReadOnlyList<ChatMessage> history = [];
    private IReadOnlyList<ChatMessage> tail = [];
    private IReadOnlyList<string> priorWorkingSet = [];

    // Realistic message fixture
    private IReadOnlyList<ChatMessage> realisticHistory = [];
    private IReadOnlyList<ChatMessage> realisticTail = [];
    private IReadOnlyList<string> realisticPriorWorkingSet = [];

    /// <summary>Gets or sets how many accumulated messages are scanned at the next model step.</summary>
    [Params(100, 1000)]
    public int HistoryMessageCount { get; set; }

    /// <summary>Determines whether to use realistic (true) or synthetic (false) messages.</summary>
    [Params(false, true)]
    public bool UseRealisticMessages { get; set; }

    /// <summary>Initializes a history whose final message represents the next incremental step.</summary>
    [GlobalSetup]
    public void Setup()
    {
        // Synthetic one-liner benchmark (baseline)
        history = Enumerable.Range(0, HistoryMessageCount)
            .Select(index => new ChatMessage(
                ChatRole.Assistant,
                $"Updated src/project/Feature{index:D5}.cs with analysis {index}."))
            .ToArray();
        tail = [history[^1]];
        priorWorkingSet = WorkingSetContextProvider.ExtractFilePaths(
            history.Take(history.Count - 1),
            maxPaths: 20);

        // Realistic messages with code fences, varied lengths, multiple paths
        var realisticMessages = RealisticMessageGenerator.GenerateRealisticMessages(HistoryMessageCount);
        realisticHistory = realisticMessages
            .Select(msg => new ChatMessage(ChatRole.Assistant, msg))
            .ToArray();
        realisticTail = [realisticHistory[^1]];
        realisticPriorWorkingSet = WorkingSetContextProvider.ExtractFilePaths(
            realisticHistory.Take(realisticHistory.Count - 1),
            maxPaths: 20);
    }

    /// <summary>
    /// Measures the current implementation with selected message fixture (synthetic or realistic).
    /// Baseline uses synthetic one-liners.
    /// </summary>
    [Benchmark(Baseline = true)]
    public IReadOnlyList<string> FullHistory()
    {
        var (h, _) = GetFixture();
        return WorkingSetContextProvider.ExtractFilePaths(h, maxPaths: 20);
    }

    /// <summary>
    /// Measures only the new-message scan plus recency-window merge. 
    /// This is not production code; it establishes the maximum benefit before adding durable cursor state.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<string> IncrementalPrototype()
    {
        var (_, t) = GetFixture();
        var (prior, _) = GetPriorFixture();
        return MergeRecentPaths(
            prior,
            WorkingSetContextProvider.ExtractFilePaths(t, maxPaths: 20),
            maxPaths: 20);
    }

    private (IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>) GetFixture()
    {
        return UseRealisticMessages ? (realisticHistory, realisticTail) : (history, tail);
    }

    private (IReadOnlyList<string>, IReadOnlyList<string>) GetPriorFixture()
    {
        return UseRealisticMessages ? (realisticPriorWorkingSet, realisticPriorWorkingSet) : (priorWorkingSet, priorWorkingSet);
    }

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

/// <summary>
/// Generates realistic dev-conversation messages with varied lengths, code fences,
/// and multiple file references to simulate real-world WorkingSet extraction scenarios.
/// </summary>
internal static class RealisticMessageGenerator
{
    private static readonly string[] Projects = new[]
    {
        "temporal-agents", "temporal-sdk", "workflows", "samples",
        "payment-service", "order-processing", "user-management"
    };

    private static readonly string[] Directories = new[]
    {
        "src", "tests", "tools", "scripts", "docs", "samples",
        "lib", "internal", "api", "models", "controllers", "services"
    };

    private static readonly string[] Subdirs = new[]
    {
        "core", "utils", "helpers", "extensions", "middleware", "handlers",
        "repositories", "dto", "config", "constants", "validators"
    };

    private static readonly string[] FilePatterns = new[]
    {
        "Agent{0}.cs", "Handler{0}.cs", "Service{0}.cs", "Repository{0}.cs",
        "Controller{0}.cs", "Workflow{0}.cs", "Activity{0}.cs", "Adapter{0}.cs",
        "Builder{0}.cs", "Factory{0}.cs", "Utils{0}.cs", "Helper{0}.cs"
    };

    private static readonly string[] CodeLanguages = new[]
    {
        "csharp", "typescript", "python", "go", "java", "rust", "sql", "json", "yaml"
    };

    private static readonly string[] ResponseHeaders = new[]
    {
        "Here's how to do that:\n",
        "You can implement it like this:\n",
        "That's a good question. Here's the pattern:\n",
        "Let me show you the solution:\n",
        "The best approach is:\n",
    };

    public static List<string> GenerateRealisticMessages(int count)
    {
        var rng = new Random(42); // Fixed seed for reproducibility
        var messages = new List<string>();

        for (int i = 0; i < count; i++)
        {
            messages.Add(GenerateMessage(i, rng));
        }

        return messages;
    }

    private static string GenerateMessage(int index, Random rng)
    {
        var type = rng.Next(0, 3);
        return type switch
        {
            0 => GenerateQueryMessage(rng),
            1 => GenerateCodeMessage(index, rng),
            _ => GenerateExplanationMessage(index, rng)
        };
    }

    private static string GenerateQueryMessage(Random rng)
    {
        var queries = new[] {
            "How do I implement error handling?",
            "What's the best way to handle retries?",
            "Can you show me a workflow example?",
            "How do I manage durable state?",
            "What pattern should I use for saga compensation?"
        };
        return queries[rng.Next(queries.Length)];
    }

    private static string GenerateCodeMessage(int index, Random rng)
    {
        var sb = new System.Text.StringBuilder();
        var header = ResponseHeaders[rng.Next(ResponseHeaders.Length)];
        sb.Append(header);

        // Add code fence with realistic file path
        var lang = CodeLanguages[rng.Next(CodeLanguages.Length)];
        sb.Append($"```{lang}\n");
        
        var filePath = GenerateFilePath(index, rng);
        sb.Append(filePath).Append("\n");

        // Add realistic code lines
        var lines = rng.Next(3, 10);
        for (int i = 0; i < lines; i++)
        {
            sb.Append(GenerateCodeLine(lang)).Append("\n");
        }

        sb.Append("```\n");

        // Add more file paths in text
        if (rng.Next(0, 2) == 0)
        {
            for (int i = 0; i < rng.Next(1, 3); i++)
            {
                sb.Append("Related file: ").Append(GenerateFilePath(index + i + 1, rng)).Append("\n");
            }
        }

        return sb.ToString();
    }

    private static string GenerateExplanationMessage(int index, Random rng)
    {
        var sb = new System.Text.StringBuilder();
        var concepts = new[] {
            "This pattern ensures proper error handling.",
            "The key insight is consistent state management.",
            "Make sure to use idempotent activities.",
            "Remember that timeouts are critical.",
            "Consider proper compensation logic.",
        };
        
        sb.Append(concepts[rng.Next(concepts.Length)]).Append(" ");
        
        if (rng.Next(0, 2) == 0)
        {
            sb.Append("See ").Append(GenerateFilePath(index, rng)).Append(" for the pattern.");
        }

        return sb.ToString();
    }

    private static string GenerateFilePath(int index, Random rng)
    {
        var project = Projects[rng.Next(Projects.Length)];
        var dir = Directories[rng.Next(Directories.Length)];
        var subdir = Subdirs[rng.Next(Subdirs.Length)];
        var filePattern = FilePatterns[rng.Next(FilePatterns.Length)];

        var filename = string.Format(filePattern, index % 100);
        return $"{project}/{dir}/{subdir}/{filename}";
    }

    private static string GenerateCodeLine(string lang)
    {
        return lang switch
        {
            "csharp" => "public async Task Execute() => await _service.Process();",
            "typescript" => "const result = await client.workflow.execute(myWorkflow, args);",
            "python" => "result = await client.execute_workflow(MyWorkflow, args)",
            "go" => "result, err := client.ExecuteWorkflow(ctx, MyWorkflow, args)",
            "java" => "WorkflowResult result = client.executeWorkflow(MyWorkflow.class, args);",
            "rust" => "let result = client.execute_workflow(my_workflow, args).await?;",
            "sql" => "SELECT * FROM workflows WHERE status = 'running';",
            "json" => "\"workflow\": { \"id\": \"wf-123\", \"status\": \"running\" }",
            _ => "// code example"
        };
    }
}
