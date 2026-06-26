using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Extensions.Agents.Session;

namespace Temporalio.Extensions.Agents.Skills;

/// <summary>
/// An <see cref="AIContextProvider"/> that advertises available skills as a compact XML
/// index injected into each LLM call. The index is cached in the session
/// <see cref="AgentSessionStateBag"/> so it survives continue-as-new transitions and
/// the provider does not need to re-scan skill sources on every turn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design.</b> On the first call to <see cref="ProvideAIContextAsync"/>, the provider
/// awaits <see cref="SkillResolver.EnsureLoadedAsync"/> to build the full skill map,
/// sorts skill names alphabetically (OrdinalIgnoreCase), emits a compact XML index
/// containing name + description per skill, and writes the index to
/// <see cref="StateBagKey"/>. On subsequent calls the cached index is read from the
/// StateBag directly — no re-scan occurs.
/// </para>
/// <para>
/// <b>Index format.</b>
/// <code>
/// &lt;skills&gt;
///   &lt;skill&gt;&lt;name&gt;expense-report&lt;/name&gt;&lt;description&gt;File expense reports...&lt;/description&gt;&lt;/skill&gt;
/// &lt;/skills&gt;
/// </code>
/// (~100 tokens per skill). Full skill instructions are loaded on demand by the
/// <c>load_skill</c> tool.
/// </para>
/// <para>
/// <b>StateBag key.</b> Index stored under <see cref="StateBagKey"/>. Subject to the
/// 64 KB <c>CarriedStateBag</c> warning path — keep the number of registered skills
/// reasonable (typically &lt;50) to avoid bloat.
/// </para>
/// <para>
/// <b>File-skill drift.</b> <see cref="SkillResolver"/> re-materialises from file sources
/// on first use after a worker restart or continue-as-new. If the directory contents have
/// changed, the resolver reflects the new state while the StateBag still holds the old
/// index text — the prompt may advertise stale skills. File skill sources should be treated
/// as immutable for the lifetime of a session.
/// </para>
/// <para>
/// <b>Script stripping.</b> When scripts are disabled (<c>scriptsEnabled = false</c>),
/// the <c>load_skill</c> tool strips the <c>&lt;scripts&gt;…&lt;/scripts&gt;</c> block
/// from synthesized skill content (works for <see cref="AgentInlineSkill"/> and
/// <see cref="AgentClassSkill{TSelf}"/> which use synthesized XML; file-based skills use
/// raw Markdown where no XML tag exists). File skill authors should omit script
/// documentation from their SKILL.md files when script execution is disabled, or accept
/// that the model may see script information it cannot execute.
/// </para>
/// </remarks>
public sealed class SkillsContextProvider : AIContextProvider
{
    /// <summary>
    /// The StateBag key under which the compact skill index XML is stored.
    /// </summary>
    public const string StateBagKey = "temporal.skills_index";

    private readonly SkillResolver _resolver;
    private readonly bool _scriptsEnabled;

    /// <summary>
    /// Initializes a new instance of <see cref="SkillsContextProvider"/>.
    /// </summary>
    /// <param name="resolver">The skill resolver shared with the tool closures.</param>
    /// <param name="scriptsEnabled">
    /// Whether script execution is enabled. When <see langword="false"/>, the XML index
    /// does not mention script invocation.
    /// </param>
    internal SkillsContextProvider(SkillResolver resolver, bool scriptsEnabled)
        : base(provideInputMessageFilter: null,
               storeInputRequestMessageFilter: null,
               storeInputResponseMessageFilter: null)
    {
        _resolver = resolver;
        _scriptsEnabled = scriptsEnabled;
    }

    /// <inheritdoc/>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // Try to read cached index from StateBag first.
        string? indexXml = null;

        if (context.Session is TemporalAgentSession agentSession)
        {
            agentSession.StateBag.TryGetValue(
                StateBagKey, out indexXml, System.Text.Json.JsonSerializerOptions.Default);
        }

