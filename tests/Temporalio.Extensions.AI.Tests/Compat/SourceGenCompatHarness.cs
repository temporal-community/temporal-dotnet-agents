using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Exceptions;
using Temporalio.Extensions.AI.Session;
using Xunit;

namespace Temporalio.Extensions.AI.Tests.Compat;

/// <summary>
/// Reusable test fixture for round-tripping polymorphic durable history entries
/// (e.g., <c>DurableSessionEntry</c>) between a "frozen old worker" type set
/// and the current process's "new worker" type set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Purpose.</b> Every time a new <c>[JsonDerivedType]</c> registration is
/// added to one of the source-gen contexts (e.g., the upcoming
/// <c>"compaction-marker"</c> discriminator), there's a real risk that an
/// older worker — still on the previous build — will pull a workflow task
/// whose history includes the new discriminator. STJ raises a vague
/// <see cref="JsonException"/>; this harness exercises that path and asserts
/// the result surfaces as a typed
/// <see cref="DurableReplayCompatibilityException"/> identifying the missing
/// discriminator.
/// </para>
/// <para>
/// <b>How it simulates "old worker".</b>
/// <see cref="BuildFrozenContextSnapshot(string)"/> reads a JSON snapshot file
/// from <c>Snapshots/{snapshotName}/discriminators.json</c> that enumerates
/// the discriminators known to the version that produced the snapshot. The
/// harness then builds a <see cref="JsonSerializerOptions"/> with a
/// <see cref="IJsonTypeInfoResolver"/> modifier that strips any
/// <see cref="JsonDerivedType"/> whose discriminator is <i>not</i> in that
/// list — yielding an options set that behaves as the older build would have.
/// </para>
/// <para>
/// <b>Why the harness wraps in place of production.</b> Production wrapping
/// of <see cref="JsonException"/> → <see cref="DurableReplayCompatibilityException"/>
/// inside <c>DurableAIDataConverter</c> is owned by Step 3 of the maf-gap
/// plan. For Step 2 we keep the wrap inside the test helper so this harness
/// stays self-contained.
/// </para>
/// </remarks>
internal static class SourceGenCompatHarness
{
    /// <summary>
    /// Discriminator-mismatch substrings the harness looks for in
    /// <see cref="JsonException"/> messages to decide whether to wrap into a
    /// <see cref="DurableReplayCompatibilityException"/>. STJ phrasing has
    /// shifted across .NET versions, so we match generously on the keyword
    /// <c>"discriminator"</c> rather than a single fixed string.
    /// </summary>
    private static readonly string[] DiscriminatorMessageMarkers =
    {
        "discriminator",
        "polymorph",
    };

