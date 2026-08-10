using System.Text.Json;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Participates in the transactional rollback owned by
/// <see cref="DurableChatWorkflowBase{TOutput}.RunTurnAsync"/>.
/// </summary>
/// <remarks>
/// The base workflow captures and restores this state while its serialized turn gate is held.
/// Implementations must keep both operations deterministic and free of workflow commands.
/// </remarks>
internal interface IDurableTurnRollbackParticipant
{
    /// <summary>Captures derived workflow state immediately before a turn begins.</summary>
    JsonElement? CaptureTurnRollbackState();

    /// <summary>Restores derived workflow state after that turn fails.</summary>
    void RestoreTurnRollbackState(JsonElement? state);
}
