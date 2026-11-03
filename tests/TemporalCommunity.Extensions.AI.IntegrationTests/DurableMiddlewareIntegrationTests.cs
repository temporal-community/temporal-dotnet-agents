using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Worker.Interceptors;
using Temporalio.Workflows;
using TemporalCommunity.Extensions.AI.Tests.Compat;
using TemporalCommunity.Extensions.Tests.Shared;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>
/// Integration tests for durable middleware and tool dispatch.
/// Each test spins up its own WorkflowEnvironment for independent configuration.
/// </summary>
public class DurableMiddlewareIntegrationTests
{
    /// <summary>
    /// Verifies the public direct-chat middleware resumes on Temporal's workflow scheduler
    /// after a non-streaming activity result and can schedule a subsequent workflow timer.
    /// </summary>
    [Fact]
    public Task DurableChatClientWorkflow_NonStreaming_CompletesAfterActivity() =>
        AssertDirectChatWorkflowAsync(streaming: false);

    /// <summary>
    /// Verifies workflow streaming fails when the async iterator advances, before it schedules a
    /// model activity. This avoids silently changing a streaming request into a buffered one.
    /// </summary>
    [Fact]
    public Task DurableChatClientWorkflow_Streaming_FailsAtEnumeratorAdvance() =>
        AssertDirectChatWorkflowAsync(streaming: true);

