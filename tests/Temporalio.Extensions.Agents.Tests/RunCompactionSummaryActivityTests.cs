using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Step 5d tests: pin that the <c>RunCompactionSummary</c> activity dispatches the prompt
/// to the resolved <see cref="IChatClient"/> and returns a populated result. The activity is
/// dormant (no workflow trigger yet) but functional — Step 6's <c>"summarization"</c>
/// strategy will call it.
/// </summary>
public class RunCompactionSummaryActivityTests
{
    [Fact]
    public async Task RunCompactionSummary_DispatchesPromptToDefaultClient_ReturnsResponse()
    {
        var recording = new RecordingChatClient(reply: "Summary of the conversation so far.");
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(recording);
        var sp = services.BuildServiceProvider();

        var activities = new AgentActivities(sp);
        var input = new RunCompactionSummaryInput
        {
            AgentName = "SupportAgent",
            SummarizationPrompt = new[]
            {
                new ChatMessage(ChatRole.System, "Summarize the conversation in one sentence."),
                new ChatMessage(ChatRole.User, "Hello"),
                new ChatMessage(ChatRole.Assistant, "Hi there!"),
            },
        };

        var env = new ActivityEnvironment();
        var result = await env.RunAsync(() => activities.RunCompactionSummaryAsync(input));

        Assert.Equal(1, recording.CallCount);
        Assert.Single(result.SummaryMessages);
        Assert.Equal("Summary of the conversation so far.", result.SummaryMessages[0].Text);
    }

    [Fact]
    public async Task RunCompactionSummary_WithChatClientKey_ResolvesKeyedClient()
    {
        // Strategies can route summarization to a separate (typically cheaper) keyed client.
        // The activity must honor ChatClientKey instead of the unkeyed default.
        var primary = new RecordingChatClient(reply: "wrong-client");
        var summarizer = new RecordingChatClient(reply: "Concise rollup.");
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(primary);
        services.AddKeyedSingleton<IChatClient>("summarizer", summarizer);
        var sp = services.BuildServiceProvider();

        var activities = new AgentActivities(sp);
        var input = new RunCompactionSummaryInput
        {
            AgentName = "SupportAgent",
            ChatClientKey = "summarizer",
            SummarizationPrompt = new[] { new ChatMessage(ChatRole.User, "x") },
        };

        var env = new ActivityEnvironment();
        var result = await env.RunAsync(() => activities.RunCompactionSummaryAsync(input));

        Assert.Equal(0, primary.CallCount);
        Assert.Equal(1, summarizer.CallCount);
        Assert.Equal("Concise rollup.", result.SummaryMessages[0].Text);
    }

    [Fact]
    public async Task RunCompactionSummary_WithModelIdOverride_PassesToChatOptions()
    {
        var recording = new RecordingChatClient(reply: "summary");
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(recording);
        var sp = services.BuildServiceProvider();

        var activities = new AgentActivities(sp);
        var input = new RunCompactionSummaryInput
        {
            AgentName = "SupportAgent",
            ModelIdOverride = "gpt-4o-mini",
            SummarizationPrompt = new[] { new ChatMessage(ChatRole.User, "x") },
        };

        var env = new ActivityEnvironment();
        var result = await env.RunAsync(() => activities.RunCompactionSummaryAsync(input));

        Assert.Equal("gpt-4o-mini", recording.LastObservedModelId);
        Assert.Equal("gpt-4o-mini", result.ModelIdUsed);
    }

    [Fact]
    public async Task RunCompactionSummary_ChatClientThrows_PropagatesException()
    {
        var failing = new RecordingChatClient(reply: null, throwOnCall: new InvalidOperationException("rate-limited"));
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(failing);
        var sp = services.BuildServiceProvider();

        var activities = new AgentActivities(sp);
        var input = new RunCompactionSummaryInput
        {
            AgentName = "SupportAgent",
            SummarizationPrompt = new[] { new ChatMessage(ChatRole.User, "x") },
        };

        var env = new ActivityEnvironment();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.RunAsync(() => activities.RunCompactionSummaryAsync(input)));
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly string? _reply;
        private readonly Exception? _throwOnCall;

        public RecordingChatClient(string? reply, Exception? throwOnCall = null)
        {
            _reply = reply;
            _throwOnCall = throwOnCall;
        }

        public int CallCount { get; private set; }
        public string? LastObservedModelId { get; private set; }

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastObservedModelId = options?.ModelId;
            if (_throwOnCall is not null) throw _throwOnCall;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, _reply ?? string.Empty)])
            {
                ModelId = options?.ModelId,
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastObservedModelId = options?.ModelId;
            if (_throwOnCall is not null) throw _throwOnCall;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply ?? string.Empty)
            {
                ModelId = options?.ModelId,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
