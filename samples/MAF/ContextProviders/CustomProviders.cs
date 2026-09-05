// CustomProviders.cs — two AIContextProvider subclasses for the ContextProviders sample.
//
// Demonstrates the AIContextProvider pattern with hand-rolled providers rather than MAF's
// built-in TodoProvider/AgentModeProvider, which expose tools dynamically via AIContext.Tools
// and are not direct drop-ins for this library's durable tool dispatch (see
// docs/how-to/MAF/individual-context-providers.md).
//
// TurnCounterProvider — stateful: increments a per-session LLM-call counter in StateBag
//                       and injects it as a system message on every step.
// DateTimeProvider    — stateless: injects the current UTC date/time on every step.
//                       Shows that providers do not have to use StateBag.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;

namespace ContextProviders;

/// <summary>
/// Tracks how many LLM calls have fired in this session across all turns.
/// Stores the counter under <c>"session.turn_count"</c> in <see cref="AgentSessionStateBag"/>
/// so the value survives worker restarts and continue-as-new transitions.
/// </summary>
public sealed class TurnCounterProvider : AIContextProvider
{
    private const string StateBagKey = "session.turn_count";

    /// <inheritdoc/>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        int count = 1;

        try
        {
            var agentContext = TemporalAgentContext.Current;
            var stateBag = agentContext.CurrentSession.StateBag;

            // Read the current counter (stored as a string to satisfy the reference-type
            // constraint on AgentSessionStateBag.SetValue<T>).
            if (stateBag.TryGetValue(StateBagKey,
                    out string? stored,
                    System.Text.Json.JsonSerializerOptions.Default)
                && int.TryParse(stored, out int existing))
            {
                count = existing + 1;
            }

            // Persist the incremented value back into the StateBag so it survives
            // across continue-as-new and worker restarts.
            stateBag.SetValue(StateBagKey, count.ToString(),
                System.Text.Json.JsonSerializerOptions.Default);
        }
        catch
        {
            // TemporalAgentContext is not available outside of an active agent activity
            // (e.g. in unit tests). Fall back to count = 1 so the message is still injected.
        }

        return new ValueTask<AIContext>(new AIContext
        {
            Messages =
            [
                new ChatMessage(
                    ChatRole.System,
                    $"[Context] This is LLM call #{count} in this session."),
            ],
        });
    }

}

/// <summary>
/// Injects the current UTC date/time as a system message on every LLM step.
/// Stateless — no <see cref="AgentSessionStateBag"/> access required.
/// </summary>
public sealed class DateTimeProvider : AIContextProvider
{
    /// <inheritdoc/>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<AIContext>(new AIContext
        {
            Messages =
            [
                new ChatMessage(
                    ChatRole.System,
                    $"[Context] Current UTC time: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}"),
            ],
        });
    }
}