    /// <summary>
    /// Verifies direct-workflow tags reach the model-activity span while Temporal private keys are
    /// removed immediately before the worker-side provider call.
    /// </summary>
    [Fact]
    public async Task DurableChatClientWorkflow_TagsReachActivityNotProvider()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var providerClient = new MetadataRecordingChatClient();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(providerClient);
        builder.Services
            .AddHostedTemporalWorker(DurableChatClientWorkflow.TaskQueue)
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
            })
            .AddWorkflow<DurableChatClientWorkflow>();

        using var host = builder.Build();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DurableChatTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        await host.StartAsync();
        try
        {
            var handle = await env.Client.StartWorkflowAsync(
                (DurableChatClientWorkflow workflow) => workflow.RunAsync(
                    new DurableChatClientWorkflowInput { IncludeCompatibilityMetadata = true }),
                new WorkflowOptions(
                    $"durable-chat-metadata-{Guid.NewGuid():N}",
                    DurableChatClientWorkflow.TaskQueue));

            Assert.Equal(
                MetadataRecordingChatClient.ResponseText,
                await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal("v1", providerClient.ActivityTags["fixture"]);
            Assert.Equal("acme", providerClient.ActivityTags["tenant"]);
            Assert.Equal(
                "keep",
                providerClient.Options?.AdditionalProperties?["user.custom"]?.ToString());
            Assert.Equal("Preserve this instruction.", providerClient.Options?.Instructions);
            Assert.DoesNotContain(
                providerClient.Options!.AdditionalProperties!,
                pair => pair.Key.StartsWith("temporal.", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Test 4: Durable tool invocation ─────────────────────────────────────

    /// <summary>
    /// Verifies that a DurableAIFunction dispatches as a Temporal activity when
    /// called inside a workflow body (Workflow.InWorkflow == true).
    ///
    /// Architecture: ToolDispatchWorkflow calls durableFunc.InvokeAsync() directly
    /// in the workflow body. Because Workflow.InWorkflow == true, DurableAIFunction
    /// routes the call to DurableFunctionActivities rather than the inner lambda.
    /// DurableFunctionActivities looks up the registered tool by name and invokes it.
    /// </summary>
    [Fact]
    public async Task DurableAIFunction_InvokesToolAsActivity_WhenCalledInsideWorkflow()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        // The real tool implementation — registered in DurableFunctionRegistry via
        // AddDurableTools so DurableFunctionActivities can resolve it by name.
        var tool = AIFunctionFactory.Create(
            () => "tool-result",
            "get_tool_result",
            "Returns a recognizable test result.");

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);

        // DurableChatActivities requires an IChatClient.
        builder.Services.AddSingleton<IChatClient>(new MinimalChatClient());

        // Use an isolated task queue name to avoid conflicts with other tests.
        const string taskQueue = "test-tool-dispatch-4";

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
            })
            .AddDurableTools(tool)
            .AddWorkflow<ToolDispatchWorkflow>();

        using var host = builder.Build();
        await host.StartAsync();

        var workflowId = $"tool-dispatch-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (ToolDispatchWorkflow wf) => wf.RunAsync(),
            new WorkflowOptions(workflowId, taskQueue));

        var result = await handle.GetResultAsync();

        // The activity resolved the registered tool by name and returned "tool-result".
        Assert.Equal("tool-result", result);

        await host.StopAsync();
    }

    [Fact]
    public async Task DurableAIFunction_RetryDefaults_AreBoundedAndPreserveExplicitNoRetry()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var permanentAttempts = 0;
        var transientAttempts = 0;
        var noRetryAttempts = 0;
        var successAttempts = 0;
        var tools = new[]
        {
            AIFunctionFactory.Create(
                () =>
                {
                    Interlocked.Increment(ref permanentAttempts);
                    throw new InvalidOperationException("permanent");
                },
                "permanent_failure"),
            AIFunctionFactory.Create(
                () => Interlocked.Increment(ref transientAttempts) < 3
                    ? throw new InvalidOperationException("transient")
                    : "recovered",
                "transient_failure"),
            AIFunctionFactory.Create(
                () =>
                {
                    Interlocked.Increment(ref noRetryAttempts);
                    throw new InvalidOperationException("no retry");
                },
                "no_retry_failure"),
            AIFunctionFactory.Create(
                () =>
                {
                    Interlocked.Increment(ref successAttempts);
                    return "success";
                },
                "success"),
        };

        const string taskQueue = "test-durable-function-retries";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(new MinimalChatClient());
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI()
            .AddDurableTools(tools)
            .AddWorkflow<RetryingToolWorkflow>();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => RunToolAsync("permanent_failure", null));
            Assert.Equal(5, permanentAttempts);

            Assert.Equal("recovered", await RunToolAsync("transient_failure", null));
            Assert.Equal(3, transientAttempts);

            await Assert.ThrowsAnyAsync<Exception>(() => RunToolAsync("no_retry_failure", 1));
            Assert.Equal(1, noRetryAttempts);

            Assert.Equal("success", await RunToolAsync("success", null));
            Assert.Equal(1, successAttempts);
        }
        finally
        {
            await host.StopAsync();
        }

        async Task<string> RunToolAsync(string functionName, int? maximumAttempts)
        {
            var handle = await env.Client.StartWorkflowAsync(
                (RetryingToolWorkflow wf) => wf.RunAsync(functionName, maximumAttempts),
                new WorkflowOptions($"durable-function-retry-{Guid.NewGuid():N}", taskQueue));
            return await handle.GetResultAsync();
        }
    }

    // ── Test 6: Heartbeat under tight HeartbeatTimeout ───────────────────────

    /// <summary>
    /// Verifies that DurableChatActivities.GetResponseAsync sends heartbeats
    /// during the streaming loop, keeping the activity alive under a heartbeat
    /// timeout that is shorter than the total activity duration.
    ///
    /// Architecture: ChatActivityTestWorkflow dispatches DurableChatActivities.GetResponseAsync
    /// directly via Workflow.ExecuteActivityAsync with a HeartbeatTimeout of 3 seconds.
    /// SlowStreamingChatClient yields 3 chunks, each preceded by a 1.5-second delay,
    /// for a total streaming duration of ~4.5 seconds. Without per-chunk heartbeats the
    /// activity would be force-failed at 3 seconds. Successful completion proves heartbeats
    /// fired.
    /// </summary>
    [Fact]
    public async Task DurableChatActivities_HeartbeatsKeepActivityAlive_UnderTightTimeout()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(new SlowStreamingChatClient());

        const string taskQueue = "test-heartbeat-6";
        var heartbeatDetails = new ConcurrentQueue<object?[]>();

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .ConfigureOptions(options =>
                options.Interceptors = [new HeartbeatCaptureWorkerInterceptor(heartbeatDetails)])
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(3);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
            })
            .AddWorkflow<ChatActivityTestWorkflow>();

        using var host = builder.Build();
        await host.StartAsync();

        var workflowId = $"heartbeat-test-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (ChatActivityTestWorkflow wf) => wf.RunAsync(),
            new WorkflowOptions(workflowId, taskQueue));

        // If heartbeats did not fire, the activity would be killed at 3 s and
        // the workflow would fail with an ActivityFailureException. Successful
        // completion is the proof that heartbeats kept it alive.
        var result = await handle.GetResultAsync();
        Assert.Equal("chunk1 chunk2 chunk3", result);
        Assert.NotEmpty(heartbeatDetails);
        Assert.All(heartbeatDetails, details => Assert.Empty(details));

        await host.StopAsync();
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static async Task AssertDirectChatWorkflowAsync(bool streaming)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(new SchedulerProbeChatClient());

        builder.Services
            .AddHostedTemporalWorker(DurableChatClientWorkflow.TaskQueue)
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
            })
            .AddWorkflow<DurableChatClientWorkflow>();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var handle = await env.Client.StartWorkflowAsync(
                (DurableChatClientWorkflow workflow) => workflow.RunAsync(
                    new DurableChatClientWorkflowInput { Streaming = streaming }),
                new WorkflowOptions(
                    $"durable-chat-client-{streaming}-{Guid.NewGuid():N}",
                    DurableChatClientWorkflow.TaskQueue));

            var result = await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (streaming)
            {
                Assert.StartsWith("Streaming responses are not supported inside a Temporal workflow", result);
            }
            else
            {
                Assert.Equal(SchedulerProbeChatClient.ResponseText, result);
            }

            Assert.Equal(
                streaming ? 0 : 1,
                await WorkflowHistoryAssertions.CountActivityScheduledAsync(
                    handle,
                    "TemporalCommunity.Extensions.AI.GetResponse"));

            var timerCount = 0;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.TimerStartedEventAttributes is not null)
                {
                    timerCount++;
                }
            }

            Assert.Equal(1, timerCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Minimal IChatClient stub required for DurableChatActivities constructor injection
    /// in tests that do not exercise the chat path.
    /// </summary>
    private sealed class MinimalChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var r = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var u in r.ToChatResponseUpdates()) yield return u;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SchedulerProbeChatClient : IChatClient
    {
        public const string ResponseText = "worker-side durable response";

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, ResponseText)]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class MetadataRecordingChatClient : IChatClient
    {
        public const string ResponseText = "metadata response";

        public ChatOptions? Options { get; private set; }

        public Dictionary<string, object?> ActivityTags { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            if (Activity.Current is { } activity)
            {
                foreach (var tag in activity.TagObjects)
                {
                    ActivityTags[tag.Key] = tag.Value;
                }
            }
            return Task.FromResult(
                new ChatResponse([new ChatMessage(ChatRole.Assistant, ResponseText)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Yields 3 chunks with a 1.5-second delay before each chunk.
    /// Total streaming duration ≈ 4.5 s — longer than the 3-second HeartbeatTimeout
    /// used in Test 6. Without heartbeats the activity would be force-failed mid-stream.
    /// </summary>
    private sealed class SlowStreamingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "chunk1 chunk2 chunk3")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var text in new[] { "chunk1", " chunk2", " chunk3" })
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

}

// ── Tool dispatch workflow ────────────────────────────────────────────────────

/// <summary>
/// Minimal workflow that invokes an <see cref="AIFunctionExtensions.AsDurable"/> wrapper to verify
/// the adapter and same-task-queue activity registration path end-to-end.
/// </summary>
[Workflow("ToolDispatchWorkflow")]
public sealed class ToolDispatchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync()
    {
        var function = AIFunctionFactory.Create(
            (Func<string>)(() =>
                throw new InvalidOperationException("Workflow-local stub must not run.")),
            "get_tool_result").AsDurable(new DurableExecutionOptions
            {
                TaskQueue = "intentionally-unpolled-function-queue",
                ActivityTimeout = TimeSpan.FromSeconds(15),
            });

        var result = await function.InvokeAsync();
        return result?.ToString() ?? string.Empty;
    }
}

[Workflow("RetryingToolWorkflow")]
public sealed class RetryingToolWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string functionName, int? maximumAttempts)
    {
        var options = new DurableExecutionOptions
        {
            ActivityTimeout = TimeSpan.FromMinutes(2),
            RetryPolicy = maximumAttempts.HasValue
                ? new RetryPolicy { MaximumAttempts = maximumAttempts.Value }
                : null,
        };
        var function = AIFunctionFactory.Create(
            (Func<string>)(() => throw new InvalidOperationException("Workflow stub must not run.")),
            functionName).AsDurable(options);
        return (await function.InvokeAsync())?.ToString() ?? string.Empty;
    }
}

