using System.Reflection;
using Temporalio.Activities;
using Temporalio.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.Agents;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Compat;

/// <summary>
/// Wire-name contract tests: reflection-based tripwire that asserts every
/// <c>[Activity("...")]</c> and <c>[Workflow("...")]</c> string in both
/// production assemblies matches a frozen expected set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Purpose.</b> Temporal workflow and activity type names are part of the
/// wire protocol. Any rename — even a "safe" refactor rename — breaks in-flight
/// sessions, history replay, and scheduled workflows. These tests pin the exact
/// strings so that a rename is a compile-level CI failure, not a silent runtime break.
/// This is the cheap tripwire that would have caught the <c>Temporalio.Extensions.*</c> →
/// <c>TemporalCommunity.Extensions.*</c> rebrand if a string were missed.
/// </para>
/// <para>
/// This test lives in the Agents test project because the Agents library depends
/// on the AI library — so both assemblies are available here.
/// </para>
/// <para>
/// If these tests fail, examine the diff and decide:
/// <list type="bullet">
///   <item>If the rename was intentional: update the expected set AND the checked-in
///         JSON histories in <c>tests/TemporalCommunity.Extensions.AI.Tests/Compat/Histories/</c>
///         (re-run <c>HistoryCaptureTests</c>).</item>
///   <item>If the rename was accidental: revert it.</item>
/// </list>
/// </para>
/// <para>
/// <b>Coverage.</b> The test enumerates types in both source assemblies, collects
/// every <c>[Activity]</c> and <c>[Workflow]</c> attribute string, and verifies
/// a two-way subset:
/// <list type="number">
///   <item><i>Found ⊆ Expected</i> — no surprise additions or renames.</item>
///   <item><i>Expected ⊆ Found</i> — nothing was silently deleted.</item>
/// </list>
/// </para>
/// </remarks>
public class WireNameContractTests
{
    // ── Frozen expected wire-name sets ──────────────────────────────────────

    /// <summary>
    /// All <c>[Workflow("...")]</c> wire names in both assemblies as of the
    /// <c>TemporalCommunity.Extensions.*</c> rebrand (Wave B baseline).
    /// </summary>
    private static readonly HashSet<string> ExpectedWorkflowNames = new(StringComparer.Ordinal)
    {
        // AI library
        "TemporalCommunity.Extensions.AI.DurableChatWorkflow",

        // Agents library
        "TemporalCommunity.Extensions.Agents.AgentWorkflow",
        "TemporalCommunity.Extensions.Agents.AgentJobWorkflow",
    };

    /// <summary>
    /// All <c>[Activity("...")]</c> wire names in both assemblies as of the
    /// <c>TemporalCommunity.Extensions.*</c> rebrand (Wave B baseline).
    /// </summary>
    private static readonly HashSet<string> ExpectedActivityNames = new(StringComparer.Ordinal)
    {
        // AI library activities
        "TemporalCommunity.Extensions.AI.GetResponse",
        "TemporalCommunity.Extensions.AI.GetChatStep",
        "TemporalCommunity.Extensions.AI.ReduceHistoryByKey",
        "TemporalCommunity.Extensions.AI.RunToolInterceptor",
        "TemporalCommunity.Extensions.AI.InvokeFunction",
        "TemporalCommunity.Extensions.AI.GenerateEmbedding",
        "TemporalCommunity.Extensions.AI.ResolveDurableToolsets",

        // Agents library activities
        "TemporalCommunity.Extensions.Agents.ReduceHistoryByKey",
        "TemporalCommunity.Extensions.Agents.RunDurableAgentStep",
        "TemporalCommunity.Extensions.Agents.RunToolInterceptor",
        "TemporalCommunity.Extensions.Agents.LoadAlwaysScopes",
        "TemporalCommunity.Extensions.Agents.AppendAlwaysScope",
        "TemporalCommunity.Extensions.Agents.InvokeAgentTool",
    };

    // ── Assembly references ─────────────────────────────────────────────────

    /// <summary>
    /// The AI library assembly.
    /// Anchored on <see cref="DurableChatSessionClient"/> which is internal to the AI lib.
    /// </summary>
    private static readonly Assembly AiAssembly = typeof(DurableChatSessionClient).Assembly;

