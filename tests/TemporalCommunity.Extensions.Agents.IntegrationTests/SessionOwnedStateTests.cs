using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Tests.Shared;
using Xunit;
using Xunit.Abstractions;
using static TemporalCommunity.Extensions.Agents.WorkflowAgents;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Option D Phase 1d — end-to-end proof that conversation state belongs to the session rather than
/// to the <see cref="TemporalAIAgent"/> instance.
/// </summary>
/// <remarks>
/// <para>
/// Before Option D, <c>TemporalAIAgent</c> held one <c>_history</c> list and one
/// <c>_currentStateBag</c>. Every session obtained from the same agent shared them, so a second
/// conversation saw the first one's turns and overwrote its context-provider state — and none of
/// it survived continue-as-new. These tests fail on that model.
/// </para>
/// <para>
/// They must run inside a workflow: <c>TemporalAIAgent</c> refuses to run anywhere else.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class SessionOwnedStateTests
{
    private readonly ITestOutputHelper _output;

    public SessionOwnedStateTests(ITestOutputHelper output) => _output = output;

    private static ScriptedChatClient FinalResponses(int count)
    {
        var responses = new List<ChatResponse>();
        for (var i = 0; i < count; i++)
        {
            responses.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Reply {i}.")));
        }

        return new ScriptedChatClient(responses);
    }

    private static async Task<IHost> StartWorkerAsync<TWorkflow>(
        ITemporalClient client, IChatClient chatClient, string taskQueue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton(chatClient);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<TWorkflow>()
            .AddTemporalAgents(opts => opts.AddDurableAgent("SubAgent", agent =>
            {
                agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            }));

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Two sessions from one agent must not see each other's turns. On the pre-Option-D model both
    /// runs appended to the same list, so each session would report four entries instead of two —
    /// and the second run's prompt would replay the first conversation.
    /// </summary>
    [Fact]
    public async Task DistinctSessions_OnOneAgent_KeepSeparateHistories()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var taskQueue = $"session-isolation-{Guid.NewGuid():N}";
        using var host = await StartWorkerAsync<DistinctSessionsWorkflow>(
            env.Client, FinalResponses(2), taskQueue);
        try
        {
            var result = await env.Client.ExecuteWorkflowAsync(
                (DistinctSessionsWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions($"session-isolation-{Guid.NewGuid():N}", taskQueue));

            _output.WriteLine(
                $"A: {result.SessionAEntryCount} entries {string.Join("|", result.SessionAUserTexts)}; " +
                $"B: {result.SessionBEntryCount} entries {string.Join("|", result.SessionBUserTexts)}");

            // One request + one response per session — not the other session's turns as well.
            Assert.Equal(2, result.SessionAEntryCount);
            Assert.Equal(2, result.SessionBEntryCount);

            Assert.Equal(["alpha question"], result.SessionAUserTexts);
            Assert.Equal(["beta question"], result.SessionBUserTexts);

            // The second run's prompt must not replay the first conversation.
            Assert.DoesNotContain("alpha", result.SessionBPromptText, StringComparison.OrdinalIgnoreCase);

            // Distinct session IDs, and neither session adopted the other's StateBag entry.
            Assert.NotEqual(result.SessionAId, result.SessionBId);
            Assert.Equal("a-only", result.SessionAOwnerTag);
            Assert.Equal("b-only", result.SessionBOwnerTag);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// A second run over the same live session is rejected before it can schedule any activity, and
    /// the first run still completes — proving the guard is released on the success path.
    /// </summary>
    [Fact]
    public async Task OverlappingRunsOnOneSession_AreRejected_AndTheFirstRunStillCompletes()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var taskQueue = $"session-overlap-{Guid.NewGuid():N}";
        // Exactly two scripted responses — one for each run that is supposed to happen (the first
        // and the post-completion third). If the rejected run had reached the model it would eat
        // the third run's response and the workflow would fail outright.
        using var host = await StartWorkerAsync<OverlappingRunsWorkflow>(
            env.Client, FinalResponses(2), taskQueue);
        try
        {
            var result = await env.Client.ExecuteWorkflowAsync(
                (OverlappingRunsWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions($"session-overlap-{Guid.NewGuid():N}", taskQueue));

            _output.WriteLine($"rejection: {result.SecondRunError}");

            Assert.Equal(nameof(InvalidOperationException), result.SecondRunExceptionType);
            Assert.Contains("same session", result.SecondRunError, StringComparison.OrdinalIgnoreCase);

            // The first run finished, so ExitRun ran and the session is reusable afterwards.
            Assert.True(result.FirstRunCompleted);
            Assert.True(result.ThirdRunAfterCompletionSucceeded);

            // Only the first and third runs left turns behind; the rejected one appended nothing.
            Assert.Equal(4, result.FinalEntryCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ─── Workflows ──────────────────────────────────────────────────────────

    public record DistinctSessionsResult
    {
        public required int SessionAEntryCount { get; init; }
        public required int SessionBEntryCount { get; init; }
        public required IReadOnlyList<string> SessionAUserTexts { get; init; }
        public required IReadOnlyList<string> SessionBUserTexts { get; init; }
        public required string SessionBPromptText { get; init; }
        public required string SessionAId { get; init; }
        public required string SessionBId { get; init; }
        public required string? SessionAOwnerTag { get; init; }
        public required string? SessionBOwnerTag { get; init; }
    }

    [Workflow("SessionOwnedState.DistinctSessions")]
    internal class DistinctSessionsWorkflow
    {
        [WorkflowRun]
        public async Task<DistinctSessionsResult> RunAsync()
        {
            var agent = GetTemporalAgent("SubAgent");
            var a = (TemporalAgentSession)await agent.CreateSessionAsync().ConfigureAwait(true);
            var b = (TemporalAgentSession)await agent.CreateSessionAsync().ConfigureAwait(true);

            a.StateBag.SetValue("owner", "a-only");
            b.StateBag.SetValue("owner", "b-only");

            await agent.RunAsync([new ChatMessage(ChatRole.User, "alpha question")], a).ConfigureAwait(true);
            var bResponse = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, "beta question")], b).ConfigureAwait(true);

            return new DistinctSessionsResult
            {
                SessionAEntryCount = a.History.Count,
                SessionBEntryCount = b.History.Count,
                SessionAUserTexts = UserTexts(a),
                SessionBUserTexts = UserTexts(b),
                SessionBPromptText = string.Join(" ", bResponse.Messages.Select(m => m.Text)),
                SessionAId = a.SessionId.WorkflowId,
                SessionBId = b.SessionId.WorkflowId,
                SessionAOwnerTag = a.StateBag.TryGetValue<string>("owner", out var av) ? av : null,
                SessionBOwnerTag = b.StateBag.TryGetValue<string>("owner", out var bv) ? bv : null,
            };
        }

        private static IReadOnlyList<string> UserTexts(TemporalAgentSession session) =>
        [
            .. session.History
                .SelectMany(e => e.Messages)
                .Where(m => m.Role == ChatRole.User)
                .Select(m => m.Text ?? string.Empty)
        ];
    }

    public record OverlappingRunsResult
    {
        public required string SecondRunExceptionType { get; init; }
        public required string SecondRunError { get; init; }
        public required bool FirstRunCompleted { get; init; }
        public required bool ThirdRunAfterCompletionSucceeded { get; init; }
        public required int FinalEntryCount { get; init; }
    }

    [Workflow("SessionOwnedState.OverlappingRuns")]
    internal class OverlappingRunsWorkflow
    {
        [WorkflowRun]
        public async Task<OverlappingRunsResult> RunAsync()
        {
            var agent = GetTemporalAgent("SubAgent");
            var session = (TemporalAgentSession)await agent.CreateSessionAsync().ConfigureAwait(true);

            // Start the first run but do not await it: RunCoreAsync runs synchronously through
            // EnterRun and up to the first activity await, so the session is live from here.
            var first = agent.RunAsync([new ChatMessage(ChatRole.User, "first")], session);

            string exceptionType;
            string error;
            try
            {
                await agent.RunAsync([new ChatMessage(ChatRole.User, "second")], session).ConfigureAwait(true);
                exceptionType = "<none>";
                error = "<no exception thrown>";
            }
            catch (InvalidOperationException ex)
            {
                exceptionType = nameof(InvalidOperationException);
                error = ex.Message;
            }

            await first.ConfigureAwait(true);

            // The guard must have been released, so a later sequential run is allowed.
            var thirdSucceeded = true;
            try
            {
                await agent.RunAsync([new ChatMessage(ChatRole.User, "third")], session).ConfigureAwait(true);
            }
            catch (InvalidOperationException)
            {
                thirdSucceeded = false;
            }

            return new OverlappingRunsResult
            {
                SecondRunExceptionType = exceptionType,
                SecondRunError = error,
                FirstRunCompleted = first.IsCompletedSuccessfully,
                ThirdRunAfterCompletionSucceeded = thirdSucceeded,
                FinalEntryCount = session.History.Count,
            };
        }
    }
}
