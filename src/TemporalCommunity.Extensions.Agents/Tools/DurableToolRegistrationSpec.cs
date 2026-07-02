using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.Agents.Tools;

/// <summary>
/// Specifies a tool and its per-tool options for registration via
/// <see cref="DurableAgentBuilder.AddContextProvider(AIContextProvider, IEnumerable{DurableToolRegistrationSpec}?)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-idempotent tools (code execution, file writes, external API calls) MUST set
/// <c>Configure = opts => opts.NoRetry()</c></b> to prevent double-execution on activity retry.
/// The default inherits the worker-level <c>DefaultRetryPolicy</c> (potentially unlimited retries).
/// </para>
/// </remarks>
public sealed record DurableToolRegistrationSpec(
    AIFunction Tool,
    Action<DurableToolOptions>? Configure = null);
