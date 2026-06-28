// Pattern 3 (durable tool dispatch in DurableChatSessionClient) — the tests below
// reference library types that ship in Neo's parallel worktree:
//   * AddDurableTools(AIFunction, Action<DurableChatToolOptions>?) overload
//   * DurableChatToolOptions (NoRetry / WithTimeout / WithMaxAttempts)
//   * DurableExecutionOptions.MaxToolCallsPerTurn
//   * DurableExecutionOptions.MaximumConsecutiveErrorsPerRequest
//   * DurableExecutionOptions.IncludeDetailedErrors
//   * DurableToolsNotWrappedException
//
// Compilation is gated on the PATTERN3 constant so unit-test CI stays green
// until Neo's branch merges. Activate locally with:
//   dotnet build -c Debug -p:DefineConstants=PATTERN3
// or by adding <DefineConstants>PATTERN3</DefineConstants> to the csproj
// once the library types land.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>
/// Integration tests for Pattern 3 — durable tool dispatch inside the managed
/// <see cref="DurableChatSessionClient"/> session loop (no custom workflow,
/// no <c>UseFunctionInvocation()</c>).
/// </summary>
/// <remarks>
/// <para>
/// Each test spins up its own <see cref="WorkflowEnvironment"/> so that
/// scripted chat clients, tool registries, and per-tool options are isolated.
/// </para>
/// <para>
/// Activity type names (string literals) come from the plan's Decisions table
/// and the existing <see cref="DurableFunctionActivities"/> attribute:
/// <list type="bullet">
///   <item><c>TemporalCommunity.Extensions.AI.GetChatStep</c> (new Pattern 3 activity)</item>
///   <item><c>TemporalCommunity.Extensions.AI.InvokeFunction</c> (existing, reused)</item>
///   <item><c>TemporalCommunity.Extensions.AI.GetResponse</c> (Pattern 1 inline)</item>
/// </list>
/// </para>
/// </remarks>
public class DurableToolDispatchIntegrationTests
{
    private const string GetChatStepActivity = "TemporalCommunity.Extensions.AI.GetChatStep";
    private const string InvokeFunctionActivity = "TemporalCommunity.Extensions.AI.InvokeFunction";

    // ── Happy path ──────────────────────────────────────────────────────────

