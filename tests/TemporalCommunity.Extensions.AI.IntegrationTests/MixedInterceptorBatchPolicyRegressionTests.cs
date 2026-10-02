using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.AI.Tools;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public class MixedInterceptorBatchPolicyRegressionTests
{
    private const string InterceptorActivity = "TemporalCommunity.Extensions.AI.RunToolInterceptor";
    private const string ToolActivity = "TemporalCommunity.Extensions.AI.InvokeFunction";

    [Fact]
    public async Task MixedBatch_ProceedAndBlock_InvokesOnlyAllowedToolOnce()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        const string allowedToolName = "batch_allowed_write";
        const string blockedToolName = "batch_blocked_write";
        var harness = new ScriptedToolHarness();
        var allowedTool = harness.BuildAlwaysSucceeds(allowedToolName, "Allowed write.", _ => "written");
        var blockedTool = harness.BuildAlwaysSucceeds(blockedToolName, "Blocked write.", _ => "written");
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
        [
            new FunctionCallContent("allowed-call", allowedToolName),
            new FunctionCallContent("blocked-call", blockedToolName),
        ],
        "Batch processed.");
        var interceptorCalls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var interceptor = new DelegateInterceptor((context, _) =>
        {
            interceptorCalls.AddOrUpdate(context.ToolName, 1, static (_, count) => count + 1);
            return Task.FromResult(context.ToolName switch
            {
                allowedToolName => DurableToolDecision.Proceed(),
                blockedToolName => DurableToolDecision.Block("blocked by test policy"),
                _ => throw new InvalidOperationException($"Unexpected tool: {context.ToolName}"),
            });
        });
        var taskQueue = $"mixed-interceptor-batch-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddChatClient(scripted).Build();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());
        builder.Services.AddSingleton<IDurableToolInterceptor<DurableToolContext>>(interceptor);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
                options.DefaultToolInterceptor =
                    services => services.GetRequiredService<IDurableToolInterceptor<DurableToolContext>>();
            })
            .AddDurableTools(allowedTool, blockedTool);
        using var host = builder.Build();

        await host.StartAsync();
        try
        {
            var session = host.Services.GetRequiredService<DurableChatSessionClient>();
            var conversationId = $"mixed-interceptor-batch-{Guid.NewGuid():N}";
            var response = await session.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "Run the batch.")])
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal("Batch processed.", response.Text);
            Assert.Equal(1, interceptorCalls[allowedToolName]);
            Assert.Equal(1, interceptorCalls[blockedToolName]);
            Assert.Equal(1, harness.GetInvocationCount(allowedToolName));
            Assert.Equal(0, harness.GetInvocationCount(blockedToolName));

            var handle = env.Client.GetWorkflowHandle(session.GetWorkflowId(conversationId));
            var counts = await WorkflowHistoryAssertions.CountAllScheduledByTypeAsync(handle);
            Assert.Equal(2, counts[InterceptorActivity]);
            Assert.Equal(1, counts[ToolActivity]);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class DelegateInterceptor(
        Func<DurableToolContext, CancellationToken, Task<DurableToolDecision>> handler)
        : IDurableToolInterceptor<DurableToolContext>
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            DurableToolContext context,
            CancellationToken cancellationToken) =>
            handler(context, cancellationToken);
    }

    private sealed class NoopEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } = new("noop", null, null, 1);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new float[] { 0f })).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
