using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;

namespace TemporalCommunity.Extensions.Agents.Approvals;

/// <summary>
/// Public static helpers for working with approval scope records stored in
/// <see cref="AgentSessionStateBag"/>. Custom interceptors can call
/// <see cref="TryMatchScope"/> to read scope records without parsing the StateBag manually.
/// </summary>
public static class ApprovalScopeHelpers
{
    // Maximum glob pattern/input length before treating as "no match".
    private const int MaxGlobPatternLength = 4096;
    private const int MaxGlobInputLength = 16384;

    /// <summary>
    /// Attempts to find a matching <see cref="ApprovalScopeRecord"/> in the specified StateBag key.
    /// </summary>
    /// <param name="toolName">The name of the tool being invoked.</param>
    /// <param name="arguments">
    /// The tool arguments. Used for pattern matching when <see cref="ApprovalScopePattern.Parameter"/>
    /// is set. Must not be null; pass an empty dictionary for parameterless tools.
    /// </param>
    /// <param name="bag">The session StateBag snapshot. When <see langword="null"/>, returns false.</param>
    /// <param name="storeKey">
    /// The StateBag key to read scope records from (e.g. <c>"temporal.approval_scopes.session"</c>).
    /// </param>
    /// <param name="match">
    /// When the method returns <see langword="true"/>, the first matching record; otherwise <see langword="null"/>.
    /// </param>
    /// <returns><see langword="true"/> when a matching scope record was found; otherwise <see langword="false"/>.</returns>
    public static bool TryMatchScope(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        AgentSessionStateBag? bag,
        string storeKey,
        out ApprovalScopeRecord? match)
    {
        match = null;

        if (bag is null)
            return false;

        List<ApprovalScopeRecord>? records = null;
        try
        {
            bag.TryGetValue<List<ApprovalScopeRecord>>(storeKey, out records, TemporalAgentJsonUtilities.DefaultOptions);
        }
        catch (Exception ex)
        {
            // Malformed cached scope state — treat as no match, do not throw.
            NullLoggerFactory.Instance.CreateLogger(nameof(ApprovalScopeHelpers)).LogWarning(
                ex,
                "ApprovalScopeHelpers: Failed to deserialize scope records from StateBag key '{StoreKey}'. Treating as no match.",
                storeKey);
            return false;
        }

        if (records is null || records.Count == 0)
            return false;

        foreach (var record in records)
        {
            try
            {
                if (IsMatchingRecord(record, toolName, arguments))
                {
                    match = record;
                    return true;
                }
            }
            catch (Exception ex)
            {
                // A bad individual record must not fail the entire scope check.
                NullLoggerFactory.Instance.CreateLogger(nameof(ApprovalScopeHelpers)).LogWarning(
                    ex,
                    "ApprovalScopeHelpers: Error evaluating scope record for tool '{ToolName}'. Skipping record.",
                    toolName);
            }
        }

        return false;
    }

    // ── Internal helpers ────────────────────────────────────────────────────

