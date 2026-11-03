namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Message-index range occupied by one prior complete turn in an activity's model input.
/// </summary>
/// <param name="StartIndex">Inclusive starting index.</param>
/// <param name="EndIndex">Exclusive ending index.</param>
/// <param name="IsProtected">Whether this turn must remain in the model context.</param>
public sealed record DurableChatTurnRange(int StartIndex, int EndIndex, bool IsProtected);
