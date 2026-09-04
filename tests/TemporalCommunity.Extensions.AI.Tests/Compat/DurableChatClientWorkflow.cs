using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI.Tests.Compat;

/// <summary>
/// Stable workflow used to verify and replay the public direct-chat middleware path.
/// </summary>
[Workflow("TemporalCommunity.Extensions.AI.Tests.DurableChatClientWorkflow")]
public sealed class DurableChatClientWorkflow
{
    /// <summary>
    /// The workflow and activity worker deliberately share a queue in these tests.
    /// Activity task-queue routing is covered separately from the scheduler regression.
    /// </summary>
    public const string TaskQueue = "test-durable-chat-client-workflow";

    /// <summary>Executes one durable chat request and then schedules a workflow timer.</summary>
    [WorkflowRun]
    public async Task<string> RunAsync(DurableChatClientWorkflowInput input)
    {
        var chatClient = new ChatClientBuilder(new WorkflowOnlyChatClient())
            .UseDurableExecution(options =>
            {
                options.TaskQueue = TaskQueue;
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
            })
            .Build();

        ChatOptions? options = null;
        if (input.IncludeCompatibilityMetadata)
        {
            options = new ChatOptions()
                .WithChatClientTag("fixture", "v1")
                .WithChatClientTag("tenant", "acme")
                .WithActivityTimeout(TimeSpan.FromSeconds(20))
                .WithMaxRetryAttempts(2);
            options.Instructions = "Preserve this instruction.";
            options.AdditionalProperties!["user.custom"] = "keep";
        }

        var messages = new[] { new ChatMessage(ChatRole.User, "scheduler probe") };
        string responseText;

        if (input.Streaming)
        {
            // Async iterators run their body when MoveNextAsync is called, not when they are
            // created. Keep the probe at that boundary so it pins the observable API contract.
            await using var enumerator = chatClient
                .GetStreamingResponseAsync(messages, options)
                .GetAsyncEnumerator();
            try
            {
                _ = await enumerator.MoveNextAsync();
                responseText = "streaming unexpectedly succeeded";
            }
            catch (NotSupportedException exception)
            {
                responseText = exception.Message;
            }
        }
        else
        {
            var response = await chatClient.GetResponseAsync(messages, options);
            responseText = response.Text ?? string.Empty;
        }

        // This second workflow command proves the chat continuation resumed on Temporal's
        // workflow task scheduler, rather than merely proving that the activity completed.
        await Workflow.DelayAsync(TimeSpan.FromMilliseconds(1));
        return responseText;
    }

    /// <summary>
    /// Sentinel provider that must never execute inside the workflow. The durable middleware
    /// must schedule the worker-side provider through a Temporal activity instead.
    /// </summary>
    private sealed class WorkflowOnlyChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The workflow-local chat provider must not run.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("The workflow-local chat provider must not run.");
#pragma warning disable CS0162 // Iterator requires a yield to compile; execution always throws.
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

/// <summary>Input for <see cref="DurableChatClientWorkflow"/>.</summary>
public sealed record DurableChatClientWorkflowInput
{
    /// <summary>Whether to verify workflow streaming fails when its enumerator advances.</summary>
    public bool Streaming { get; init; }

    /// <summary>Whether to include the pre-metadata-fix compatibility values.</summary>
    public bool IncludeCompatibilityMetadata { get; init; }
}
