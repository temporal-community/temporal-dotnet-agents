using System.Text.Json;

namespace TemporalCommunity.Extensions.AI.Internal;

internal sealed record DurableTerminalTurnOutcome
{
    public required string RequestId { get; init; }

    public required string AdmissionFingerprint { get; init; }

    public string? TurnInputFingerprint { get; init; }

    public JsonElement? Output { get; init; }

    public JsonElement? ResponseEntry { get; init; }

    public DurableTerminalTurnFailure? Failure { get; init; }
}

internal sealed record DurableTerminalTurnFailure
{
    public required string ErrorType { get; init; }

    public required string Message { get; init; }

    public required bool NonRetryable { get; init; }
}
