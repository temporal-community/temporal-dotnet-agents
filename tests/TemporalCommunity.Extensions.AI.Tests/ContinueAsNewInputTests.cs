using System.Text.Json;
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
    public void MissingHistorySummarizerSettings_UseFrozenDefaults()
    {
        var restored = JsonSerializer.Deserialize<DurableChatWorkflowInput>(
            """{"defaultChatClientKey":"primary"}""",
            DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(restored);
        Assert.Equal(8192, restored.HistorySummarizerMaxContextTokens);
        Assert.Equal(1024, restored.HistorySummarizerOutputTokenReserve);
        Assert.Equal(DurableHistoryCompactionStrategy.None, restored.HistoryCompactionStrategy);
    }

    [Fact]
    public void CompactionStrategyAndRecoveryState_SurviveWorkflowInputSerialization()
    {
        var input = new DurableChatWorkflowInput
        {
            HistoryCompactionStrategy = DurableHistoryCompactionStrategy.TrimRecentTurns,
            HistoryRecovery = new DurableHistoryRecoveryState
            {
                IsBlocked = true,
                BlockReason = "compaction-failed",
                RetryCountThisIncident = 2,
                AcceptedOperationIds = ["retry-1", "retry-2"],
                LastOutcome = "retry-failed",
            },
        };

        var payload = JsonSerializer.Serialize(input, DurableAIJsonUtilities.DefaultOptions);
        var restored = JsonSerializer.Deserialize<DurableChatWorkflowInput>(
            payload,
            DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(restored);
        Assert.Equal(DurableHistoryCompactionStrategy.TrimRecentTurns, restored.HistoryCompactionStrategy);
        Assert.NotNull(restored.HistoryRecovery);
        Assert.True(restored.HistoryRecovery.IsBlocked);
        Assert.Equal("compaction-failed", restored.HistoryRecovery.BlockReason);
        Assert.Equal(2, restored.HistoryRecovery.RetryCountThisIncident);
        Assert.Equal(["retry-1", "retry-2"], restored.HistoryRecovery.AcceptedOperationIds);
        Assert.Equal("retry-failed", restored.HistoryRecovery.LastOutcome);
    }

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
        var historyRecovery = new DurableHistoryRecoveryState
        {
            IsBlocked = true,
            BlockReason = "configuration-invalid",
            RetryCountThisIncident = 3,
            TrimUsedThisIncident = true,
            AcceptedOperationIds = ["retry-1", "retry-2", "retry-3", "trim-1"],
            LastOutcome = "retry-failed",
        };
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
            SessionEpoch = "session-epoch",
            SessionIdleDeadline = DateTimeOffset.UnixEpoch.AddDays(3),
            CompletedTurnCount = 41,
            MaxTerminalOutcomeCount = 17,
            MaxTerminalOutcomeBytes = 8192,
            MaximumTransportPayloadSizeBytes = 16384,
            PayloadCodecExpansionAllowanceBytes = 512,
            HistoryEventSoftLimit = 12_000,
            HistoryEventReserveLimit = 14_000,
            HistorySizeSoftLimitBytes = 12 * 1024 * 1024,
            HistorySizeReserveLimitBytes = 14 * 1024 * 1024,
            MaxConcurrentTurnJoiners = 8,
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
            ContextReducerKey = "context-reducer-v1",
            DefaultChatClientKey = "profile-v1",
            HistorySummarizerClientKey = "summary-profile-v1",
            HistorySummarizerMaxContextTokens = 16_384,
            HistorySummarizerOutputTokenReserve = 2_048,
            HistoryCompactionStrategy = DurableHistoryCompactionStrategy.SummarizeOldestTurns,
            HistoryRecovery = historyRecovery,
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
        IReadOnlyList<global::TemporalCommunity.Extensions.AI.Internal.DurableTerminalTurnOutcome>
            terminalOutcomes =
            [
                new()
                {
                    RequestId = "request-1",
                    AdmissionFingerprint = "fingerprint-1",
                    Output = JsonDocument.Parse("\"output\"").RootElement.Clone(),
                    ResponseEntry = JsonSerializer.SerializeToElement<DurableSessionEntry>(
                        new DurableSessionResponse
                        {
                            CorrelationId = "request-1",
                            CreatedAt = DateTimeOffset.UnixEpoch,
                        },
                        DurableAIJsonUtilities.DefaultOptions),
                },
            ];
        var createdAt = new DateTimeOffset(2026, 8, 10, 1, 2, 3, TimeSpan.Zero);
        var renewedIdleDeadline = createdAt.AddDays(3);

        var actual = DurableChatWorkflowBase<ChatResponse>.CreateContinueAsNewInput(
            original,
            carriedHistory,
            approvals,
            createdAt,
            terminalOutcomes,
            completedTurnCount: 42,
            sessionIdleDeadline: renewedIdleDeadline);

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
        Assert.Equal("context-reducer-v1", actual.ContextReducerKey);
        Assert.Equal("profile-v1", actual.DefaultChatClientKey);
        Assert.Equal("summary-profile-v1", actual.HistorySummarizerClientKey);
        Assert.Equal(16_384, actual.HistorySummarizerMaxContextTokens);
        Assert.Equal(2_048, actual.HistorySummarizerOutputTokenReserve);
        Assert.Equal(
            DurableHistoryCompactionStrategy.SummarizeOldestTurns,
            actual.HistoryCompactionStrategy);
        Assert.Same(historyRecovery, actual.HistoryRecovery);
        Assert.Equal(createdAt, actual.OriginalCreatedAt);
        Assert.Equal("session-epoch", actual.SessionEpoch);
        Assert.Equal(renewedIdleDeadline, actual.SessionIdleDeadline);
        Assert.Equal(42, actual.CompletedTurnCount);
        Assert.Equal(17, actual.MaxTerminalOutcomeCount);
        Assert.Equal(8192, actual.MaxTerminalOutcomeBytes);
        Assert.Equal(16384, actual.MaximumTransportPayloadSizeBytes);
        Assert.Equal(512, actual.PayloadCodecExpansionAllowanceBytes);
        Assert.Equal(12_000, actual.HistoryEventSoftLimit);
        Assert.Equal(14_000, actual.HistoryEventReserveLimit);
        Assert.Equal(12 * 1024 * 1024, actual.HistorySizeSoftLimitBytes);
        Assert.Equal(14 * 1024 * 1024, actual.HistorySizeReserveLimitBytes);
        Assert.Equal(8, actual.MaxConcurrentTurnJoiners);
        Assert.Same(terminalOutcomes, actual.CarriedTerminalOutcomes);
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
