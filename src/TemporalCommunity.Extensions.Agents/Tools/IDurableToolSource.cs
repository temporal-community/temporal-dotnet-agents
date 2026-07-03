using System.Diagnostics.CodeAnalysis;
using TemporalCommunity.Extensions.Agents;

namespace TemporalCommunity.Extensions.Agents.Tools;

/// <summary>
/// Implemented by an <c>AIContextProvider</c> subclass to declare its durable tools at
/// registration time. The framework will register these tools as separate Temporal activities
/// (identical to calling <c>agent.AddTool</c> directly).
/// </summary>
/// <remarks>
/// <para>
/// Implement this interface on an <c>AIContextProvider</c> subclass to declare its
/// durable tools at registration time — or use the
/// <c>DurableAgentBuilder.AddContextProvider(AIContextProvider, IEnumerable{DurableToolRegistrationSpec})</c>
/// overload with an explicit <see cref="IEnumerable{DurableToolRegistrationSpec}"/> if you don't control
/// the provider type.
/// </para>
/// <para>
/// TODO: consider whether this interface should be promoted to a more prominent part of the
/// public API surface as the pattern matures. Currently <c>[Experimental("TA001")]</c> because
/// the shape may evolve.
/// </para>
/// </remarks>
[Experimental("TA001")]
public interface IDurableToolSource
{
    /// <summary>
    /// Returns the durable tool specs this provider contributes.
    /// Called once at registration time; the specs are passed to <c>AddToolCore</c>
    /// and registered as durable activities.
    /// </summary>
    IEnumerable<DurableToolRegistrationSpec> GetDurableTools();
}
