// SearchContextProvider.cs — demonstrates two approaches to registering durable tools
// alongside a context provider via IDurableToolSource and DurableToolRegistrationSpec.
//
// This file contains:
//   SearchContextProvider   — an AIContextProvider that also implements IDurableToolSource,
//                             declaring its tools at registration time. The framework registers
//                             them as durable activities automatically (no explicit AddTool calls).
//   WebSearchStub           — a lightweight web-search stub using AIFunctionFactory.Create.
//                             In production this would call a real search API (Bing, Brave, etc.).
//
// TODO: demonstrate with HyperlightCodeActProvider once HyperlightSandbox.Api is on NuGet.

#pragma warning disable TA001 // IDurableToolSource is experimental; intentional usage in sample

using System.ComponentModel;
using DurableContextProvider;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Tools;

namespace DurableContextProvider;

/// <summary>
/// A context provider that injects a configurable research persona instruction and also
/// declares a durable web-search tool by implementing <see cref="IDurableToolSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// By implementing <see cref="IDurableToolSource"/>, this provider tells the framework to
/// register <c>web_search</c> as a durable Temporal activity at startup — identical to calling
/// <c>agent.AddTool(webSearch)</c> explicitly. You only need one registration path; do not
/// call both <c>AddTool</c> and <c>IDurableToolSource</c> for the same tool, or you'll get
/// a duplicate-name error.
/// </para>
/// <para>
/// The per-iteration strip in <c>AgentActivities</c> automatically nulls out
/// <c>AIContext.Tools</c> after this provider's <c>InvokingAsync</c> call, so downstream
/// providers don't see the provider-contributed tools in their context.
/// </para>
/// </remarks>
public sealed class SearchContextProvider : AIContextProvider, IDurableToolSource
{
    private readonly string _researchPersona;
    private readonly IReadOnlyList<DurableToolRegistrationSpec> _specs;

    public SearchContextProvider(string researchPersona = "You are a research assistant. Use web_search to find current information before answering.")
    {
        _researchPersona = researchPersona;

        // Declare the tool this provider contributes. The framework will register
        // web_search as a durable Temporal activity (InvokeAgentTool:SearchAgent:web_search).
        // Write tools MUST set Configure = opts => opts.NoRetry() to prevent double-execution.
        // web_search is read-only, so we use the default retry policy (idempotent).
        _specs =
        [
            new DurableToolRegistrationSpec(WebSearchStub.CreateFunction()),
        ];
    }

    /// <summary>
    /// Returns the durable tools this provider contributes. Called once at registration time.
    /// </summary>
    public IReadOnlyList<DurableToolRegistrationSpec> GetDurableTools() => _specs;

    /// <inheritdoc/>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // Inject the research persona as an additional instruction before each LLM call.
        // Context providers fire once per LLM step, not once per turn — keep this cheap.
        return ValueTask.FromResult(new AIContext
        {
            Instructions = _researchPersona,
        });
    }
}

/// <summary>
/// A lightweight web-search stub. In a real application this would call Bing, Brave Search,
/// Tavily, or another search API. This version simulates results so the sample runs without
/// an external search API key.
/// </summary>
public static class WebSearchStub
{
    /// <summary>
    /// Creates the <c>web_search</c> <see cref="AIFunction"/>.
    /// </summary>
    public static AIFunction CreateFunction()
        => AIFunctionFactory.Create(
            Search,
            name: "web_search",
            description: "Search the web for current information. Returns a brief summary of results.");

    [Description("Search the web for current information. Returns a brief summary of results.")]
    private static string Search(
        [Description("The search query")] string query)
    {
        // Stub: return a deterministic fake result so the sample runs without a real API key.
        // In production, replace this with an HTTP call to your preferred search provider.
        return $"[Stub results for '{query}'] " +
               $"Found 3 relevant articles: (1) Overview of {query} — published today. " +
               $"(2) Recent developments in {query} — industry blog. " +
               $"(3) {query} explained — reference documentation.";
    }
}
