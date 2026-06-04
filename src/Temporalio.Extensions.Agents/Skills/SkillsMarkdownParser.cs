namespace Temporalio.Extensions.Agents.Skills;

/// <summary>
/// Result of a successful SKILL.md frontmatter parse.
/// </summary>
/// <param name="Name">The skill name from the <c>name:</c> frontmatter key.</param>
/// <param name="Description">The skill description from the <c>description:</c> frontmatter key.</param>
/// <param name="License">The optional license from the <c>license:</c> frontmatter key.</param>
/// <param name="Compatibility">The optional compatibility string from the <c>compatibility:</c> frontmatter key.</param>
internal sealed record ParsedSkillFile(
    string Name,
    string Description,
    string? License,
    string? Compatibility);

/// <summary>
/// Pure string parser for SKILL.md frontmatter. No file I/O — callers are responsible
/// for reading the raw content and stripping any BOM before passing it here.
/// </summary>
internal static class SkillsMarkdownParser
{
    /// <summary>
    /// Attempts to parse the YAML frontmatter of a SKILL.md file content string.
    /// </summary>
    /// <param name="content">
    /// The raw file content (BOM must already be stripped). The first line (after splitting
    /// on <c>\n</c> and trimming each line of trailing whitespace) must be exactly
    /// <c>---</c>. Leading blank lines are not accepted.
    /// </param>
    /// <returns>
    /// A <see cref="ParsedSkillFile"/> if the frontmatter is valid and contains both
    /// <c>name</c> and <c>description</c>; <see langword="null"/> otherwise.
    /// </returns>
    internal static ParsedSkillFile? TryParse(string content)
    {
        // Split on \n so that \r\n files produce lines with trailing \r that TrimEnd removes.
        var rawLines = content.Split('\n');

        // Build trimmed lines array — we need to TrimEnd BEFORE any comparison.
        var lines = new string[rawLines.Length];
        for (int i = 0; i < rawLines.Length; i++)
        {
            lines[i] = rawLines[i].TrimEnd();
        }

        // Line 0 must be exactly "---" — no leading blank lines accepted.
        if (lines.Length == 0 || lines[0] != "---")
        {
            return null;
        }

        // Collect frontmatter lines until the closing "---" delimiter.
        int closingIndex = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i] == "---")
            {
                closingIndex = i;
                break;
            }
        }

        // If no closing "---" found, the frontmatter is unclosed — malformed.
        if (closingIndex < 0)
        {
            return null;
        }

        string? name = null;
        string? description = null;
        string? license = null;
        string? compatibility = null;

        for (int i = 1; i < closingIndex; i++)
        {
            var line = lines[i];

            // Skip blank lines inside frontmatter.
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            // Non-empty line without a colon is malformed.
            int colonPos = line.IndexOf(':', StringComparison.Ordinal);
            if (colonPos < 0)
            {
                return null;
            }

            // Split on first ':' only; trim both key and value.
            var key = line[..colonPos].Trim();
            var value = line[(colonPos + 1)..].Trim();

            // First occurrence wins for all keys.
            switch (key)
            {
                case "name":
                    name ??= value;
                    break;
                case "description":
                    description ??= value;
                    break;
                case "license":
                    license ??= value;
                    break;
                case "compatibility":
                    compatibility ??= value;
                    break;
                // Unknown keys are silently ignored.
            }
        }

        // Both name and description are required.
        if (name is null || description is null)
        {
            return null;
        }

        return new ParsedSkillFile(name, description, license, compatibility);
    }
}
