using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Extensions.Agents.Session;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// An <see cref="AIContextProvider"/> that computes a working-set summary from the
/// accumulated <see cref="ChatMessage"/> history and injects it as a compact context note
/// into each LLM call. Stores the computed working-set in the session's
/// <see cref="Microsoft.Agents.AI.AgentSessionStateBag"/> so it survives continue-as-new.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design.</b> The working-set is computed as a pure function over the session history:
/// file paths mentioned in assistant/tool messages (code-fence language hints and bare
/// paths matching common extensions) are extracted, deduplicated, and sorted. A compact
/// summary note listing the most-recently-referenced files is injected into the LLM
/// context as a system <see cref="ChatMessage"/> before each LLM call.
/// </para>
/// <para>
/// <b>StateBag key.</b> The serialized working-set is stored under
/// <see cref="StateBagKey"/> so downstream providers and tools can read it.
/// </para>
/// <para>
/// <b>External-store sessions limitation.</b> When <c>UseExternalStoreMode</c> is active,
/// the workflow strips message payloads from in-workflow history entries and only passes the
/// current-turn messages to the step activity. In that case the computed working-set only
/// reflects the current turn, not the full session history. This is a known limitation of
/// the initial implementation; a future iteration can load from
/// <c>IAgentHistoryStore</c> to close the gap.
/// </para>
/// <para>
/// <b>Determinism.</b> This provider is a pure function over the message list supplied by
/// the framework — no I/O, no randomness. Output is deterministic for the same input,
/// which means it is safe to call per LLM step without replay concerns.
/// </para>
/// </remarks>
public sealed class WorkingSetContextProvider : AIContextProvider
{
    /// <summary>
    /// The key under which the working-set JSON is stored in the session StateBag.
    /// </summary>
    public const string StateBagKey = "temporal.working_set";

    /// <summary>
    /// Maximum number of file paths to include in the working-set note. Paths beyond this
    /// limit are dropped (most-recently-seen paths win when the window overflows).
    /// </summary>
    public int MaxPaths { get; set; } = 20;

    /// <summary>
    /// When <see langword="true"/>, the injected note is omitted and only the StateBag is
    /// updated. Use to piggyback working-set tracking without adding visible context notes.
    /// </summary>
    public bool SilentMode { get; set; }

    /// <summary>
    /// Initializes a new instance of <see cref="WorkingSetContextProvider"/>.
    /// </summary>
    public WorkingSetContextProvider()
        : base(provideInputMessageFilter: null,
               storeInputRequestMessageFilter: null,
               storeInputResponseMessageFilter: null)
    {
    }

    /// <inheritdoc/>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var messages = context.AIContext.Messages;
        if (messages is null || !messages.Any())
        {
            return new ValueTask<AIContext>(new AIContext());
        }

        // Extract file paths from the history as a pure deterministic function.
        var paths = ExtractFilePaths(messages, MaxPaths);

        // Persist the working-set to the StateBag via the session on the InvokingContext.
        // Providers run inside RunDurableAgentStepAsync; TemporalAgentContext.Current is set
        // by InvokeAgentToolAsync (a different activity) and is not available here.
        // Accessing the StateBag directly through the session avoids that dependency.
        if (paths.Count > 0)
        {
            try
            {
                var csv = string.Join(",", paths);
                if (context.Session is TemporalAgentSession agentSession)
                {
                    agentSession.StateBag.SetValue(
                        StateBagKey, csv, System.Text.Json.JsonSerializerOptions.Default);
                }
            }
            catch (Exception ex)
            {
                // Session not a TemporalAgentSession (e.g. in tests) or StateBag.SetValue
                // failed — continue without persisting the working-set. The injected note
                // still provides value. Log at Debug for diagnosability without noise.
                if (Temporalio.Activities.ActivityExecutionContext.HasCurrent)
                {
                    Temporalio.Activities.ActivityExecutionContext.Current.Logger.LogDebug(
                        ex,
                        "WorkingSetContextProvider could not persist '{StateBagKey}' to the StateBag; continuing without carry-forward.",
                        StateBagKey);
                }
            }
        }

        if (SilentMode || paths.Count == 0)
        {
            return new ValueTask<AIContext>(new AIContext());
        }

        // Build a compact system note that gives the LLM visibility into which files are
        // most recently active in the session.
        var sb = new StringBuilder();
        sb.Append("## Working set\nRecently referenced files/paths in this session:\n");
        foreach (var path in paths)
        {
            sb.Append("- ").AppendLine(path);
        }