        if (string.IsNullOrEmpty(indexXml))
        {
            // First call: materialise the resolver and build the index.
            await _resolver.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            indexXml = BuildIndex(_resolver, _scriptsEnabled);

            // Persist to StateBag for carry-forward across turns and continue-as-new.
            if (context.Session is TemporalAgentSession session)
            {
                try
                {
                    session.StateBag.SetValue(
                        StateBagKey, indexXml, System.Text.Json.JsonSerializerOptions.Default);
                }
                catch (Exception ex)
                {
                    // StateBag.SetValue failed — continue without persisting. The injected
                    // note still provides value for the current call. Log at Debug for
                    // diagnosability without noise.
                    if (Temporalio.Activities.ActivityExecutionContext.HasCurrent)
                    {
                        Temporalio.Activities.ActivityExecutionContext.Current.Logger.LogDebug(
                            ex,
                            "SkillsContextProvider could not persist '{StateBagKey}' to the StateBag; continuing without carry-forward.",
                            StateBagKey);
                    }
                }
            }
        }

        return new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, indexXml)],
        };
    }

    /// <summary>
    /// Removes a single <c>&lt;scripts&gt;…&lt;/scripts&gt;</c> block from synthesized skill
    /// content. Used by the <c>load_skill</c> tool to strip script documentation when script
    /// execution is disabled so the model does not see tools it cannot call.
    /// </summary>
    /// <remarks>
    /// This helper works for <see cref="AgentInlineSkill"/> and <see cref="AgentClassSkill{TSelf}"/>
    /// which emit synthesized XML. File-based skills (<c>AgentFileSkill</c>) use raw Markdown — if
    /// the SKILL.md documents scripts in Markdown prose (not XML tags), this helper is a no-op and
    /// the model may still see script information.
    /// </remarks>
    /// <param name="content">The skill content string to strip.</param>
    /// <returns>
    /// The content with the scripts block removed, or the original string if no unambiguous
    /// block was found.
    /// </returns>
    internal static string StripScriptsSection(string content)
    {
        const string openTag = "<scripts>";
        const string closeTag = "</scripts>";

        // Step 1: find first <scripts>
        int start = content.IndexOf(openTag, StringComparison.Ordinal);
        if (start < 0)
        {
            return content;
        }

        // Step 2: find first </scripts>
        int end = content.IndexOf(closeTag, StringComparison.Ordinal);
        if (end < 0)
        {
            return content;
        }

        // Step 3: check for multiple <scripts> (ambiguous/nested)
        int secondOpen = content.IndexOf(openTag, start + 1, StringComparison.Ordinal);
        if (secondOpen >= 0)
        {
            return content;
        }

        // Step 4: check for multiple </scripts> (ambiguous/nested)
        int secondClose = content.IndexOf(closeTag, end + 1, StringComparison.Ordinal);
        if (secondClose >= 0)
        {
            return content;
        }

        // Step 5: sanity — start must precede end
        if (start >= end)
        {
            return content;
        }

        // Step 6: splice out [start … end + closeTag.Length], trim one leading/trailing newline
        int blockEnd = end + closeTag.Length;
        var result = content[..start] + content[blockEnd..];

        // Trim a single leading newline before the block (the newline just before <scripts>)
        if (result.Length > 0 && start > 0 && result[start - 1] == '\n')
        {
            result = result[..(start - 1)] + result[start..];
        }
        else
        {
            // Trim a single trailing newline after the block (the newline just after </scripts>)
            if (start < result.Length && result[start] == '\n')
            {
                result = result[..start] + result[(start + 1)..];
            }
        }

        return result;
    }

    private static string BuildIndex(SkillResolver resolver, bool scriptsEnabled)
    {
        var sortedNames = resolver.GetSortedNames();
        var all = resolver.GetAll();

        var sb = new StringBuilder();
        sb.AppendLine("<skills>");

        foreach (var name in sortedNames)
        {
            if (!all.TryGetValue(name, out var skill))
            {
                continue;
            }

            var description = EscapeXml(skill.Frontmatter.Description ?? string.Empty);
            var escapedName = EscapeXml(name);

            sb.Append("  <skill><name>").Append(escapedName)
              .Append("</name><description>").Append(description)
              .AppendLine("</description></skill>");
        }

        sb.Append("</skills>");
        return sb.ToString();
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}
