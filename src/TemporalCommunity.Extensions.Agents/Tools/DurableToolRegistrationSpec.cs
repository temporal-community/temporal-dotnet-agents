using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Tools;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Specifies a tool and its per-tool options for registration via
/// <see cref="DurableAgentBuilder.AddContextProvider(AIContextProvider, IEnumerable{DurableToolRegistrationSpec}?)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-idempotent tools (code execution, file writes, external API calls) MUST set
/// <c>Configure = opts => opts.NoRetry()</c></b> to prevent double-execution on activity retry.
/// The default inherits an explicit worker-level <c>DefaultRetryPolicy</c>, or the library's
/// bounded five-attempt tool policy when the worker policy is unset.
/// </para>
/// </remarks>
public sealed record DurableToolRegistrationSpec(
    AIFunction Tool,
    Action<DurableToolOptions>? Configure = null);
