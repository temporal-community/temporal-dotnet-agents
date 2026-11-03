using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableHistoryProjectionValidatorTests
{
    [Fact]
    public void Validate_AllowsRemovingOlderCompleteTurns()
    {
        var oldTurn = CreateTurn("old");
        var currentTurn = CreateTurn("current");

        DurableHistoryProjectionValidator.Validate(
            oldTurn.Concat(currentTurn).ToArray(),
            currentTurn);
    }

    [Fact]
    public void Validate_RejectsSplitTurn()
    {
        var turn = CreateTurn("current");

        Assert.Throws<DurableHistoryProjectionException>(() =>
            DurableHistoryProjectionValidator.Validate(turn, [turn[0]]));
    }

    [Fact]
    public void Validate_RejectsRemovingLatestCompleteTurn()
    {
        var history = CreateTurn("current");

        Assert.Throws<DurableHistoryProjectionException>(() =>
            DurableHistoryProjectionValidator.Validate(history, []));
    }

    [Fact]
    public void Validate_RejectsReorderedOrDuplicatedEntries()
    {
        var firstTurn = CreateTurn("first");
        var currentTurn = CreateTurn("current");
        var history = firstTurn.Concat(currentTurn).ToArray();

        Assert.Throws<DurableHistoryProjectionException>(() =>
            DurableHistoryProjectionValidator.Validate(
                history,
                [currentTurn[0], firstTurn[0], currentTurn[1]]));
        Assert.Throws<DurableHistoryProjectionException>(() =>
            DurableHistoryProjectionValidator.Validate(
                history,
                [firstTurn[0], firstTurn[0], firstTurn[1], currentTurn[0], currentTurn[1]]));
    }

    private static DurableSessionEntry[] CreateTurn(string id)
    {
        var now = DateTimeOffset.UnixEpoch;
        var request = new DurableSessionRequest
        {
            EntryId = $"{id}-request",
            TurnId = id,
            CorrelationId = id,
            CreatedAt = now,
            Messages = [new ChatMessage(ChatRole.User, $"request-{id}")],
        };
        var response = new DurableSessionResponse
        {
            EntryId = $"{id}-response",
            TurnId = id,
            CorrelationId = id,
            CreatedAt = now.AddSeconds(1),
            Messages = [new ChatMessage(ChatRole.Assistant, $"response-{id}")],
        };
        return [request, response];
    }
}
