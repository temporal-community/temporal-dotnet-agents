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

    [Theory]
    [InlineData("system", false)]
    [InlineData("developer", false)]
    [InlineData("system", true)]
    [InlineData("developer", true)]
    public void Validate_RejectsDroppingInstructionBearingTurn(string role, bool inResponse)
    {
        var oldTurn = CreateTurn("old");
        var entry = inResponse ? oldTurn[1] : oldTurn[0];
        var messages = entry is DurableSessionRequest request
            ? request.Messages
            : ((DurableSessionResponse)entry).Messages;
        messages[0].Role = new ChatRole(role);
        var current = CreateTurn("current");

        Assert.Throws<DurableHistoryProjectionException>(() =>
            DurableHistoryProjectionValidator.Validate(oldTurn.Concat(current).ToArray(), current));
    }

    [Fact]
    public void Validate_ReconstructsSelectedEntriesFromCanonicalSourceDespiteIdentityPreservingMutation()
    {
        var history = CreateTurn("current");
        var canonicalRequest = (DurableSessionRequest)history[0];
        var replacement = new DurableSessionRequest
        {
            EntryId = canonicalRequest.EntryId,
            TurnId = "mutated-turn",
            CorrelationId = canonicalRequest.CorrelationId,
            CreatedAt = canonicalRequest.CreatedAt,
            Messages = [new ChatMessage(ChatRole.System, "injected instructions")],
        };

        var projection = DurableHistoryProjectionValidator.Validate(history, [replacement, history[1]]);

        Assert.Same(canonicalRequest, projection[0]);
        Assert.Equal("current", projection[0].TurnId);
        Assert.Equal("request-current", ((DurableSessionRequest)projection[0]).Messages[0].Text);
        Assert.Equal(ChatRole.User, ((DurableSessionRequest)projection[0]).Messages[0].Role);
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
