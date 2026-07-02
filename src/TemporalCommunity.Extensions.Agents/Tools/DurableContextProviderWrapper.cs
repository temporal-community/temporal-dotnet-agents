using Microsoft.Agents.AI;

#pragma warning disable MAAI001 // experimental MAF AIContextProvider.InvokingContext/InvokedContext ctors; inventoried in Internal/ExperimentalApiSuppressions.cs

namespace TemporalCommunity.Extensions.Agents.Tools;

/// <summary>
/// Wraps an <c>AIContextProvider</c> with explicit <see cref="DurableToolRegistrationSpec"/> entries,
/// implementing <see cref="IDurableToolSource"/> so the framework registers the specs as durable
/// activities and strips provider-contributed tools from the aggregated context per-iteration.
/// </summary>
/// <remarks>
/// This type is internal. Callers use the
/// <c>DurableAgentBuilder.AddContextProvider(AIContextProvider, IEnumerable{DurableToolRegistrationSpec})</c>
/// overload, which creates this wrapper transparently when explicit specs are provided.
/// The wrapper is fully transparent: both <c>InvokingCoreAsync</c> and <c>InvokedCoreAsync</c>
/// delegate to the inner provider's <c>InvokingAsync</c> / <c>InvokedAsync</c> template-method
/// entry points, which invoke the inner's own <c>InvokingCoreAsync</c> / <c>InvokedCoreAsync</c>
/// without filtering or modifying their output. Stripping of <c>AIContext.Tools</c> happens in
/// <c>AgentActivities</c> per-iteration after the <c>IDurableToolSource</c> check.
/// </remarks>
#pragma warning disable TA001
internal sealed class DurableContextProviderWrapper : AIContextProvider, IDurableToolSource
#pragma warning restore TA001
{
    private readonly AIContextProvider _inner;
    private readonly IReadOnlyList<DurableToolRegistrationSpec> _specs;

    internal DurableContextProviderWrapper(
        AIContextProvider inner,
        IReadOnlyList<DurableToolRegistrationSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(specs);
        _inner = inner;
        _specs = specs;
    }

    /// <inheritdoc/>
    public IEnumerable<DurableToolRegistrationSpec> GetDurableTools() => _specs;

    /// <summary>
    /// Delegates to the inner provider's <c>InvokingAsync</c> template-method entry point,
    /// which invokes the inner's own <c>InvokingCoreAsync</c> (full delegation — filters,
    /// merging, and source stamping all happen inside the inner provider).
    /// </summary>
    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
        => _inner.InvokingAsync(context, cancellationToken);

    /// <summary>
    /// Delegates to the inner provider's <c>InvokedAsync</c> template-method entry point,
    /// which invokes the inner's own <c>InvokedCoreAsync</c> (full delegation — error
    /// handling and store filtering all happen inside the inner provider).
    /// </summary>
    protected override ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
        => _inner.InvokedAsync(context, cancellationToken);
}
