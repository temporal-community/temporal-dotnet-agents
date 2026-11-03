using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Estimates model input tokens for the complete effective request.
/// </summary>
/// <remarks>
/// Implementations must be deterministic and side-effect free. The <paramref name="options"/>
/// supplied to this method includes effective instructions, tool declarations, and structured
/// output schema when available. Register an implementation in worker DI to replace the default
/// conservative heuristic.
/// </remarks>
public interface IDurableChatTokenEstimator
{
    /// <summary>Returns the estimated input-token count for the messages and effective options.</summary>
    int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options);
}