    /// <summary>
    /// The Agents library assembly.
    /// Anchored on <see cref="TemporalAgentsOptions"/> (public type in the Agents lib).
    /// This test project references the Agents library which transitively references the AI library,
    /// making both assemblies available here.
    /// </summary>
    private static readonly Assembly AgentsAssembly = typeof(TemporalAgentsOptions).Assembly;

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// All <c>[Workflow]</c> wire names in both assemblies exactly match the expected set.
    /// An extra name = new/renamed workflow added without updating this file.
    /// A missing name = a workflow was silently removed.
    /// </summary>
    [Fact]
    public void WorkflowWireNames_MatchExpectedSet_Exactly()
    {
        var found = CollectWorkflowNames(AiAssembly, AgentsAssembly);

        AssertSetsMatch(found, ExpectedWorkflowNames, "workflow");
    }

    /// <summary>
    /// All <c>[Activity]</c> wire names in both assemblies exactly match the expected set.
    /// </summary>
    [Fact]
    public void ActivityWireNames_MatchExpectedSet_Exactly()
    {
        var found = CollectActivityNames(AiAssembly, AgentsAssembly);

        AssertSetsMatch(found, ExpectedActivityNames, "activity");
    }

    /// <summary>
    /// All discovered workflow wire names start with the <c>TemporalCommunity.Extensions.</c> prefix.
    /// This guards against reverting to the old <c>Temporalio.Extensions.*</c> prefix.
    /// </summary>
    [Fact]
    public void AllWorkflowWireNames_HaveTemporalCommunityPrefix()
    {
        var found = CollectWorkflowNames(AiAssembly, AgentsAssembly);

        foreach (var name in found)
        {
            Assert.True(
                name.StartsWith("TemporalCommunity.Extensions.", StringComparison.Ordinal),
                $"Workflow wire name '{name}' does not start with 'TemporalCommunity.Extensions.'.");
        }
    }

    /// <summary>
    /// All discovered activity wire names start with the <c>TemporalCommunity.Extensions.</c> prefix.
    /// </summary>
    [Fact]
    public void AllActivityWireNames_HaveTemporalCommunityPrefix()
    {
        var found = CollectActivityNames(AiAssembly, AgentsAssembly);

        foreach (var name in found)
        {
            Assert.True(
                name.StartsWith("TemporalCommunity.Extensions.", StringComparison.Ordinal),
                $"Activity wire name '{name}' does not start with 'TemporalCommunity.Extensions.'.");
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Collect all explicit <c>[Workflow("name")]</c> wire names from the given assemblies.
    /// Only picks up types that carry a <see cref="WorkflowAttribute"/> with a non-null,
    /// non-empty Name — skipping any workflows that use the default (class name) registration.
    /// </summary>
    private static HashSet<string> CollectWorkflowNames(params Assembly[] assemblies)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                var attr = type.GetCustomAttribute<WorkflowAttribute>();
                if (attr is { Name: { Length: > 0 } name })
                    names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// Collect all explicit <c>[Activity("name")]</c> wire names from the given assemblies.
    /// Inspects all public and non-public static and instance methods.
    /// </summary>
    private static HashSet<string> CollectActivityNames(params Assembly[] assemblies)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static;
        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                foreach (var method in type.GetMethods(flags))
                {
                    var attr = method.GetCustomAttribute<ActivityAttribute>();
                    if (attr is { Name: { Length: > 0 } name })
                        names.Add(name);
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Assert that <paramref name="found"/> and <paramref name="expected"/> are identical sets.
    /// Reports both directions so a single failure message is actionable.
    /// </summary>
    private static void AssertSetsMatch(
        HashSet<string> found,
        HashSet<string> expected,
        string kind)
    {
        var unexpected = found.Except(expected).OrderBy(x => x).ToList();
        var missing = expected.Except(found).OrderBy(x => x).ToList();

        if (unexpected.Count > 0 || missing.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Wire-name contract violation for {kind} names:");
            if (unexpected.Count > 0)
            {
                sb.AppendLine("  UNEXPECTED (found in assembly but not in expected set):");
                foreach (var n in unexpected) sb.AppendLine($"    - {n}");
                sb.AppendLine("  → If intentional: add to expected set AND update Compat/Histories/ JSONs.");
            }
            if (missing.Count > 0)
            {
                sb.AppendLine("  MISSING (in expected set but not found in assembly):");
                foreach (var n in missing) sb.AppendLine($"    - {n}");
                sb.AppendLine("  → If intentional: remove from expected set.");
            }
            Assert.Fail(sb.ToString());
        }
    }
}
