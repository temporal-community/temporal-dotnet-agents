using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.Agents.Approvals;

/// <summary>Supported pattern-match strategies for approval scope argument matching.</summary>
/// <remarks>
/// Serialized as a string and rejects numeric enum values at the data-converter boundary.
/// </remarks>
[JsonConverter(typeof(PatternMatchTypeJsonConverter))]
public enum PatternMatchType
{
    /// <summary>Case-sensitive ordinal string equality.</summary>
    Exact,

    /// <summary>Unix glob pattern.</summary>
    Glob,

    /// <summary>.NET regular expression with a bounded execution timeout.</summary>
    Regex,
}

/// <summary>Enforces string-only serialization for <see cref="PatternMatchType"/>.</summary>
internal sealed class PatternMatchTypeJsonConverter : JsonStringEnumConverter<PatternMatchType>
{
    /// <summary>Initializes the converter with integer values disabled.</summary>
    public PatternMatchTypeJsonConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Describes how a reusable agent approval applies to a subset of tool calls identified by a
/// tool name and optional argument pattern.
/// </summary>
public sealed class ApprovalScopePattern
{
    /// <summary>Matching strategy applied to the selected argument value or complete arguments JSON.</summary>
    public required PatternMatchType Type { get; init; }

    /// <summary>
    /// Optional top-level argument name. When <see langword="null"/>, the pattern applies to
    /// the complete serialized arguments object.
    /// </summary>
    public string? Parameter { get; init; }

    /// <summary>The match expression.</summary>
    public required string Pattern { get; init; }
}
