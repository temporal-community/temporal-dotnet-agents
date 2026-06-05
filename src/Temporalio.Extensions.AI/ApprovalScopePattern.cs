using System.Text.Json.Serialization;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Supported pattern-match strategies for approval scope argument matching.
/// </summary>
/// <remarks>
/// Serialized as a string (e.g., <c>"Exact"</c>, <c>"Glob"</c>, <c>"Regex"</c>) via
/// <see cref="PatternMatchTypeJsonConverter"/>. Integer values in submitted payloads are
/// rejected with a <see cref="System.Text.Json.JsonException"/> at the data converter
/// boundary before reaching workflow code.
/// </remarks>
[JsonConverter(typeof(PatternMatchTypeJsonConverter))]
public enum PatternMatchType
{
    /// <summary>
    /// String equality on the canonical argument string (case-sensitive, <see cref="System.StringComparison.Ordinal"/>).
    /// </summary>
    Exact,

    /// <summary>
    /// Unix glob pattern. <c>*</c> = any non-separator sequence, <c>**</c> = any sequence
    /// including <c>/</c>. Does not use OS path APIs or <c>FileSystemGlobbing</c>.
    /// </summary>
    Glob,

    /// <summary>
    /// .NET <see cref="System.Text.RegularExpressions.Regex"/> with a mandatory short timeout
    /// to prevent ReDoS.
    /// </summary>
    Regex
}

/// <summary>
/// Custom JSON converter for <see cref="PatternMatchType"/> that enforces string-only
/// serialization and rejects integer enum values.
/// </summary>
/// <remarks>
/// Wraps <see cref="JsonStringEnumConverter{TEnum}"/> with <c>allowIntegerValues: false</c>
/// so that external systems submitting <see cref="DurableApprovalDecision"/> payloads cannot
/// use numeric values for the pattern type. String values are deserialized
/// case-insensitively, so <c>"exact"</c>, <c>"Exact"</c>, and <c>"EXACT"</c> all map to
/// <see cref="PatternMatchType.Exact"/>.
/// </remarks>
public sealed class PatternMatchTypeJsonConverter : JsonStringEnumConverter<PatternMatchType>
{
    /// <summary>
    /// Initializes a new instance of <see cref="PatternMatchTypeJsonConverter"/> with
    /// case-insensitive string deserialization and integer value rejection.
    /// </summary>
    public PatternMatchTypeJsonConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Describes how a scope approval applies to a subset of tool calls identified by the
/// tool name and an optional argument pattern.
/// </summary>
/// <remarks>
/// Serialized as a plain JSON object:
/// <c>{ "type": "Glob", "parameter": "path", "pattern": "/tmp/*" }</c>.
/// No polymorphism or <c>$type</c> discriminators are needed.
/// </remarks>
public sealed class ApprovalScopePattern
{
    /// <summary>
    /// Matching strategy applied to the argument value or serialized argument JSON.
    /// </summary>
    public required PatternMatchType Type { get; init; }

    /// <summary>
    /// Name of the argument to match against. Must be a top-level key in
    /// <see cref="DurableToolContext.Arguments"/>. When <see langword="null"/>,
    /// the pattern is applied to the entire serialized arguments JSON string.
    /// </summary>
    public string? Parameter { get; init; }

    /// <summary>
    /// The match expression. Interpretation depends on <see cref="Type"/>.
    /// </summary>
    public required string Pattern { get; init; }
}
