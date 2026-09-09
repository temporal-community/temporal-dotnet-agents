// CustomProviders.cs — two AIContextProvider subclasses for the ContextProviders sample.
//
// Demonstrates the AIContextProvider pattern with hand-rolled providers rather than MAF's
// built-in TodoProvider/AgentModeProvider, which expose tools dynamically via AIContext.Tools
// and are not direct drop-ins for this library's durable tool dispatch (see
// docs/how-to/MAF/context-providers.md).
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
        // The InvokingContext carries the durable session — use it. Do NOT reach for
        // TemporalAgentContext.Current here: context providers run BEFORE the activity
        // establishes that context, so it throws and the counter silently never advances.
        // Fail loudly rather than silently counting 1 forever. Inside the durable activity the
        // session is always a TemporalAgentSession; anything else means the provider is being
        // driven outside the supported path, and a frozen counter would look like working code.
        if (context.Session is not TemporalAgentSession session)
        {
            throw new InvalidOperationException(
                $"{nameof(TurnCounterProvider)} requires a {nameof(TemporalAgentSession)}, but got " +
                $"'{context.Session?.GetType().Name ?? "null"}'. Session-scoped provider state lives " +
                "in the durable session's StateBag.");
        }

        var count = 1;
        {
            var stateBag = session.StateBag;

            // Stored as a string to satisfy the reference-type constraint on
            // AgentSessionStateBag.SetValue<T>.
            if (stateBag.TryGetValue(StateBagKey,
                    out string? stored,
                    System.Text.Json.JsonSerializerOptions.Default)
                && int.TryParse(stored, out var existing))
            {
                count = existing + 1;
            }

            // The activity re-serializes the bag after the step, so this survives worker
            // restarts and continue-as-new.
            stateBag.SetValue(StateBagKey, count.ToString(),
                System.Text.Json.JsonSerializerOptions.Default);

            // Sample-only: makes the provider's effect visible on the console. A real provider
            // would not write to stdout from inside an activity.
            Console.WriteLine($"[TurnCounter] LLM call #{count}");
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
