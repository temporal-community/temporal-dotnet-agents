using System.Runtime.CompilerServices;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Reference-identity <see cref="IEqualityComparer{T}"/> that compares elements with
/// <see cref="object.ReferenceEquals(object?, object?)"/> and hashes with
/// <see cref="RuntimeHelpers.GetHashCode(object?)"/>.
/// </summary>
/// <remarks>
/// A strongly-typed, allocation-free stand-in for the .NET 5+ BCL
/// <c>System.Collections.Generic.ReferenceEqualityComparer</c>, which is not available on
/// <c>netstandard2.1</c>. Used across both libraries as the comparer for identity-keyed
/// <see cref="HashSet{T}"/> / <see cref="Dictionary{TKey,TValue}"/> collections that key on
/// object reference rather than value equality (decorator-chain cycle detection, per-turn
/// metadata, mixed-pattern check caches). A single shared <see cref="Instance"/> is safe to
/// reuse because the comparer is stateless.
/// </remarks>
/// <typeparam name="T">The reference type being compared.</typeparam>
internal sealed class ReferenceComparer<T> : IEqualityComparer<T>
    where T : class
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly ReferenceComparer<T> Instance = new();

    private ReferenceComparer()
    {
    }

    /// <inheritdoc />
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    /// <inheritdoc />
    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