    /// <summary>
    /// Builds an "old worker" <see cref="JsonSerializerOptions"/> by reading
    /// a snapshot file under
    /// <c>Snapshots/{snapshotName}/discriminators.json</c> and stripping any
    /// <see cref="JsonDerivedType"/> registration whose discriminator is not
    /// listed in the snapshot.
    /// </summary>
    /// <param name="snapshotName">
    /// The snapshot directory name (e.g., <c>"v0_3"</c>). The directory must
    /// contain a <c>discriminators.json</c> file in the format described in
    /// <see cref="DiscriminatorSnapshot"/>.
    /// </param>
    /// <returns>
    /// A read-only <see cref="JsonSerializerOptions"/> that behaves like the
    /// older worker's <c>DurableAIJsonUtilities.DefaultOptions</c> would have.
    /// </returns>
    public static JsonSerializerOptions BuildFrozenContextSnapshot(string snapshotName)
    {
        if (string.IsNullOrWhiteSpace(snapshotName))
        {
            throw new ArgumentException(
                "Snapshot name must be provided.", nameof(snapshotName));
        }

        var snapshot = LoadSnapshot(snapshotName);
        var allowed = snapshot.BaseTypes.ToDictionary(
            kvp => ResolveBaseType(kvp.Key),
            kvp => new HashSet<string>(kvp.Value, StringComparer.Ordinal));

        // Start from the live options and clone — we want the same converters
        // and same source-gen resolver chain, only the polymorphic derived-type
        // sets get filtered.
        var options = new JsonSerializerOptions(DurableAIJsonUtilities.DefaultOptions);
        options.TypeInfoResolver = new FilteringResolver(options.TypeInfoResolver!, allowed);
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// Asserts that deserializing <paramref name="payload"/> via the
    /// <paramref name="oldOptions"/> raises a
    /// <see cref="DurableReplayCompatibilityException"/> whose
    /// <see cref="DurableReplayCompatibilityException.Discriminator"/> equals
    /// <paramref name="expectedDiscriminator"/>, while the same payload
    /// deserializes cleanly under <paramref name="newOptions"/>.
    /// </summary>
    /// <param name="newOptions">
    /// Options containing the new <c>[JsonDerivedType]</c> set (i.e., the
    /// current process's live options). Used as a sanity check: the payload
    /// itself must be valid under the new context.
    /// </param>
    /// <param name="oldOptions">
    /// Options simulating the lagging worker (typically produced by
    /// <see cref="BuildFrozenContextSnapshot(string)"/>).
    /// </param>
    /// <param name="payload">JSON string to deserialize.</param>
    /// <param name="expectedDiscriminator">
    /// The discriminator that should appear inside the typed exception's
    /// <see cref="DurableReplayCompatibilityException.Discriminator"/>.
    /// </param>
    public static void AssertReplayDeserialization(
        JsonSerializerOptions newOptions,
        JsonSerializerOptions oldOptions,
        string payload,
        string expectedDiscriminator)
    {
        ArgumentNullException.ThrowIfNull(newOptions);
        ArgumentNullException.ThrowIfNull(oldOptions);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(expectedDiscriminator);

        // 1) Sanity check: payload is valid under the current (new) context.
        //    We deserialize via the abstract base type to force polymorphic
        //    dispatch.
        var asEntry = JsonSerializer.Deserialize<DurableSessionEntry>(payload, newOptions);
        Assert.NotNull(asEntry);

        // 2) The same payload, deserialized under the frozen (old) options,
        //    must raise the typed compatibility exception — never a raw
        //    JsonException.
        var thrown = Assert.Throws<DurableReplayCompatibilityException>(() =>
            DeserializeWithWrap<DurableSessionEntry>(payload, oldOptions, expectedDiscriminator));

        Assert.Equal(expectedDiscriminator, thrown.Discriminator);
    }

    /// <summary>
    /// Deserializes <paramref name="payload"/> via <paramref name="options"/>
    /// and translates any discriminator-mismatch
    /// <see cref="JsonException"/> into a
    /// <see cref="DurableReplayCompatibilityException"/>.
    /// </summary>
    /// <typeparam name="T">Target base type for deserialization.</typeparam>
    /// <param name="payload">JSON string to deserialize.</param>
    /// <param name="options">Serializer options to use.</param>
    /// <param name="discriminatorHint">
    /// Discriminator value to surface in the wrapped exception. When the
    /// caller already knows which discriminator is missing (e.g., from the
    /// payload's <c>$type</c> field) this avoids parsing free-form STJ
    /// messages.
    /// </param>
    public static T? DeserializeWithWrap<T>(
        string payload,
        JsonSerializerOptions options,
        string discriminatorHint)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, options);
        }
        catch (JsonException ex) when (IsDiscriminatorMismatch(ex))
        {
            throw new DurableReplayCompatibilityException(
                discriminator: discriminatorHint,
                registeredContext: "DurableAIJsonUtilities.DefaultOptions (frozen snapshot)",
                suggestedAction:
                    $"Upgrade the lagging worker to a build that registers " +
                    $"'{discriminatorHint}' in its [JsonDerivedType] set.",
                innerException: ex);
        }
    }

    private static bool IsDiscriminatorMismatch(JsonException ex)
    {
        if (ex.Message is null)
        {
            return false;
        }

        foreach (var marker in DiscriminatorMessageMarkers)
        {
            if (ex.Message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static DiscriminatorSnapshot LoadSnapshot(string snapshotName)
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Compat", "Snapshots", snapshotName, "discriminators.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Snapshot file not found: {path}. " +
                "Make sure the snapshot JSON has CopyToOutputDirectory=PreserveNewest in the test csproj.",
                path);
        }

        var json = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<DiscriminatorSnapshot>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (snapshot is null || snapshot.BaseTypes is null)
        {
            throw new InvalidDataException(
                $"Snapshot {path} parsed to null or missing 'baseTypes'.");
        }

        return snapshot;
    }

    private static Type ResolveBaseType(string fullyQualifiedName)
    {
        // The base type lives in the Temporalio.Extensions.AI assembly. Walk
        // loaded assemblies first; fall back to Type.GetType for AQN inputs.
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(fullyQualifiedName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(t => t is not null);
        type ??= Type.GetType(fullyQualifiedName, throwOnError: false);

        if (type is null)
        {
            throw new InvalidOperationException(
                $"Unable to resolve type '{fullyQualifiedName}' referenced by snapshot. " +
                "Check the snapshot's baseTypes entry against the current assembly.");
        }

        return type;
    }

    /// <summary>
    /// Wire-format for snapshot files. The file should look like:
    /// <code>
    /// {
    ///   "snapshotName": "v0_3",
    ///   "baseTypes": {
    ///     "Temporalio.Extensions.AI.Session.DurableSessionEntry": ["ai_request", "ai_response"]
    ///   }
    /// }
    /// </code>
    /// </summary>
    private sealed class DiscriminatorSnapshot
    {
        public string? SnapshotName { get; set; }

        public Dictionary<string, List<string>> BaseTypes { get; set; } = new();
    }

    /// <summary>
    /// Wraps an inner <see cref="IJsonTypeInfoResolver"/> and, for each base
    /// type in <see cref="_allowed"/>, removes any
    /// <see cref="JsonDerivedType"/> whose discriminator is not in the
    /// allow-list. Used to simulate "this is what the old worker's
    /// PolymorphismOptions looked like".
    /// </summary>
    private sealed class FilteringResolver : IJsonTypeInfoResolver
    {
        private readonly IJsonTypeInfoResolver _inner;
        private readonly IReadOnlyDictionary<Type, HashSet<string>> _allowed;

        public FilteringResolver(
            IJsonTypeInfoResolver inner,
            IReadOnlyDictionary<Type, HashSet<string>> allowed)
        {
            _inner = inner;
            _allowed = allowed;
        }

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            var info = _inner.GetTypeInfo(type, options);
            if (info is null)
            {
                return null;
            }

            if (_allowed.TryGetValue(type, out var allowedDiscriminators) &&
                info.PolymorphismOptions is { } poly)
            {
                for (var i = poly.DerivedTypes.Count - 1; i >= 0; i--)
                {
                    var derived = poly.DerivedTypes[i];
                    var disc = derived.TypeDiscriminator as string;
                    if (disc is null || !allowedDiscriminators.Contains(disc))
                    {
                        poly.DerivedTypes.RemoveAt(i);
                    }
                }
            }

            return info;
        }
    }
}