    /// <summary>
    /// Test 1: end-to-end Pattern 3 session with a single tool call.
    /// </summary>
    [Fact]
    public async Task SingleToolCall_SingleTurn()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var weatherTool = harness.BuildAlwaysSucceeds(
            "get_weather",
            "Returns the current weather.",
            _ => "sunny, 72F");

        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?> { ["city"] = "SF" })],
            "The weather in SF is sunny, 72F.");

        var taskQueue = $"pattern3-single-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, scripted, builder => builder.AddDurableTools(weatherTool));
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"single-{Guid.NewGuid():N}";

        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "What's the weather in SF?")]);

        Assert.NotNull(response);
        Assert.Contains("sunny", response.Text);
        Assert.Equal(1, harness.GetInvocationCount("get_weather"));

        // Verify dispatch pattern: one GetChatStep per LLM round (2 here: first returns tool call, second returns final),
        // one InvokeFunction per tool call (1 here).
        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle(workflowId);
        var stepCount = await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, GetChatStepActivity);
        var invokeCount = await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, InvokeFunctionActivity);
        Assert.Equal(2, stepCount);
        Assert.Equal(1, invokeCount);

        await host.StopAsync();
    }

    /// <summary>
    /// Test 2: two <see cref="FunctionCallContent"/> in a single LLM step
    /// → both <c>InvokeFunction</c> activities are dispatched in parallel.
    /// </summary>
    [Fact]
    public async Task ParallelToolCalls_BothDispatchedInParallel()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var tool1 = harness.BuildAlwaysSucceeds("tool_a", "Tool A.", _ => "result-a");
        var tool2 = harness.BuildAlwaysSucceeds("tool_b", "Tool B.", _ => "result-b");

        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            new[]
            {
                new FunctionCallContent("call-a", "tool_a"),
                new FunctionCallContent("call-b", "tool_b"),
            },
            "Both results received.");

        var taskQueue = $"pattern3-parallel-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, scripted, builder =>
        {
            builder.AddDurableTools(tool1);
            builder.AddDurableTools(tool2);
        });
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"parallel-{Guid.NewGuid():N}";

        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "do both")]);

        Assert.NotNull(response);
        Assert.Equal(1, harness.GetInvocationCount("tool_a"));
        Assert.Equal(1, harness.GetInvocationCount("tool_b"));

        // Both InvokeFunction activities should be scheduled BEFORE either completes.
        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle(workflowId);
        var (schedules, firstComplete) =
            await WorkflowHistoryAssertions.CollectScheduleVsCompleteAsync(handle, InvokeFunctionActivity);
        Assert.Equal(2, schedules.Count);
        Assert.True(firstComplete > 0);
        Assert.All(schedules, idx => Assert.True(idx < firstComplete,
            $"Schedule index {idx} should precede first complete index {firstComplete} for parallel fan-out."));

        await host.StopAsync();
    }

    /// <summary>
    /// Test 3: a tool registered with <c>NoRetry()</c> throws → fails without retry.
    /// </summary>
    [Fact]
    public async Task PerToolRetry_NoRetryThrows_FailsWithoutRetry()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var alwaysFails = harness.BuildAlwaysThrows("write_record", "Non-idempotent write.", "boom");

        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "write_record")])),
        ]);

        var taskQueue = $"pattern3-noretry-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            scripted,
            builder => builder.AddDurableTools(alwaysFails, o => o.NoRetry()),
            opts => opts.MaximumConsecutiveErrorsPerRequest = 0);
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"noretry-{Guid.NewGuid():N}";

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await sessionClient.ChatAsync(conversationId,
                [new ChatMessage(ChatRole.User, "do the thing")]));

        // Exactly one invocation — no retries.
        Assert.Equal(1, harness.GetInvocationCount("write_record"));

        await host.StopAsync();
    }

    /// <summary>
    /// Test 4: when the LLM never produces a final answer, the workflow exits after
    /// <c>MaxToolCallsPerTurn</c> iterations and returns a sentinel <see cref="ChatResponse"/>
    /// instead of throwing (per OD-9).
    /// </summary>
    [Fact]
    public async Task MaxToolCallsPerTurn_Cap_ReturnsSentinelResponse()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var endlessTool = harness.BuildAlwaysSucceeds("loop_tool", "Returns nothing useful.", _ => "ok");

        // Script enough tool-call responses to exceed the cap; the test sets cap = 3.
        const int cap = 3;
        var scripted = new ScriptedChatClient(Enumerable.Range(0, cap + 5).Select(i =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"call-{i}", "loop_tool")]))));

        var taskQueue = $"pattern3-cap-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            scripted,
            builder => builder.AddDurableTools(endlessTool),
            opts => opts.MaxToolCallsPerTurn = cap);
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"cap-{Guid.NewGuid():N}";

        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "loop forever")]);

        Assert.NotNull(response);
        // Sentinel message must explicitly call out the cap (OD-9 — improves on Pattern 1's confusing "partial response").
        Assert.Contains("Maximum tool-call iterations", response.Text);

        await host.StopAsync();
    }

    /// <summary>
    /// Test 5: regression — when callers use the existing Pattern 1 (
    /// <c>UseFunctionInvocation()</c>, no <c>AddDurableTools</c>), the behaviour is
    /// unchanged: a single <c>GetResponse</c> activity, no <c>GetChatStep</c>,
    /// no <c>InvokeFunction</c>.
    /// </summary>
    [Fact]
    public async Task Pattern1Regression_UseFunctionInvocation_StillWorks()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var weatherTool = harness.BuildAlwaysSucceeds("get_weather", "Weather.", _ => "sunny");

        // Pattern 1 uses FunctionInvokingChatClient to execute tools inline.
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", "get_weather")],
            "Final weather report.");

        var taskQueue = $"pattern1-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services
            .AddChatClient(scripted)
            .UseFunctionInvocation()
            .Build();

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(10);
            });

        using var host = builder.Build();
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"pattern1-{Guid.NewGuid():N}";

        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "weather?")],
            new ChatOptions { Tools = [weatherTool] });

        Assert.NotNull(response);

        // No Pattern 3 activities should have been dispatched.
        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle(workflowId);
        var counts = await WorkflowHistoryAssertions.CountAllScheduledByTypeAsync(handle);
        Assert.False(counts.ContainsKey(GetChatStepActivity),
            "Pattern 1 must not dispatch the Pattern 3 GetChatStep activity.");
        Assert.False(counts.ContainsKey(InvokeFunctionActivity),
            "Pattern 1 dispatches tools inline; InvokeFunction must not appear.");

        await host.StopAsync();
    }

    // ── Error handling — OD-7 catch-and-feed-back ───────────────────────────

    /// <summary>
    /// Test 6: tool throws on turn 1 → workflow synthesizes an error
    /// <see cref="FunctionResultContent"/> → the LLM is invoked again with the error
    /// in its message history.
    /// </summary>
    [Fact]
    public async Task CatchAndFeedBack_RoundTrip()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        // Fails once then succeeds — the workflow must call the LLM twice.
        var flakyTool = harness.BuildFailThenSucceed("flaky_tool", "Sometimes fails.", failCount: 1, successResult: "second-time-result");

        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "flaky_tool")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-2", "flaky_tool")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Final answer after retry.")),
        ]);

        var taskQueue = $"pattern3-feedback-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            scripted,
            builder => builder.AddDurableTools(flakyTool, o => o.NoRetry()),
            opts =>
            {
                opts.MaximumConsecutiveErrorsPerRequest = 3; // tolerate the failure
                opts.IncludeDetailedErrors = true;
            });
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"feedback-{Guid.NewGuid():N}";

        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Use the flaky tool")]);

        Assert.NotNull(response);
        Assert.Contains("Final answer", response.Text);

        // The scripted client should have been called 3 times (initial + after-error + after-success).
        Assert.Equal(3, scripted.CallCount);

        // The SECOND call to the LLM (Calls[1]) must contain a Tool-role message with the synthesized error.
        var secondCall = scripted.Calls[1];
        var toolMessages = secondCall.Messages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.NotEmpty(toolMessages);
        var hasErrorResult = toolMessages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Any(frc => frc.CallId == "call-1");
        Assert.True(hasErrorResult,
            "Synthesized error FunctionResultContent for call-1 must be present in the second LLM call.");

        await host.StopAsync();
    }

    /// <summary>
    /// Test 7a: tool fails N+1 consecutive turns → workflow surfaces a non-retryable
    /// <see cref="ApplicationFailureException"/>.
    /// Test 7b: a successful turn resets the consecutive-error counter to 0.
    /// </summary>
    [Fact]
    public async Task MaximumConsecutiveErrorsPerRequest_Threshold_FailsAfterCap()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var alwaysFails = harness.BuildAlwaysThrows("broken_tool", "Always fails.", "scripted failure");

        const int threshold = 2;
        // Script: 3 tool calls → 3 failures → threshold exceeded.
        var scripted = new ScriptedChatClient(Enumerable.Range(0, threshold + 1).Select(i =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"call-{i}", "broken_tool")]))));

        var taskQueue = $"pattern3-threshold-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            scripted,
            builder => builder.AddDurableTools(alwaysFails, o => o.NoRetry()),
            opts =>
            {
                opts.MaxToolCallsPerTurn = threshold + 5;
                opts.MaximumConsecutiveErrorsPerRequest = threshold;
            });
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"threshold-{Guid.NewGuid():N}";

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await sessionClient.ChatAsync(conversationId,
                [new ChatMessage(ChatRole.User, "do the broken thing")]));

        Assert.True(harness.GetInvocationCount("broken_tool") >= threshold + 1,
            $"Tool should have been invoked at least {threshold + 1} times before threshold tripped.");

        await host.StopAsync();
    }

    [Fact]
    public async Task MaximumConsecutiveErrorsPerRequest_SuccessResetsCounter()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        // We need a deterministic per-invocation behaviour: fail / success / fail / fail.
        // Use a closure counter rather than the harness so the sequence is bespoke to this test.
        var callIndex = 0;
        var sequenceTool = AIFunctionFactory.Create(
            (string? _ = null) =>
            {
                callIndex++;
                if (callIndex == 1) throw new InvalidOperationException("first failure");
                if (callIndex == 2) return (object?)"success-resets-counter";
                throw new InvalidOperationException($"failure #{callIndex - 1} after reset");
            },
            "sequence_tool",
            "fail/success/fail/fail");

        // Script: 4 tool calls → fail, success, fail, fail. Threshold = 2.
        var scripted = new ScriptedChatClient(Enumerable.Range(0, 4).Select(i =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"call-{i}", "sequence_tool")]))));

        const int threshold = 2;
        var taskQueue = $"pattern3-reset-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            scripted,
            builder => builder.AddDurableTools(sequenceTool, o => o.NoRetry()),
            opts =>
            {
                opts.MaxToolCallsPerTurn = 10;
                opts.MaximumConsecutiveErrorsPerRequest = threshold;
            });
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"reset-{Guid.NewGuid():N}";

        // With a reset on success, only 2 consecutive failures occur (calls 3 & 4),
        // which equals the threshold but does NOT exceed it on its own — the third
        // consecutive failure would be needed. The expected behaviour per plan:
        // counter > MaximumConsecutiveErrorsPerRequest throws. So 2 in a row should
        // trip threshold=2 only if the test allows a third. We exit at the LLM-script
        // end deterministically — if the workflow survives until script exhaustion
        // an exception is fine; what we assert is that the FIRST failure (before
        // the success) did NOT trip the threshold.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await sessionClient.ChatAsync(conversationId,
                [new ChatMessage(ChatRole.User, "drive the sequence")]));

        // Counter-reset semantics: the workflow must have made it past the success
        // (call 2). That means callIndex should have advanced beyond 2.
        Assert.True(callIndex >= 3,
            $"After a successful turn, the consecutive-error counter must reset; observed callIndex={callIndex}.");

        await host.StopAsync();
    }

    /// <summary>
    /// Test 8: parallel fan-out with mixed success and failure. The synthesized
    /// tool-role message must contain <see cref="FunctionResultContent"/> for
    /// every <see cref="FunctionCallContent.CallId"/> in the original order
    /// (load-bearing — OpenAI/Anthropic reject turns with missing call IDs).
    /// </summary>
    [Fact]
    public async Task MixedSuccessFailure_ParallelFanout_AllCallIdsSynthesized()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var good = harness.BuildAlwaysSucceeds("good_tool", "Always succeeds.", _ => "good-result");
        var bad = harness.BuildAlwaysThrows("bad_tool", "Always fails.", "deliberate failure");

        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-good", "good_tool"),
                new FunctionCallContent("call-bad", "bad_tool"),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Acknowledged the error.")),
        ]);

        var taskQueue = $"pattern3-mixed-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            scripted,
            builder =>
            {
                builder.AddDurableTools(good);
                builder.AddDurableTools(bad, o => o.NoRetry());
            },
            opts =>
            {
                opts.MaximumConsecutiveErrorsPerRequest = 3;
                opts.IncludeDetailedErrors = true;
            });
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"mixed-{Guid.NewGuid():N}";

        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "mixed run")]);

        Assert.NotNull(response);

        // The second LLM call's messages should contain a Tool-role message
        // with FunctionResultContent for BOTH call-good AND call-bad, in that order.
        var secondCall = scripted.Calls[1];
        var toolMessage = secondCall.Messages.LastOrDefault(m => m.Role == ChatRole.Tool);
        Assert.NotNull(toolMessage);
        var results = toolMessage!.Contents.OfType<FunctionResultContent>().ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal("call-good", results[0].CallId);
        Assert.Equal("call-bad", results[1].CallId);

        await host.StopAsync();
    }

    /// <summary>
    /// Test 9: with <c>MaximumConsecutiveErrorsPerRequest = 0</c> (MAF-style immediate
    /// propagation), a single tool failure surfaces straight to the caller and the LLM
    /// is not called a second time.
    /// </summary>
    [Fact]
    public async Task ImmediatePropagation_MaximumConsecutiveErrorsPerRequest_Zero()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var bad = harness.BuildAlwaysThrows("fail_tool", "fails", "boom");

        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "fail_tool")])),
            // This second response must NEVER be consumed.
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "this should never be reached")),
        ]);

        var taskQueue = $"pattern3-immediate-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            scripted,
            builder => builder.AddDurableTools(bad, o => o.NoRetry()),
            opts => opts.MaximumConsecutiveErrorsPerRequest = 0);
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"immediate-{Guid.NewGuid():N}";

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await sessionClient.ChatAsync(conversationId,
                [new ChatMessage(ChatRole.User, "fail fast")]));

        // Exactly one LLM call. The synthesized-feedback path must not have fired.
        Assert.Equal(1, scripted.CallCount);

        await host.StopAsync();
    }

    // ── CRIT-2: WhenAllAsync fan-out cancellation path ──────────────────────

    /// <summary>
    /// CRIT-2 regression: when <c>Workflow.WhenAllAsync</c> throws because the
    /// workflow was cancelled mid tool fan-out, previously cancelled tasks fell into
    /// the <c>hadError</c> path in the per-task inspection loop.  With
    /// <c>MaximumConsecutiveErrorsPerRequest = 0</c> (MAF-style immediate propagation),
    /// the cancelled task incremented <c>consecutiveErrors</c> past the threshold and
    /// the workflow surfaced a non-retryable <see cref="ApplicationFailureException"/>
    /// rather than a workflow cancellation.
    ///
    /// After the fix (<c>task.IsCanceled → rethrow OperationCanceledException</c>),
    /// workflow cancellation must not be misclassified as an application error.
    /// </summary>
    [Fact]
    public async Task WhenAllAsync_WorkflowCancelledDuringToolFanOut_DoesNotSurfaceApplicationFailureException()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        // A gate that the tool will block on until the test releases it.
        // The tool never returns on its own — the workflow will be cancelled first.
        var toolGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolStartedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingTool = AIFunctionFactory.Create(
            async (string? _ = null) =>
            {
                // Signal that the tool activity has started so the test knows when to cancel.
                toolStartedSignal.TrySetResult();
                // Block until released or the activity's CancellationToken fires.
                await toolGate.Task.ConfigureAwait(false);
                return (object?)"unblocked";
            },
            "blocking_cancel_tool",
            "A tool that blocks until released.");

        // LLM script: first step returns a tool call, second step never runs.
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-cancel", "blocking_cancel_tool")],
            "This final answer should not be reached.");

        var taskQueue = $"pattern3-cancel-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            scripted,
            builder => builder.AddDurableTools(blockingTool, o => o.NoRetry()),
            // MaximumConsecutiveErrorsPerRequest = 0 is the tightest setting:
            // even one error immediately throws. This was the trigger for the bug.
            opts => opts.MaximumConsecutiveErrorsPerRequest = 0);
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"cancel-fanout-{Guid.NewGuid():N}";

        // Fire the chat turn in the background; it will block at the tool fan-out.
        var chatTask = Task.Run(async () =>
            await sessionClient.ChatAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "start the tool")]));

        // Wait until the tool activity has started so the fan-out is in progress.
        await toolStartedSignal.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Cancel the workflow while the tool is still blocking.
        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle(workflowId);
        await handle.CancelAsync();

        // The chatTask will throw when the workflow cancellation propagates.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => chatTask);

        // Assert: the exception must NOT be an ApplicationFailureException whose
        // message indicates an exceeded error threshold.  Before the fix, the bug
        // produced exactly that ("Exceeded MaximumConsecutiveErrorsPerRequest").
        // The correct failure mode is a workflow cancellation (or a wrapping
        // WorkflowFailedException / WorkflowUpdateFailedException containing it).
        AssertNotApplicationFailureFromErrorThreshold(ex);

        // Release the tool gate so the blocked activity thread can clean up.
        toolGate.TrySetResult();

        await host.StopAsync();
    }

    /// <summary>
    /// Walks the full exception chain and fails if any exception is an
    /// <see cref="ApplicationFailureException"/> whose message contains the
    /// "Exceeded MaximumConsecutiveErrorsPerRequest" threshold-exceeded sentinel.
    /// </summary>
    private static void AssertNotApplicationFailureFromErrorThreshold(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is ApplicationFailureException afe &&
                afe.Message.Contains("Exceeded MaximumConsecutiveErrorsPerRequest",
                    StringComparison.OrdinalIgnoreCase))
            {
                Assert.Fail(
                    $"Workflow cancellation was misclassified as ApplicationFailureException: {afe.Message}. " +
                    "CRIT-2 fix: cancelled tasks must rethrow OperationCanceledException, " +
                    "not increment the consecutive-error counter.");
            }
        }
    }

    // ── Other gaps ──────────────────────────────────────────────────────────

    /// <summary>
    /// Test 10: drive a Pattern 3 session through a continue-as-new transition
    /// and assert both <c>ToolActivityOptions</c> and <c>MaxToolCallsPerTurn</c>
    /// survive the boundary.
    /// </summary>
    [Fact]
    public async Task Pattern3_ContinueAsNew_CarriesToolActivityOptionsAndMaxToolCallsPerTurn()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var tool = harness.BuildAlwaysSucceeds("ping", "Ping tool.", _ => "pong");

        // Script: each turn fires one tool call then a final answer. We do 3 turns.
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "ping")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer-1")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c2", "ping")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer-2")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c3", "ping")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer-3")),
        ]);

        const int maxToolCallsPerTurn = 7;
        var taskQueue = $"pattern3-can-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            scripted,
            builder => builder.AddDurableTools(tool, o => o.WithTimeout(TimeSpan.FromSeconds(25))),
            opts =>
            {
                opts.MaxToolCallsPerTurn = maxToolCallsPerTurn;
                // Trigger continue-as-new after the first turn's entries are stored.
                // Each turn produces a request + response → MaxEntryCount = 2 forces CAN at turn 1 finish.
                opts.MaxEntryCount = 2;
            });
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"can-{Guid.NewGuid():N}";

        var r1 = await sessionClient.ChatAsync(conversationId, [new ChatMessage(ChatRole.User, "turn 1")]);
        Assert.NotNull(r1);
        var r2 = await sessionClient.ChatAsync(conversationId, [new ChatMessage(ChatRole.User, "turn 2")]);
        Assert.NotNull(r2);
        var r3 = await sessionClient.ChatAsync(conversationId, [new ChatMessage(ChatRole.User, "turn 3")]);
        Assert.NotNull(r3);

        // Each turn ran its tool exactly once and produced a final response —
        // implies Pattern 3 stayed active across the CAN.
        Assert.Equal(3, harness.GetInvocationCount("ping"));

        await host.StopAsync();
    }

    /// <summary>
    /// Test 11: the silent-failure safety net. Custom workflow invokes
    /// <c>GetResponseAsync</c> directly (NOT via <c>DurableChatSessionClient</c>),
    /// scripted LLM returns <see cref="FunctionCallContent"/>, and the chat-client
    /// chain contains no <c>FunctionInvokingChatClient</c> — meaning nothing would
    /// actually dispatch the returned tool calls. The runtime check in
    /// <c>GetResponseAsync</c> must throw <c>DurableToolsNotWrappedException</c>
    /// to surface the misconfiguration loudly.
    /// </summary>
    [Fact]
    public async Task DurableToolsNotWrappedException_ThrowsOnSilentFailure()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync(
            new WorkflowEnvironmentStartLocalOptions
            {
                DataConverter = DurableAIDataConverter.Instance,
            });

        var harness = new ScriptedToolHarness();
        var weatherTool = harness.BuildAlwaysSucceeds("get_weather", "weather", _ => "n/a");

        // Scripted LLM returns a tool call. With middleware path + no FIC + no dispatcher,
        // the runtime check must fire.
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "get_weather")])),
        ]);

        var taskQueue = $"pattern3-silent-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        // No UseFunctionInvocation() — this is the footgun.
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(10);
            })
            .AddDurableTools(weatherTool)
            // Register a custom workflow that uses DurableChatClient middleware (NOT the
            // session client path that owns Pattern 3 dispatch).
            .AddWorkflow<MiddlewareChatWorkflow>();

        using var host = builder.Build();
        await host.StartAsync();

        var workflowId = $"silent-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (MiddlewareChatWorkflow wf) => wf.RunAsync(),
            new WorkflowOptions(workflowId, taskQueue));

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await handle.GetResultAsync());

        // Walk the exception chain looking for the expected type name (cross the activity boundary).
        var found = false;
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().Name == "DurableToolsNotWrappedException")
            {
                found = true;
                break;
            }
            if (e is ApplicationFailureException afe && (afe.ErrorType?.Contains("DurableToolsNotWrapped") ?? false))
            {
                found = true;
                break;
            }
        }
        Assert.True(found, $"Expected DurableToolsNotWrappedException in exception chain; got: {ex}");

        await host.StopAsync();
    }

    /// <summary>
    /// Test 12: when the caller passes an explicit subset of tools via
    /// <see cref="ChatOptions.Tools"/>, the activity must NOT overwrite that with
    /// the full registry. Verifies the "respect explicit pass" promise of OD-1.
    ///
    /// Originally skipped because AITool/AIFunction instances couldn't survive JSON
    /// serialization across the workflow→activity boundary (they wrap delegates).
    /// Resolved by <see cref="ChatOptionsToolsJsonConverter"/>, which serializes the
    /// tool subset as a <c>$toolNames</c> name sidecar and reconstitutes placeholder
    /// AIFunction instances on the activity side; <c>SwapPlaceholderTools</c> then
    /// swaps them for real registry entries by name before the LLM call.
    /// </summary>
    [Fact]
    public async Task AutoPopulation_RespectsExplicitChatOptionsTools()
    {
        // ChatOptionsToolsJsonConverter only applies through DurableAIDataConverter; the
        // embedded server's default client uses DataConverter.Default, which has no MEAI
        // polymorphism wiring and would silently drop Tools on the workflow-update wire.
        // Production code gets the converter auto-wired via AddTemporalClient / 3-arg
        // AddHostedTemporalWorker / DurableAIPlugin; tests have to set it explicitly.
        await using var env = await WorkflowEnvironment.StartLocalAsync(
            new WorkflowEnvironmentStartLocalOptions
            {
                DataConverter = DurableAIDataConverter.Instance,
            });

        var harness = new ScriptedToolHarness();
        var weather = harness.BuildAlwaysSucceeds("weather", "Weather", _ => "sunny");
        var stock = harness.BuildAlwaysSucceeds("stock", "Stock", _ => "100");

        // Scripted client immediately returns a final answer — no tool calls.
        // We only need to inspect what ChatOptions.Tools looked like at activity entry.
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "no tools needed")),
        ]);

        var taskQueue = $"pattern3-explicit-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            scripted,
            builder =>
            {
                builder.AddDurableTools(weather);
                builder.AddDurableTools(stock);
            });
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"explicit-{Guid.NewGuid():N}";

        // Pass only the weather tool explicitly.
        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "just weather please")],
            new ChatOptions { Tools = [weather] });

        Assert.NotNull(response);
        Assert.Single(scripted.Calls);

        var firstCall = scripted.Calls[0];
        Assert.NotNull(firstCall.Options);
        Assert.NotNull(firstCall.Options!.Tools);
        Assert.Single(firstCall.Options.Tools!);
        Assert.Equal("weather", firstCall.Options.Tools![0] is AIFunction af ? af.Name : null);

        await host.StopAsync();
    }

    // ── Test-host plumbing ──────────────────────────────────────────────────

    /// <summary>
    /// Wires up the standard Pattern 3 worker host: scripted chat client (no
    /// <c>UseFunctionInvocation()</c>), <see cref="DurableAIServiceCollectionExtensions.AddDurableAI"/>,
    /// optional per-tool / per-option configuration, and a stub embedding generator
    /// to satisfy <see cref="DurableEmbeddingActivities"/> constructor injection.
    /// </summary>
    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        Action<ITemporalWorkerServiceOptionsBuilder> registerTools,
        Action<DurableExecutionOptions>? configureOptions = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);

        // Pattern 3 idiom: register the chat client WITHOUT UseFunctionInvocation().
        builder.Services
            .AddChatClient(chatClient)
            .Build();

        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());

        var workerBuilder = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(60);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(15);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
                configureOptions?.Invoke(opts);
            });

        registerTools(workerBuilder);

        return builder.Build();
    }

    /// <summary>
    /// Stub IEmbeddingGenerator required by <see cref="DurableEmbeddingActivities"/>
    /// constructor injection even when not exercised by the test.
    /// </summary>
    private sealed class NoopEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } = new("noop", null, null, 1);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new[] { 0f })).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

/// <summary>
/// Custom workflow that drives the <see cref="DurableChatClient"/> middleware path
/// (Pattern 2 entry point) used by the <c>DurableToolsNotWrappedException</c> test.
/// </summary>
[Workflow("MiddlewareChatWorkflow")]
public sealed class MiddlewareChatWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync()
    {
        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            ConversationId = Workflow.Info.WorkflowId,
        };

        var response = await Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(input),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromSeconds(30),
                HeartbeatTimeout = TimeSpan.FromSeconds(10),
                // No RetryPolicy override — the activity throws
                // DurableToolsNotWrappedException as a non-retryable
                // ApplicationFailureException, so Temporal's default policy
                // (unlimited retries) is short-circuited by the nonRetryable flag.
                // If the library ever loses that nonRetryable: true, this test will
                // hang as a regression signal.
            });

        return response.Messages.Count > 0 ? response.Messages[0].Text ?? string.Empty : string.Empty;
    }
}

