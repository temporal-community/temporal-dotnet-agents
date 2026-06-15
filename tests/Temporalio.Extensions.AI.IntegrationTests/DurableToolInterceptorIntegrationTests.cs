using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Approvals;
using Temporalio.Extensions.AI.IntegrationTests.Helpers;
using Temporalio.Extensions.AI.Tools;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.AI.IntegrationTests;

/// <summary>
/// Integration tests for the MEAI Pattern 3 tool interceptor path
/// (<see cref="DurableExecutionOptions.DefaultToolInterceptor"/>).
/// Covers Proceed, Skip, Block, and RequireApproval (Rule 2) outcomes.
/// Each test spins its own <see cref="WorkflowEnvironment"/> for isolation.
/// </summary>
public class DurableToolInterceptorIntegrationTests
{
    private const string RunToolInterceptorActivity = "Temporalio.Extensions.AI.RunToolInterceptor";
    private const string InvokeFunctionActivity = "Temporalio.Extensions.AI.InvokeFunction";

    // ── Proceed ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When the interceptor returns Proceed, the tool activity is dispatched normally
    /// and the workflow receives the real tool result.
    /// </summary>
    [Fact]
    public async Task Interceptor_Proceed_ToolDispatches()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var tool = harness.BuildAlwaysSucceeds("ping", "Ping.", _ => "pong");

        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", "ping")],
            "The result is pong.");

        var interceptorInvoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var interceptor = new DelegateInterceptor(async (ctx, _) =>
        {
            interceptorInvoked.TrySetResult(ctx.ToolName);
            return DurableToolDecision.Proceed();
        });

        var taskQueue = $"interceptor-proceed-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, scripted, builder =>
            builder.AddDurableTools(tool), interceptor);
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"proceed-{Guid.NewGuid():N}";

        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "ping?")]);

        Assert.NotNull(response);
        Assert.Contains("pong", response.Text);
        Assert.Equal(1, harness.GetInvocationCount("ping"));

        // Interceptor must have been invoked.
        var interceptedTool = await interceptorInvoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ping", interceptedTool);

        // Both RunToolInterceptor and InvokeFunction activities must appear in history.
        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle(workflowId);
        var counts = await WorkflowHistoryAssertions.CountAllScheduledByTypeAsync(handle);
        Assert.True(counts.ContainsKey(RunToolInterceptorActivity),
            "RunToolInterceptor activity must appear in workflow history.");
        Assert.True(counts.ContainsKey(InvokeFunctionActivity),
            "InvokeFunction activity must appear in workflow history.");

        await host.StopAsync();
    }

    // ── Skip ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the interceptor returns Skip, no InvokeFunction activity is dispatched and
    /// a synthetic FunctionResultContent is injected in its place.
    /// </summary>
    [Fact]
    public async Task Interceptor_Skip_NoToolDispatch_SyntheticResultInjected()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var tool = harness.BuildAlwaysSucceeds("cache_lookup", "Looks up from cache.", _ => "from-cache");

        // Scripted LLM: first call returns a tool call; second call receives the
        // synthetic "cached" result injected by the interceptor.
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "cache_lookup")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Got cached value.")),
        ]);

        var interceptor = new DelegateInterceptor((ctx, _) =>
            Task.FromResult(DurableToolDecision.Skip("cached_result_from_interceptor")));

        var taskQueue = $"interceptor-skip-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, scripted, builder =>
            builder.AddDurableTools(tool), interceptor);
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"skip-{Guid.NewGuid():N}";

        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "lookup something")]);

        Assert.NotNull(response);

        // Tool must NOT have been invoked — the interceptor short-circuited it.
        Assert.Equal(0, harness.GetInvocationCount("cache_lookup"));

        // InvokeFunction must NOT appear in history (Skip skips the real dispatch).
        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle(workflowId);
        var counts = await WorkflowHistoryAssertions.CountAllScheduledByTypeAsync(handle);
        Assert.False(counts.ContainsKey(InvokeFunctionActivity),
            "InvokeFunction must NOT appear when interceptor returns Skip.");

        // But RunToolInterceptor must appear.
        Assert.True(counts.ContainsKey(RunToolInterceptorActivity),
            "RunToolInterceptor activity must appear in workflow history.");

        // The synthetic result ("cached_result_from_interceptor") must have been
        // visible to the LLM — the second scripted call received a tool-role message.
        var secondCall = scripted.Calls[1];
        var toolMessage = secondCall.Messages.LastOrDefault(m => m.Role == ChatRole.Tool);
        Assert.NotNull(toolMessage);
        var result = toolMessage!.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        Assert.NotNull(result);
        Assert.Equal("call-1", result!.CallId);
        Assert.Equal("cached_result_from_interceptor", result.Result?.ToString());

        await host.StopAsync();
    }

    // ── Block ───────────────────────────────────────────────────────────────

    /// <summary>
    /// When the interceptor returns Block, no InvokeFunction activity is dispatched and
    /// an error FunctionResultContent is injected so the LLM is informed.
    /// </summary>
    [Fact]
    public async Task Interceptor_Block_NoToolDispatch_ErrorResultInjected()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var tool = harness.BuildAlwaysSucceeds("dangerous_tool", "A dangerous tool.", _ => "executed!");

        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "dangerous_tool")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Understood, the action was blocked.")),
        ]);

        var interceptor = new DelegateInterceptor((ctx, _) =>
            Task.FromResult(DurableToolDecision.Block("policy: dangerous_tool is not permitted")));

        var taskQueue = $"interceptor-block-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, scripted, builder =>
            builder.AddDurableTools(tool), interceptor);
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"block-{Guid.NewGuid():N}";

        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "do the dangerous thing")]);

        Assert.NotNull(response);

        // Tool must NOT have been invoked.
        Assert.Equal(0, harness.GetInvocationCount("dangerous_tool"));

        // InvokeFunction must NOT appear in history.
        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle(workflowId);
        var counts = await WorkflowHistoryAssertions.CountAllScheduledByTypeAsync(handle);
        Assert.False(counts.ContainsKey(InvokeFunctionActivity),
            "InvokeFunction must NOT appear when interceptor returns Block.");

        // The blocked result with [Blocked] prefix must have been fed back to LLM.
        var secondCall = scripted.Calls[1];
        var toolMessage = secondCall.Messages.LastOrDefault(m => m.Role == ChatRole.Tool);
        Assert.NotNull(toolMessage);
        var result = toolMessage!.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        Assert.NotNull(result);
        Assert.Equal("call-1", result!.CallId);
        var resultText = result.Result?.ToString() ?? string.Empty;
        Assert.Contains("[Blocked]", resultText);
        Assert.Contains("policy: dangerous_tool is not permitted", resultText);

        await host.StopAsync();
    }

    // ── RequireApproval (Rule 2) ────────────────────────────────────────────

    /// <summary>
    /// When a tool is registered with <c>.RequireApproval()</c>, it pauses for approval
    /// even without an interceptor (BLOCK-2 fix: RequireApproval is an absolute floor).
    /// Approving the request allows the tool to execute.
    /// </summary>
    [Fact]
    public async Task RequireApproval_NoInterceptor_PausesForApproval_ApproveAllowsTool()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var tool = harness.BuildAlwaysSucceeds("send_email", "Sends email.", _ => "email-sent");

        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", "send_email")],
            "Email was sent.");

        var taskQueue = $"interceptor-approval-{Guid.NewGuid():N}";
        // No interceptor registered — RequireApproval works without one.
        using var host = BuildHostNoInterceptor(env.Client, taskQueue, scripted, builder =>
            builder.AddDurableTools(tool, o => o.RequireApproval()));
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"approval-require-{Guid.NewGuid():N}";

        // Start the chat turn in background — it will pause waiting for approval.
        var chatTask = Task.Run(async () =>
            await sessionClient.ChatAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "send the email")]));

        // Wait for approval request to appear.
        DurableApprovalRequest? pending = null;
        for (var i = 0; i < 30 && pending is null; i++)
        {
            await Task.Delay(200);
            pending = await sessionClient.GetPendingApprovalAsync(conversationId);
        }

        Assert.NotNull(pending);
        Assert.Equal("send_email", pending!.FunctionName);

        // Approve.
        await sessionClient.SubmitApprovalAsync(conversationId, new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved = true,
        });

        // Chat should now complete.
        var response = await chatTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(response);
        Assert.Equal(1, harness.GetInvocationCount("send_email"));

        await host.StopAsync();
    }

    // ── SkipInterceptor flag ────────────────────────────────────────────────

    /// <summary>
    /// When a tool has <c>.SkipInterceptor()</c>, no interceptor activity is dispatched for
    /// that tool — it proceeds directly to InvokeFunction.
    /// </summary>
    [Fact]
    public async Task SkipInterceptor_Flag_InterceptorNotCalledForSkippedTool()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var harness = new ScriptedToolHarness();
        var tool = harness.BuildAlwaysSucceeds("read_file", "Read-only; safe to skip interceptor.", _ => "file-contents");

        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", "read_file")],
            "File contents retrieved.");

        var interceptorCallCount = 0;
        var interceptor = new DelegateInterceptor((ctx, _) =>
        {
            Interlocked.Increment(ref interceptorCallCount);
            return Task.FromResult(DurableToolDecision.Block("should not be called"));
        });

        var taskQueue = $"interceptor-skip-flag-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, scripted, builder =>
            builder.AddDurableTools(tool, o => o.SkipInterceptor()), interceptor);
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"skip-flag-{Guid.NewGuid():N}";

        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "read the file")]);

        Assert.NotNull(response);
        Assert.Equal(1, harness.GetInvocationCount("read_file"));

        // Interceptor must not have been invoked (the tool was flagged SkipInterceptor).
        Assert.Equal(0, interceptorCallCount);

        // RunToolInterceptor must NOT appear in history.
        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle(workflowId);
        var counts = await WorkflowHistoryAssertions.CountAllScheduledByTypeAsync(handle);
        Assert.False(counts.ContainsKey(RunToolInterceptorActivity),
            "RunToolInterceptor must NOT appear when tool has SkipInterceptor flag.");

        await host.StopAsync();
    }

    // ── Test-host plumbing ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a Pattern 3 worker host with a registered interceptor.
    /// </summary>
    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        Action<ITemporalWorkerServiceOptionsBuilder> registerTools,
        IDurableToolInterceptor<DurableToolContext> interceptor)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services
            .AddChatClient(chatClient)
            .Build();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());
        builder.Services.AddSingleton<IDurableToolInterceptor<DurableToolContext>>(interceptor);

        var workerBuilder = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(60);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(15);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
                opts.DefaultToolInterceptor = sp =>
                    sp.GetRequiredService<IDurableToolInterceptor<DurableToolContext>>();
            });

        registerTools(workerBuilder);

        return builder.Build();
    }

    /// <summary>
    /// Builds a Pattern 3 worker host WITHOUT an interceptor (for RequireApproval BLOCK-2 tests).
    /// </summary>
    private static IHost BuildHostNoInterceptor(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        Action<ITemporalWorkerServiceOptionsBuilder> registerTools)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
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
                // No DefaultToolInterceptor — RequireApproval must work independently.
            });

        registerTools(workerBuilder);

        return builder.Build();
    }

    /// <summary>
    /// Stub IEmbeddingGenerator required by DurableEmbeddingActivities constructor injection.
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

    /// <summary>
    /// Simple interceptor backed by a delegate, for inline test configuration.
    /// </summary>
    private sealed class DelegateInterceptor(
        Func<DurableToolContext, CancellationToken, Task<DurableToolDecision>> handler)
        : IDurableToolInterceptor<DurableToolContext>
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            DurableToolContext context,
            CancellationToken cancellationToken) =>
            handler(context, cancellationToken);
    }
}
