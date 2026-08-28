using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Common;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class ContinueAsNewInputTests
{
    [Fact]
    public void CreateContinueAsNewInput_PreservesAllFrozenSettings()
    {
        var retryPolicy = new RetryPolicy { MaximumAttempts = 7 };
        var toolOptions = new Dictionary<string, ActivityOptions>
        {
            ["write"] = new() { StartToCloseTimeout = TimeSpan.FromSeconds(41) },
        };
        var interceptorOptions = new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromSeconds(42),
        };
        var interceptorToolOptions = new Dictionary<string, ActivityOptions>
        {
            ["write"] = new() { StartToCloseTimeout = TimeSpan.FromSeconds(43) },
        };
        var skippedTools = new[] { "skip" };
        var approvalTools = new[] { "approve" };
        var approvalTimeouts = new Dictionary<string, TimeSpan>
        {
            ["approve"] = TimeSpan.FromHours(4),
        };
        var manifestWithoutFingerprint = new TemporalCommunity.Extensions.AI.Internal.DurableToolsetManifest
        {
            ManifestVersion = TemporalCommunity.Extensions.AI.Internal.DurableToolsetManifest.CurrentVersion,
            ToolsetIds = [],
            Members = [],
            Fingerprint = string.Empty,
        };
        var manifest = manifestWithoutFingerprint with
        {
            Fingerprint = TemporalCommunity.Extensions.AI.Internal.DurableToolsetManifestFingerprint.Create(
                manifestWithoutFingerprint),
        };

        var original = new DurableChatWorkflowInput
        {
            TimeToLive = TimeSpan.FromDays(3),
            CarriedHistory =
            [
                new DurableSessionRequest
                {
                    CorrelationId = "old",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                },
            ],
            ActivityTimeout = TimeSpan.FromMinutes(11),
            HeartbeatTimeout = TimeSpan.FromMinutes(3),
            RetryPolicy = retryPolicy,
            ApprovalTimeout = TimeSpan.FromHours(8),
            ApprovalResolutionHistory = [new DurableApprovalDecision { RequestId = "old" }],
            EnableSearchAttributes = true,
            MaxEntryCount = 321,
            HistoryReducerKey = "reducer-v2",
            OriginalCreatedAt = DateTimeOffset.UnixEpoch,
            ToolActivityOptions = toolOptions,
            InterceptorActivityOptions = interceptorOptions,
            InterceptorToolActivityOptions = interceptorToolOptions,
            InterceptorSkippedTools = skippedTools,
            RequiresApprovalTools = approvalTools,
            ToolApprovalTimeouts = approvalTimeouts,
            MaxToolCallsPerTurn = 17,
            MaximumConsecutiveErrorsPerRequest = 5,
            IncludeDetailedErrors = true,
            ToolsetManifest = manifest,
        };
        var carriedHistory = new List<DurableSessionEntry>
        {
            new DurableSessionResponse
            {
                CorrelationId = "new",
                CreatedAt = DateTimeOffset.UnixEpoch,
            },
        };
        IReadOnlyList<DurableApprovalDecision> approvals =
            [new DurableApprovalDecision { RequestId = "new" }];
        var createdAt = new DateTimeOffset(2026, 8, 10, 1, 2, 3, TimeSpan.Zero);

        var actual = DurableChatWorkflowBase<ChatResponse>.CreateContinueAsNewInput(
            original, carriedHistory, approvals, createdAt);

        Assert.NotSame(original, actual);
        Assert.Equal(original.TimeToLive, actual.TimeToLive);
        Assert.Same(carriedHistory, actual.CarriedHistory);
        Assert.Equal(original.ActivityTimeout, actual.ActivityTimeout);
        Assert.Equal(original.HeartbeatTimeout, actual.HeartbeatTimeout);
        Assert.Same(retryPolicy, actual.RetryPolicy);
        Assert.Equal(original.ApprovalTimeout, actual.ApprovalTimeout);
        Assert.Same(approvals, actual.ApprovalResolutionHistory);
        Assert.Equal(original.EnableSearchAttributes, actual.EnableSearchAttributes);
        Assert.Equal(original.MaxEntryCount, actual.MaxEntryCount);
        Assert.Equal("reducer-v2", actual.HistoryReducerKey);
        Assert.Equal(createdAt, actual.OriginalCreatedAt);
        Assert.Same(toolOptions, actual.ToolActivityOptions);
        Assert.Same(interceptorOptions, actual.InterceptorActivityOptions);
        Assert.Same(interceptorToolOptions, actual.InterceptorToolActivityOptions);
        Assert.Same(skippedTools, actual.InterceptorSkippedTools);
        Assert.Same(approvalTools, actual.RequiresApprovalTools);
        Assert.Same(approvalTimeouts, actual.ToolApprovalTimeouts);
        Assert.Equal(original.MaxToolCallsPerTurn, actual.MaxToolCallsPerTurn);
        Assert.Equal(
            original.MaximumConsecutiveErrorsPerRequest,
            actual.MaximumConsecutiveErrorsPerRequest);
        Assert.Equal(original.IncludeDetailedErrors, actual.IncludeDetailedErrors);
        Assert.Same(manifest, actual.ToolsetManifest);
    }
}
