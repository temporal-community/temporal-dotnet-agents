using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public class MissingToolInterceptorRegressionTests
{
    private const string InterceptorActivity = "TemporalCommunity.Extensions.AI.RunToolInterceptor";
    private const string ToolActivity = "TemporalCommunity.Extensions.AI.InvokeFunction";

    [Fact]
    public async Task MissingInterceptorActivity_ThrowsNonRetryableConfigurationFailure()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var activities = new DurableChatActivities(services);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() => activities.RunToolInterceptorAsync(
                new DurableToolInterceptorInput { ToolName = "write_record" })));

        Assert.Equal(nameof(DurableConfigurationException), failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("write_record", failure.Message);
        Assert.Contains("IDurableToolInterceptor<DurableToolContext>", failure.Message);
        Assert.Contains("DefaultToolInterceptor", failure.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerReplacement_RespectsFrozenInterceptorRequirement(bool originalSessionRequiresInterceptor)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var queue = $"missing-interceptor-{Guid.NewGuid():N}";
        var conversationId = $"drift-{Guid.NewGuid():N}";
        var invocationCount = 0;
        var tool = AIFunctionFactory.Create(() =>
        {
            Interlocked.Increment(ref invocationCount);
            return "written";
        }, "write_record");

        using (var original = BuildHost(env.Client, queue,
            new ScriptedChatClient([new ChatResponse(new ChatMessage(ChatRole.Assistant, "Ready."))]),
            tool, originalSessionRequiresInterceptor))
        {
            await original.StartAsync();
            try
            {
                var session = original.Services.GetRequiredService<DurableChatSessionClient>();
                Assert.Equal("Ready.", (await session.SendAsync(conversationId,
                    [new ChatMessage(ChatRole.User, "Initialize the session.")])).Text);
            }
            finally
            {
                await original.StopAsync();
            }
        }

        var toolCall = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("write-1", "write_record")]));
        var chat = originalSessionRequiresInterceptor
            ? new ScriptedChatClient(
            [
                toolCall,
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Recovered without tools.")),
            ])
            : new ScriptedChatClient(
            [
                toolCall,
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
            ]);
        using var replacement = BuildHost(env.Client, queue, chat, tool, interceptorEnabled: false);
        await replacement.StartAsync();
        try
        {
            var session = replacement.Services.GetRequiredService<DurableChatSessionClient>();
            var turn = session.SendAsync(conversationId,
                [new ChatMessage(ChatRole.User, "Write a record.")]);

            if (originalSessionRequiresInterceptor)
            {
                var updateFailure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(
                    () => turn.WaitAsync(TimeSpan.FromSeconds(30)));
                var activityFailure = Assert.IsType<ActivityFailureException>(updateFailure.InnerException);
                var failure = Assert.IsType<ApplicationFailureException>(activityFailure.InnerException);
                Assert.Equal(nameof(DurableConfigurationException), failure.ErrorType);
                Assert.True(failure.NonRetryable);
                Assert.Contains("write_record", failure.Message);
                Assert.Equal(0, Volatile.Read(ref invocationCount));
                Assert.Equal(1, chat.CallCount);
            }
            else
            {
                Assert.Equal("Done.", (await turn.WaitAsync(TimeSpan.FromSeconds(30))).Text);
                Assert.Equal(1, Volatile.Read(ref invocationCount));
                Assert.Equal(2, chat.CallCount);
            }

            var handle = env.Client.GetWorkflowHandle(session.GetWorkflowId(conversationId));
            var counts = await WorkflowHistoryAssertions.CountAllScheduledByTypeAsync(handle);
            if (originalSessionRequiresInterceptor)
            {
                Assert.Equal(1, counts[InterceptorActivity]);
                Assert.False(counts.ContainsKey(ToolActivity));
                var history = await handle.FetchHistoryAsync();
                var failure = Assert.Single(history.Events,
                    item => item.ActivityTaskFailedEventAttributes is not null)
                    .ActivityTaskFailedEventAttributes;
                Assert.Equal(Temporalio.Api.Enums.V1.RetryState.NonRetryableFailure, failure.RetryState);

                // Follow-up uses the explicit second scripted response, which has no tool calls.
                Assert.Equal("Recovered without tools.", (await session.SendAsync(conversationId,
                    [new ChatMessage(ChatRole.User, "Continue without tools.")])
                    .WaitAsync(TimeSpan.FromSeconds(30))).Text);
                Assert.Equal(0, Volatile.Read(ref invocationCount));
                Assert.Equal(2, chat.CallCount);
                counts = await WorkflowHistoryAssertions.CountAllScheduledByTypeAsync(handle);
                Assert.Equal(1, counts[InterceptorActivity]);
                Assert.False(counts.ContainsKey(ToolActivity));
            }
            else
            {
                Assert.False(counts.ContainsKey(InterceptorActivity));
                Assert.Equal(1, counts[ToolActivity]);
            }

            await session.ShutdownAsync(conversationId);
        }
        finally
        {
            await replacement.StopAsync();
        }
    }

    private static IHost BuildHost(
        ITemporalClient client,
        string queue,
        IChatClient chat,
        AIFunction tool,
        bool interceptorEnabled)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(chat).Build();
        builder.Services.AddHostedTemporalWorker(queue)
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
                options.RetryPolicy = new() { MaximumAttempts = 3 };
                if (interceptorEnabled)
                {
                    options.DefaultToolInterceptor = _ => new ProceedInterceptor();
                }
            })
            .AddDurableTools(tool);
        return builder.Build();
    }

    private sealed class ProceedInterceptor : IDurableToolInterceptor<DurableToolContext>
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            DurableToolContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(DurableToolDecision.Proceed());
    }
}
