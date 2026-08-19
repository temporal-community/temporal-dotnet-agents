using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

public sealed class ApprovalScopeRevocationTests
{
    [Fact]
    public void Revoke_RemovesOnlyTheRequestedGrant()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var state = ApprovalScopeCoordinator.WriteSessionScopeToStateBag(
            null,
            "write_file",
            pattern: null,
            matchAllArguments: true,
            grantId: "grant-a",
            expiresAt: now.AddHours(1),
            actor: "reviewer",
            reason: "ticket-a",
            originatingRequestId: "request-a",
            grantedAt: now,
            maxSessionScopeRecords: 10,
            maxSessionScopeBytes: 16 * 1024,
            sessionId: "session",
            logger: NullLogger.Instance);
        state = ApprovalScopeCoordinator.WriteSessionScopeToStateBag(
            state,
            "delete_file",
            pattern: null,
            matchAllArguments: true,
            grantId: "grant-b",
            expiresAt: now.AddHours(1),
            actor: "reviewer",
            reason: "ticket-b",
            originatingRequestId: "request-b",
            grantedAt: now,
            maxSessionScopeRecords: 10,
            maxSessionScopeBytes: 16 * 1024,
            sessionId: "session",
            logger: NullLogger.Instance);

        var (updated, removed) = ApprovalScopeCoordinator.RevokeSessionScopeFromStateBag(
            state,
            "grant-a");
        var bag = AgentSessionStateBag.Deserialize(updated!.Value);

        Assert.True(removed);
        Assert.False(ApprovalScopeHelpers.TryMatchScope(
            "write_file", new Dictionary<string, object?>(), bag,
            "temporal.approval_scopes.session", now, out _));
        Assert.True(ApprovalScopeHelpers.TryMatchScope(
            "delete_file", new Dictionary<string, object?>(), bag,
            "temporal.approval_scopes.session", now, out var retained));
        Assert.Equal("grant-b", retained!.GrantId);
    }
}
