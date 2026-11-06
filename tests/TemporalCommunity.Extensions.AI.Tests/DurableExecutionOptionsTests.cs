using TemporalCommunity.Extensions.AI;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableExecutionOptionsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var options = new DurableExecutionOptions();

        Assert.Null(options.TaskQueue);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ActivityTimeout);
        Assert.Null(options.RetryPolicy);
        Assert.Equal("chat-", options.WorkflowIdPrefix);
        Assert.Equal(TimeSpan.FromDays(14), options.SessionTimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(2), options.HeartbeatTimeout);
        Assert.Equal(128, options.MaxTerminalOutcomeCount);
        Assert.Equal(1024 * 1024, options.MaxTerminalOutcomeBytes);
        Assert.Equal(1024 * 1024, options.MaximumTransportPayloadSizeBytes);
        Assert.Equal(0, options.PayloadCodecExpansionAllowanceBytes);
        Assert.Equal(40_000, options.HistoryEventSoftLimit);
        Assert.Equal(45_000, options.HistoryEventReserveLimit);
        Assert.Equal(40 * 1024 * 1024, options.HistorySizeSoftLimitBytes);
        Assert.Equal(45 * 1024 * 1024, options.HistorySizeReserveLimitBytes);
        Assert.Equal(32, options.MaxConcurrentTurnJoiners);
        Assert.Equal(8192, options.HistorySummarizerMaxContextTokens);
        Assert.Equal(1024, options.HistorySummarizerOutputTokenReserve);
        Assert.Equal(DurableInferenceTelemetryMode.ApplicationOwned, options.InferenceTelemetryMode);
    }

    [Fact]
    public void Validate_RejectsUnknownInferenceTelemetryMode()
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "test-queue",
            InferenceTelemetryMode = (DurableInferenceTelemetryMode)999,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Contains(nameof(DurableExecutionOptions.InferenceTelemetryMode), exception.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenTaskQueueIsNull()
    {
        var options = new DurableExecutionOptions();

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("TaskQueue", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenTaskQueueIsEmpty()
    {
        var options = new DurableExecutionOptions { TaskQueue = "" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("TaskQueue", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenTaskQueueIsWhitespace()
    {
        var options = new DurableExecutionOptions { TaskQueue = "   " };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("TaskQueue", ex.Message);
    }

    [Fact]
    public void Validate_SucceedsWhenTaskQueueIsSet()
    {
        var options = new DurableExecutionOptions { TaskQueue = "my-queue" };

        options.Validate(); // Should not throw
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        var retryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 3 };
        var options = new DurableExecutionOptions
        {
            TaskQueue = "test-queue",
            ActivityTimeout = TimeSpan.FromMinutes(10),
            RetryPolicy = retryPolicy,
            WorkflowIdPrefix = "custom-",
            SessionTimeToLive = TimeSpan.FromDays(7),
            HeartbeatTimeout = TimeSpan.FromMinutes(5),
        };

        Assert.Equal("test-queue", options.TaskQueue);
        Assert.Equal(TimeSpan.FromMinutes(10), options.ActivityTimeout);
        Assert.Same(retryPolicy, options.RetryPolicy);
        Assert.Equal("custom-", options.WorkflowIdPrefix);
        Assert.Equal(TimeSpan.FromDays(7), options.SessionTimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(5), options.HeartbeatTimeout);
    }

    [Fact]
    public void DefaultChatClientKey_IsNullByDefault()
    {
        var options = new DurableExecutionOptions();
        Assert.Null(options.DefaultChatClientKey);
    }

    [Fact]
    public void DefaultChatClientKey_CanBeSet()
    {
        var options = new DurableExecutionOptions { DefaultChatClientKey = "gpt-4o" };
        Assert.Equal("gpt-4o", options.DefaultChatClientKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptyHistorySummarizerClientProfile(string key)
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "queue",
            DefaultHistorySummarizerClientKey = key,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Contains(nameof(DurableExecutionOptions.DefaultHistorySummarizerClientKey), exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptyDefaultChatClientProfile(string key)
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "queue",
            DefaultChatClientKey = key,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Contains(nameof(DurableExecutionOptions.DefaultChatClientKey), exception.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenActivityTimeoutIsZero()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", ActivityTimeout = TimeSpan.Zero };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("ActivityTimeout", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenActivityTimeoutIsNegative()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", ActivityTimeout = TimeSpan.FromSeconds(-1) };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("ActivityTimeout", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenHeartbeatTimeoutIsZero()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", HeartbeatTimeout = TimeSpan.Zero };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("HeartbeatTimeout", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenSessionTimeToLiveIsZero()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", SessionTimeToLive = TimeSpan.Zero };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("SessionTimeToLive", ex.Message);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1024, -1)]
    [InlineData(1024, 1024)]
    public void Validate_RejectsInvalidTransportPayloadBudget(int maximumBytes, int expansionAllowanceBytes)
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "q",
            MaximumTransportPayloadSizeBytes = maximumBytes,
            PayloadCodecExpansionAllowanceBytes = expansionAllowanceBytes,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Contains(
            maximumBytes <= 0
                ? nameof(options.MaximumTransportPayloadSizeBytes)
                : nameof(options.PayloadCodecExpansionAllowanceBytes),
            exception.Message);
    }

    [Fact]
    public void Validate_RejectsUnboundedActivityRetryPolicy()
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "q",
            RetryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 0 },
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Contains(nameof(options.RetryPolicy), exception.Message);
        Assert.Contains("positive MaximumAttempts", exception.Message);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1024, -1)]
    [InlineData(1024, 1024)]
    public void Validate_RejectsInvalidHistorySummarizerBudget(
        int maxContextTokens,
        int outputTokenReserve)
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "queue",
            HistorySummarizerMaxContextTokens = maxContextTokens,
            HistorySummarizerOutputTokenReserve = outputTokenReserve,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Contains("HistorySummarizer", exception.Message);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    public void Validate_RejectsInvalidHistoryEventThresholds(int softLimit, int reserveLimit)
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "q",
            HistoryEventSoftLimit = softLimit,
            HistoryEventReserveLimit = reserveLimit,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Contains(nameof(options.HistoryEventSoftLimit), exception.Message);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    public void Validate_RejectsInvalidHistorySizeThresholds(int softLimit, int reserveLimit)
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "q",
            HistorySizeSoftLimitBytes = softLimit,
            HistorySizeReserveLimitBytes = reserveLimit,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Contains(nameof(options.HistorySizeSoftLimitBytes), exception.Message);
    }

    [Fact]
    public void Validate_RejectsNonPositiveTurnJoinerLimit()
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "q",
            MaxConcurrentTurnJoiners = 0,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Contains(nameof(options.MaxConcurrentTurnJoiners), exception.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenApprovalTimeoutIsZero()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", ApprovalTimeout = TimeSpan.Zero };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("ApprovalTimeout", ex.Message);
    }

    // ── MaxEntryCount ──────────────────────────────────────────────────

    [Fact]
    public void MaxEntryCount_DefaultsTo1000()
    {
        var options = new DurableExecutionOptions();
        Assert.Equal(1000, options.MaxEntryCount);
    }

    [Fact]
    public void MaxEntryCount_CanBeSet()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", MaxEntryCount = 500 };
        Assert.Equal(500, options.MaxEntryCount);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Validate_ThrowsWhenMaxEntryCountCannotRetainACompleteTurn(int maxEntryCount)
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", MaxEntryCount = maxEntryCount };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxEntryCount", ex.Message);
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(-1, 1024)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 4095)]
    public void Validate_RejectsInvalidTerminalOutcomeLedgerBounds(int count, int bytes)
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "q",
            MaxTerminalOutcomeCount = count,
            MaxTerminalOutcomeBytes = bytes,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Contains(
            count <= 0 ? nameof(options.MaxTerminalOutcomeCount) : nameof(options.MaxTerminalOutcomeBytes),
            exception.Message);
    }

    [Fact]
    public void Validate_AcceptsMinimumTerminalOutcomeLedgerByteBudget()
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "q",
            MaxTerminalOutcomeBytes = 4096,
        };

        options.Validate();
    }

    // ── MaxToolCallsPerTurn (S-T1-1) ───────────────────────────────────

    [Fact]
    public void MaxToolCallsPerTurn_DefaultsTo20()
    {
        var options = new DurableExecutionOptions();
        Assert.Equal(20, options.MaxToolCallsPerTurn);
    }

    [Fact]
    public void Validate_ThrowsWhenMaxToolCallsPerTurnIsZero()
    {
        // A non-positive loop bound makes the durable dispatch loop body never run, so the
        // chat call would never fire. Validate() must reject it up front.
        var options = new DurableExecutionOptions { TaskQueue = "q", MaxToolCallsPerTurn = 0 };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxToolCallsPerTurn", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenMaxToolCallsPerTurnIsNegative()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", MaxToolCallsPerTurn = -1 };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxToolCallsPerTurn", ex.Message);
    }

    [Fact]
    public void Validate_SucceedsWhenMaxToolCallsPerTurnIsPositive()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", MaxToolCallsPerTurn = 1 };

        options.Validate(); // Should not throw
    }

    [Fact]
    public void Validate_ThrowsWhenMaximumConsecutiveErrorsPerRequestIsNegative()
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "q",
            MaximumConsecutiveErrorsPerRequest = -1,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains(nameof(DurableExecutionOptions.MaximumConsecutiveErrorsPerRequest), ex.Message);
    }
}