// ── Chat activity test workflow ───────────────────────────────────────────────

/// <summary>
/// Minimal workflow that dispatches DurableChatActivities.GetResponseAsync directly
/// to verify the heartbeat path under a tight HeartbeatTimeout (Test 6).
/// </summary>
[Workflow("ChatActivityTestWorkflow")]
public sealed class ChatActivityTestWorkflow
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
                HeartbeatTimeout = TimeSpan.FromSeconds(3),
            });

        return response.Messages.Count > 0 ? response.Messages[0].Text ?? string.Empty : string.Empty;
    }
}

internal sealed class HeartbeatCaptureWorkerInterceptor(
    ConcurrentQueue<object?[]> heartbeatDetails) : IWorkerInterceptor
{
    public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
        new HeartbeatCaptureActivityInboundInterceptor(nextInterceptor, heartbeatDetails);
}

internal sealed class HeartbeatCaptureActivityInboundInterceptor(
    ActivityInboundInterceptor next,
    ConcurrentQueue<object?[]> heartbeatDetails) : ActivityInboundInterceptor(next)
{
    public override void Init(ActivityOutboundInterceptor outbound) =>
        base.Init(new HeartbeatCaptureActivityOutboundInterceptor(outbound, heartbeatDetails));
}

internal sealed class HeartbeatCaptureActivityOutboundInterceptor(
    ActivityOutboundInterceptor next,
    ConcurrentQueue<object?[]> heartbeatDetails) : ActivityOutboundInterceptor(next)
{
    public override void Heartbeat(HeartbeatInput input)
    {
        heartbeatDetails.Enqueue(input.Details);
        base.Heartbeat(input);
    }
}
