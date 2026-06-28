namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Thrown when an older worker / older serializer context attempts to
/// deserialize a payload that contains a <c>$type</c> polymorphic
/// discriminator the local
/// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> doesn't
/// know about — i.e., a rolling-deploy / mixed-fleet version skew where a
/// newer worker wrote a payload that the local worker can't fully model.
/// </summary>
/// <remarks>
/// <para>
/// This exception exists to give users an actionable, typed error in place of
/// an opaque <see cref="System.Text.Json.JsonException"/> bubbling out as a
/// vague workflow-task failure. The wrapping is deliberately narrow: only
/// discriminator-mismatch failures are wrapped, so unrelated JSON errors keep
/// their original type.
/// </para>
/// <para>
/// The structured payload (<see cref="Discriminator"/>,
/// <see cref="RegisteredContext"/>, <see cref="SuggestedAction"/>) lets test
/// code and operators pinpoint which discriminator is missing without parsing
/// free-form exception messages.
/// </para>
/// <para>
/// Typical recovery: upgrade the lagging worker to a build that registers the
/// missing discriminator. The <see cref="SuggestedAction"/> text always points
/// at this remediation.
/// </para>
/// </remarks>
public sealed class DurableReplayCompatibilityException : DurableConfigurationException
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DurableReplayCompatibilityException"/> class.
    /// </summary>
    /// <param name="discriminator">
    /// The unknown <c>$type</c> discriminator value extracted from the
    /// incoming payload. Wire-format constants (e.g., <c>"compaction-marker"</c>)
    /// are safe to surface verbatim — they do not contain user data.
    /// </param>
    /// <param name="registeredContext">
    /// Name of the
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>
    /// (or composite resolver chain) the deserializer was using when the
    /// failure occurred. Used to communicate "which worker build is lagging".
    /// </param>
    /// <param name="suggestedAction">
    /// Human-readable remediation text pointing the operator at the fix
    /// (usually "upgrade the worker to a build that registers
    /// <c>&lt;discriminator&gt;</c>").
    /// </param>
    /// <param name="innerException">
    /// The originating <see cref="System.Text.Json.JsonException"/> (or other
    /// low-level deserialization error) used to detect the mismatch. May be
    /// <see langword="null"/> when callers construct this exception directly
    /// for a known-bad scenario.
    /// </param>
    public DurableReplayCompatibilityException(
        string discriminator,
        string registeredContext,
        string suggestedAction,
        Exception? innerException = null)
        : base(
            BuildMessage(discriminator, registeredContext, suggestedAction),
            innerException)
    {
        Discriminator = discriminator;
        RegisteredContext = registeredContext;
        SuggestedAction = suggestedAction;
    }

    /// <summary>
    /// Gets the unknown <c>$type</c> discriminator value found in the payload
    /// (e.g., <c>"compaction-marker"</c>). Never user-supplied data — these
    /// are wire-format constants declared via <c>[JsonDerivedType]</c>
    /// attributes.
    /// </summary>
    public string Discriminator { get; init; }

    /// <summary>
    /// Gets the name of the
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> (or
    /// resolver chain) that was active when the deserialization failed.
    /// Useful for distinguishing "AI library context" vs. "Agents library
    /// context" failures.
    /// </summary>
    public string RegisteredContext { get; init; }

    /// <summary>
    /// Gets human-readable remediation guidance — typically: upgrade the
    /// lagging worker build so it knows about <see cref="Discriminator"/>.
    /// </summary>
    public string SuggestedAction { get; init; }

    private static string BuildMessage(
        string discriminator,
        string registeredContext,
        string suggestedAction)
    {
        return $"Unknown polymorphic discriminator '{discriminator}' for context " +
            $"'{registeredContext}'. This is a rolling-deploy / mixed-fleet skew: " +
            $"a newer worker wrote history that the current worker cannot fully " +
            $"deserialize. {suggestedAction}";
    }
}