    private static bool IsMatchingRecord(
        ApprovalScopeRecord record,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        // Tool-name match: case-insensitive (consistent with DurableFunctionRegistry).
        if (!string.Equals(record.ToolName, toolName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Null pattern = wildcard: match any call of this tool regardless of arguments.
        if (record.Pattern is null)
            return true;

        var pattern = record.Pattern;

        // Determine the subject string to match against.
        string subject;
        if (pattern.Parameter is null)
        {
            // Null parameter = match against canonical JSON of the whole arguments dict.
            subject = SerializeArgumentsCanonically(arguments);
        }
        else
        {
            // Match against the single named argument value.
            if (!arguments.TryGetValue(pattern.Parameter, out var argValue))
                return false; // argument key not present → no match

            subject = ConvertArgumentValueToString(argValue);
        }

        return ApplyPatternMatch(subject, pattern);
    }

    private static bool ApplyPatternMatch(string subject, ApprovalScopePattern pattern)
    {
        return pattern.Type switch
        {
            PatternMatchType.Exact => string.Equals(subject, pattern.Pattern, StringComparison.Ordinal),
            PatternMatchType.Glob => GlobMatch(subject, pattern.Pattern),
            PatternMatchType.Regex => RegexMatch(subject, pattern.Pattern),
            _ => false,
        };
    }

    private static bool GlobMatch(string input, string globPattern)
    {
        if (globPattern.Length > MaxGlobPatternLength || input.Length > MaxGlobInputLength)
        {
            // Overlong input/pattern treated as no match.
            return false;
        }

        return GlobMatchIterative(input, globPattern);
    }

    /// <summary>
    /// Iterative glob matcher. Supports:
    /// <list type="bullet">
    ///   <item><c>**</c> — matches any sequence including <c>/</c></item>
    ///   <item><c>*</c> — matches any sequence NOT containing <c>/</c></item>
    ///   <item>literal characters — exact match</item>
    /// </list>
    /// No escaping, character classes, or brace expansion. No OS path handling.
    /// </summary>
    private static bool GlobMatchIterative(string input, string pattern)
    {
        // Use DP approach: dp[i] = true if pattern[0..i-1] matches some prefix of input.
        // Extend to full string match by checking dp[patLen] at input end.

        var patLen = pattern.Length;
        var inpLen = input.Length;

        // dp[i][j] = does pattern[0..j-1] match input[0..i-1]?
        // We'll use a rolling two-row approach to save space, but for simplicity
        // use a 2D boolean array (patterns are bounded to 4096, inputs to 16384).

        // Actually implement iterative DP with two rows.
        var prev = new bool[patLen + 1];
        var curr = new bool[patLen + 1];

        prev[0] = true; // empty pattern matches empty input

        // Pre-fill: pattern prefix consisting only of ** can match empty input.
        for (var j = 1; j <= patLen; j++)
        {
            if (pattern[j - 1] == '*' && j >= 2 && pattern[j - 2] == '*')
            {
                // ** segment — check if what preceded this star also matched empty
                prev[j] = prev[j - 1];
            }
            else if (pattern[j - 1] == '*' && (j < 2 || pattern[j - 2] != '*'))
            {
                // Single * at start can match empty non-/ sequence.
                prev[j] = prev[j - 1];
            }
            else
            {
                prev[j] = false;
            }
        }

        for (var i = 1; i <= inpLen; i++)
        {
            curr[0] = false; // non-empty input cannot match empty pattern

            for (var j = 1; j <= patLen; j++)
            {
                var pc = pattern[j - 1];
                var ic = input[i - 1];

                if (pc == '*')
                {
                    // Check whether this is ** (double star).
                    var isDoubleStar = j >= 2 && pattern[j - 2] == '*';
                    // Also check ahead: if next char in pattern is also *, treat as double star.
                    var nextIsAlsoStar = j < patLen && pattern[j] == '*';

                    if (isDoubleStar || nextIsAlsoStar)
                    {
                        // ** matches any character including /.
                        // Option 1: consume current input char and stay at same pattern position.
                        // Option 2: skip this pattern char (don't consume input).
                        curr[j] = curr[j - 1] || prev[j];
                    }
                    else
                    {
                        // Single * — matches any char except /.
                        if (ic == '/')
                        {
                            // Single * cannot cross /.
                            curr[j] = curr[j - 1];
                        }
                        else
                        {
                            // Option 1: consume input char and keep * (greedy).
                            // Option 2: skip this pattern char.
                            curr[j] = prev[j] || curr[j - 1];
                        }
                    }
                }
                else
                {
                    // Literal character match.
                    curr[j] = prev[j - 1] && pc == ic;
                }
            }

            // Swap rows.
            (prev, curr) = (curr, prev);
        }

        return prev[patLen];
    }

    private static bool RegexMatch(string input, string regexPattern)
    {
        try
        {
            return Regex.IsMatch(input, regexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            // Invalid regex syntax — treat as no match.
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            // ReDoS protection — treat as no match.
            return false;
        }
    }

    /// <summary>
    /// Converts an argument value to a string for pattern matching.
    /// </summary>
    private static string ConvertArgumentValueToString(object? value)
    {
        return value switch
        {
            null => "null",
            string s => s,
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.String => je.GetString() ?? string.Empty,
                JsonValueKind.Object or JsonValueKind.Array => je.GetRawText(),
                _ => je.GetRawText(),
            },
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    /// <summary>
    /// Serializes the arguments dictionary to canonical JSON with keys sorted recursively
    /// using <see cref="StringComparer.Ordinal"/>. Array order is preserved.
    /// </summary>
    internal static string SerializeArgumentsCanonically(IReadOnlyDictionary<string, object?> arguments)
    {
        // Serialize the sorted dictionary to JSON.
        var sortedDict = SortDictionaryKeys(arguments);
        return JsonSerializer.Serialize(sortedDict, DurableAIJsonUtilities.DefaultOptions);
    }

    private static Dictionary<string, object?> SortDictionaryKeys(IReadOnlyDictionary<string, object?> dict)
    {
        // Sort keys using StringComparer.Ordinal.
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in dict)
        {
            sorted[kvp.Key] = NormalizeValue(kvp.Value);
        }
        return new Dictionary<string, object?>(sorted);
    }

    private static object? NormalizeValue(object? value)
    {
        return value switch
        {
            JsonElement je => NormalizeJsonElement(je),
            IReadOnlyDictionary<string, object?> d => SortDictionaryKeys(d),
            _ => value,
        };
    }

    private static object? NormalizeJsonElement(JsonElement je)
    {
        return je.ValueKind switch
        {
            JsonValueKind.Object => NormalizeJsonObject(je),
            JsonValueKind.Array => NormalizeJsonArray(je),
            _ => je,
        };
    }

    private static Dictionary<string, object?> NormalizeJsonObject(JsonElement je)
    {
        var dict = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in je.EnumerateObject())
        {
            dict[prop.Name] = NormalizeJsonElement(prop.Value);
        }
        return new Dictionary<string, object?>(dict);
    }

    private static List<object?> NormalizeJsonArray(JsonElement je)
    {
        var list = new List<object?>(je.GetArrayLength());
        foreach (var item in je.EnumerateArray())
        {
            list.Add(NormalizeJsonElement(item));
        }
        return list;
    }
}
