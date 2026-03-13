namespace Temporalio.Extensions.Agents;

/// <summary>
/// Strips markdown code fences from LLM output before JSON deserialization.
/// Many models wrap structured output in <c>```json ... ```</c> blocks — this helper
/// extracts the raw JSON payload. Port of the Dapr Agent Framework's approach.
/// </summary>
internal static class MarkdownCodeFenceHelper
{
    /// <summary>
    /// Strips markdown code fences and extracts the first balanced JSON object or array.
    /// Returns the original text unchanged if no fences or balanced braces are found.
    /// </summary>
    public static string StripMarkdownCodeFences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var stripped = StripFences(text);
        return ExtractBalancedJson(stripped) ?? stripped;
    }

    private static string StripFences(string text)
    {
        var span = text.AsSpan().Trim();

        // Check for opening fence: ```json, ```JSON, or plain ```
        if (!span.StartsWith("```"))
            return text;

        // Find end of the opening fence line.
        int firstNewline = span.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        // Skip the opening fence line.
        var inner = span.Slice(firstNewline + 1);

        // Strip closing fence.
        if (inner.TrimEnd().EndsWith("```"))
        {
            int closingFence = inner.LastIndexOf("```");
            if (closingFence >= 0)
            {
                inner = inner.Slice(0, closingFence);
            }
        }

        return inner.Trim().ToString();
    }

    /// <summary>
    /// Finds the first balanced <c>{}</c> or <c>[]</c> in the input, respecting string escaping.
    /// Returns <see langword="null"/> if no balanced structure is found.
    /// </summary>
    private static string? ExtractBalancedJson(string text)
    {
        char openChar;
        char closeChar;

        int firstBrace = text.IndexOf('{');
        int firstBracket = text.IndexOf('[');

        if (firstBrace < 0 && firstBracket < 0)
            return null;

        int startIndex;
        if (firstBrace >= 0 && (firstBracket < 0 || firstBrace < firstBracket))
        {
            startIndex = firstBrace;
            openChar = '{';
            closeChar = '}';
        }
        else
        {
            startIndex = firstBracket;
            openChar = '[';
            closeChar = ']';
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == openChar)
                depth++;
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0)
                    return text.Substring(startIndex, i - startIndex + 1);
            }
        }

        return null;
    }
}