        return new ValueTask<AIContext>(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, sb.ToString())],
        });
    }

    /// <summary>
    /// Extracts file paths from message text content using heuristics:
    /// <list type="number">
    /// <item>Code-fence opening lines: <c>```lang\npath/to/file.ext</c></item>
    /// <item>Tokens that look like file paths (contain <c>/</c> or <c>\</c> and have a
    /// common file extension such as <c>.cs</c>, <c>.py</c>, <c>.ts</c>, etc.).</item>
    /// </list>
    /// Returns paths in most-recently-seen order, capped at <paramref name="maxPaths"/>.
    /// </summary>
    internal static IReadOnlyList<string> ExtractFilePaths(
        IEnumerable<ChatMessage> messages,
        int maxPaths)
    {
        // Use a linked-set pattern: seen tracks uniqueness, ordered preserves recency.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var msg in messages)
        {
            if (msg.Role != ChatRole.Assistant && msg.Role != ChatRole.Tool)
            {
                continue;
            }

            foreach (var content in msg.Contents)
            {
                if (content is TextContent tc)
                {
                    ExtractFromText(tc.Text, seen, ordered);
                }
            }
        }

        // Return most-recently-seen paths (last in list = most recent).
        if (ordered.Count <= maxPaths)
        {
            return ordered.AsReadOnly();
        }

        // Keep the last maxPaths entries (most recent window).
        return ordered.GetRange(ordered.Count - maxPaths, maxPaths).AsReadOnly();
    }

    private static void ExtractFromText(
        string? text,
        HashSet<string> seen,
        List<string> ordered)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Scan line by line.
        var lines = text.AsSpan();
        int start = 0;
        bool inCodeFence = false;
        bool nextLineIsPath = false;

        while (start < lines.Length)
        {
            int end = lines[start..].IndexOf('\n');
            ReadOnlySpan<char> line;
            if (end < 0)
            {
                line = lines[start..];
                start = lines.Length;
            }
            else
            {
                line = lines[start..(start + end)];
                start += end + 1;
            }

            // Trim trailing \r.
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            var trimmed = line.TrimStart();

            // Code-fence detection: ```<lang>
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeFence)
                {
                    inCodeFence = false;
                    nextLineIsPath = false;
                }
                else
                {
                    inCodeFence = true;
                    // The first non-empty line inside a code fence is often a file path hint.
                    nextLineIsPath = true;
                }

                continue;
            }

            if (inCodeFence && nextLineIsPath)
            {
                nextLineIsPath = false;
                var candidate = trimmed.ToString().Trim();
                if (LooksLikeFilePath(candidate))
                {
                    AddPath(candidate, seen, ordered);
                }
                continue;
            }

            // Token scan: look for path-shaped tokens anywhere on the line.
            ScanTokensForPaths(line.ToString(), seen, ordered);
        }
    }

    private static void ScanTokensForPaths(
        string line,
        HashSet<string> seen,
        List<string> ordered)
    {
        // Split on whitespace and punctuation that wouldn't appear in a path.
        var separators = new[] { ' ', '\t', ',', ';', '(', ')', '[', ']', '{', '}', '"', '\'', '<', '>' };
        var tokens = line.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            // Strip backticks from inline code.
            var t = token.Trim('`');
            if (LooksLikeFilePath(t))
            {
                AddPath(t, seen, ordered);
            }
        }
    }

    private static bool LooksLikeFilePath(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 3)
        {
            return false;
        }

        // Must contain a path separator.
        bool hasSlash = candidate.Contains('/', StringComparison.Ordinal)
                     || candidate.Contains('\\', StringComparison.Ordinal);
        if (!hasSlash)
        {
            return false;
        }

        // Must have a recognized file extension.
        var dot = candidate.LastIndexOf('.');
        if (dot < 0 || dot == candidate.Length - 1)
        {
            return false;
        }

        var ext = candidate[(dot + 1)..];
        return s_knownExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static void AddPath(string path, HashSet<string> seen, List<string> ordered)
    {
        if (!seen.Add(path))
        {
            // Move to end (most recent wins).
            ordered.Remove(path);
        }

        ordered.Add(path);
    }

    // Common file extensions to recognize as file paths. Conservative set — prefer
    // precision over recall to avoid false positives.
    private static readonly string[] s_knownExtensions =
    [
        "cs", "csx",
        "py", "pyi",
        "ts", "tsx",
        "js", "jsx", "mjs", "cjs",
        "go",
        "rs",
        "java",
        "kt",
        "rb",
        "php",
        "cpp", "cc", "cxx", "c", "h", "hpp",
        "swift",
        "dart",
        "elm",
        "ex", "exs",
        "hs",
        "lua",
        "r",
        "sql",
        "sh", "bash", "zsh",
        "ps1",
        "yaml", "yml",
        "json", "jsonc",
        "xml",
        "toml",
        "ini", "cfg", "conf",
        "env",
        "md", "mdx",
        "txt",
        "dockerfile",
        "makefile",
        "csproj", "vbproj", "fsproj", "sln", "slnx",
        "props", "targets",
    ];
}
