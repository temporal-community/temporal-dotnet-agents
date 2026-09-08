using System.Text.Json;
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

    /// <summary>
    /// A session carried across a continue-as-new boundary keeps its history and its StateBag.
    /// On the pre-Option-D model neither was part of the serialized session, so the conversation
    /// silently restarted empty on the next generation.
    /// </summary>
    [Fact]
    public async Task SessionCarriedAcrossContinueAsNew_KeepsHistoryAndStateBag()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var taskQueue = $"session-can-{Guid.NewGuid():N}";
        // One response per generation.
        using var host = await StartWorkerAsync<CarryForwardWorkflow>(
            env.Client, FinalResponses(2), taskQueue);
        try
        {
            var result = await env.Client.ExecuteWorkflowAsync(
                (CarryForwardWorkflow wf) => wf.RunAsync(new CarryForwardInput { Generation = 0 }),
                new WorkflowOptions($"session-can-{Guid.NewGuid():N}", taskQueue));

            _output.WriteLine(
                $"gen={result.Generation} entries={result.HistoryEntryCount} " +
                $"tag={result.CarriedTag} texts={string.Join("|", result.UserTexts)}");

            Assert.Equal(1, result.Generation);

            // Two turns' worth of entries: the pre-continue-as-new turn survived.
            Assert.Equal(4, result.HistoryEntryCount);
            Assert.Equal(["turn in generation 0", "turn in generation 1"], result.UserTexts);

            // StateBag written before the boundary is still there.
            Assert.Equal("written-in-gen-0", result.CarriedTag);

            // Same conversation, not a fresh one.
            Assert.Equal(result.OriginalSessionId, result.SessionId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    public record CarryForwardInput
    {
        public JsonElement? CarriedSession { get; init; }
        public string? OriginalSessionId { get; init; }
        public int Generation { get; init; }
    }

    public record CarryForwardResult
    {
        public required int Generation { get; init; }
        public required int HistoryEntryCount { get; init; }
        public required IReadOnlyList<string> UserTexts { get; init; }
        public required string? CarriedTag { get; init; }
        public required string SessionId { get; init; }
        public required string OriginalSessionId { get; init; }
    }

    [Workflow("SessionOwnedState.CarryForward")]
    internal class CarryForwardWorkflow
    {
        [WorkflowRun]
        public async Task<CarryForwardResult> RunAsync(CarryForwardInput input)
        {
            var agent = GetTemporalAgent("SubAgent");

            var session = input.CarriedSession is { } carried
                ? (TemporalAgentSession)await agent.DeserializeSessionAsync(carried).ConfigureAwait(true)
                : (TemporalAgentSession)await agent.CreateSessionAsync().ConfigureAwait(true);

            if (input.Generation == 0)
            {
                session.StateBag.SetValue("carried.tag", "written-in-gen-0");
            }

            await agent.RunAsync(
                [new ChatMessage(ChatRole.User, $"turn in generation {input.Generation}")],
                session).ConfigureAwait(true);

            if (input.Generation == 0)
            {
                var next = new CarryForwardInput
                {
                    CarriedSession = await agent.SerializeSessionAsync(session).ConfigureAwait(true),
                    OriginalSessionId = session.SessionId.WorkflowId,
                    Generation = 1,
                };
                throw Workflow.CreateContinueAsNewException((CarryForwardWorkflow wf) => wf.RunAsync(next));
            }

            return new CarryForwardResult
            {
                Generation = input.Generation,
                HistoryEntryCount = session.History.Count,
                UserTexts =
                [
                    .. session.History
                        .SelectMany(e => e.Messages)
                        .Where(m => m.Role == ChatRole.User)
                        .Select(m => m.Text ?? string.Empty)
                ],
                CarriedTag = session.StateBag.TryGetValue<string>("carried.tag", out var tag) ? tag : null,
                SessionId = session.SessionId.WorkflowId,
                OriginalSessionId = input.OriginalSessionId ?? string.Empty,
            };
        }
    }

    /// <summary>
    /// SECURITY: a session now carries its conversation history, so running one agent's session on
    /// another agent would flatten that transcript into the second agent's prompt and event
    /// history. Before session ownership a mismatched session carried only an ID and a StateBag.
    /// </summary>
    [Fact]
    public async Task SessionBelongingToAnotherAgent_IsRejectedBeforeAnyLlmCall()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var taskQueue = $"session-binding-{Guid.NewGuid():N}";
        var scripted = FinalResponses(1);
        using var host = await StartWorkerAsync<ForeignSessionWorkflow>(env.Client, scripted, taskQueue);
        try
        {
            var result = await env.Client.ExecuteWorkflowAsync(
                (ForeignSessionWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions($"session-binding-{Guid.NewGuid():N}", taskQueue));

            _output.WriteLine($"rejection: {result.RejectionMessage}");

            Assert.Equal(nameof(InvalidOperationException), result.RejectionExceptionType);
            Assert.Contains("Researcher", result.RejectionMessage, StringComparison.Ordinal);
            Assert.Contains("SubAgent", result.RejectionMessage, StringComparison.OrdinalIgnoreCase);

            // The rejection must not echo the transcript it refused to read.
            Assert.DoesNotContain("confidential", result.RejectionMessage, StringComparison.OrdinalIgnoreCase);

            // A session belonging to this agent still runs — matching is by name, so two agent
            // instances resolved from one registration stay interchangeable. That legitimate run
            // is the one and only call the model should have seen.
            Assert.True(result.OwnSessionRanSuccessfully);
            Assert.Equal(1, scripted.CallCount);

            // The load-bearing assertion: the foreign transcript never reached the model.
            var everySentMessage = scripted.Calls
                .SelectMany(c => c.Messages)
                .Select(m => m.Text ?? string.Empty)
                .ToArray();
            Assert.DoesNotContain(everySentMessage, t => t.Contains("confidential", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    public record ForeignSessionResult
    {
        public required string RejectionExceptionType { get; init; }
        public required string RejectionMessage { get; init; }
        public required bool OwnSessionRanSuccessfully { get; init; }
    }

    [Workflow("SessionOwnedState.ForeignSession")]
    internal class ForeignSessionWorkflow
    {
        [WorkflowRun]
        public async Task<ForeignSessionResult> RunAsync()
        {
            var agent = GetTemporalAgent("SubAgent");

            var foreign = new TemporalAgentSession(new TemporalAgentSessionId("Researcher", "abc123"));
            foreign.AppendHistoryEntry(TemporalCommunity.Extensions.Agents.State.AgentSessionRequest
                .FromRunRequest(
                    new TemporalCommunity.Extensions.Agents.Scheduling.RunRequest(
                        [new ChatMessage(ChatRole.User, "confidential research notes")])
                    {
                        CorrelationId = "c-foreign",
                    },
                    Workflow.UtcNow));

            string exceptionType;
            string message;
            try
            {
                await agent.RunAsync([new ChatMessage(ChatRole.User, "summarise")], foreign)
                    .ConfigureAwait(true);
                exceptionType = "<none>";
                message = "<no exception thrown>";
            }
            catch (InvalidOperationException ex)
            {
                exceptionType = nameof(InvalidOperationException);
                message = ex.Message;
            }

            // Same-name session from a second agent instance must still be accepted.
            var otherInstance = GetTemporalAgent("SubAgent");
            var own = await otherInstance.CreateSessionAsync().ConfigureAwait(true);
            var ranOk = true;
            try
            {
                await agent.RunAsync([new ChatMessage(ChatRole.User, "own session")], own)
                    .ConfigureAwait(true);
            }
            catch (InvalidOperationException)
            {
                ranOk = false;
            }

            return new ForeignSessionResult
            {
                RejectionExceptionType = exceptionType,
                RejectionMessage = message,
                OwnSessionRanSuccessfully = ranOk,
            };
        }
    }

    /// <summary>
    /// The determinism-critical sparse-cursor mapping. When some tool calls in a turn are blocked
    /// and produce synthetic results, the loop walks a sparse array with a separate
    /// <c>pendingIdx</c> cursor to pair completed activities back to their ORIGINAL tool-call
    /// index. An off-by-one there would attach tool 2's result to tool 0's call id.
    /// </summary>
    /// <remarks>
    /// This asserts on tool-result pairing rather than on StateBag write-backs. Write-backs are not
    /// observable on the sub-agent path at all: <c>InvokeAgentToolInput</c> carries no SessionId, so
    /// the activity cannot establish a <c>TemporalAgentContext</c> for a sub-agent and always
    /// returns a null bag. See the KNOWN LIMITATION note in <c>TemporalAIAgent.RunTurnAsync</c>.
    /// The cursor being exercised is the same one either way.
    /// </remarks>
    [Fact]
    public async Task BlockedToolCall_DoesNotShiftSurvivingToolResultsOntoWrongCallIds()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        // c1 names an unregistered tool, so it is blocked before dispatch and leaves a hole
        // between two calls that do run.
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("c0", "echo",
                    new Dictionary<string, object?> { ["value"] = "zero" }),
                new FunctionCallContent("c1", "not_registered", new Dictionary<string, object?>()),
                new FunctionCallContent("c2", "echo",
                    new Dictionary<string, object?> { ["value"] = "two" }),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
        ]);

        var echo = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("value")] string value) => $"echoed:{value}",
            new AIFunctionFactoryOptions { Name = "echo" });

        var taskQueue = $"session-sparse-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<SparseToolResultWorkflow>()
            .AddTemporalAgents(opts => opts.AddDurableAgent("SubAgent", agent =>
            {
                agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                agent.AddTool(echo);
            }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var pairs = await env.Client.ExecuteWorkflowAsync(
                (SparseToolResultWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions($"session-sparse-{Guid.NewGuid():N}", taskQueue));

            _output.WriteLine(string.Join(" | ", pairs));

            Assert.Equal(3, pairs.Count);
            Assert.StartsWith("c0=echoed:zero", pairs[0], StringComparison.Ordinal);
            Assert.StartsWith("c1=", pairs[1], StringComparison.Ordinal);
            Assert.Contains("Blocked", pairs[1], StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("c2=echoed:two", pairs[2], StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Workflow("SessionOwnedState.SparseToolResult")]
    internal class SparseToolResultWorkflow
    {
        [WorkflowRun]
        public async Task<List<string>> RunAsync()
        {
            var agent = GetTemporalAgent("SubAgent");
            var session = (TemporalAgentSession)await agent.CreateSessionAsync().ConfigureAwait(true);
            var response = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, "call the tools")], session).ConfigureAwait(true);

            return
            [
                .. response.Messages
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>()
                    .Select(r => $"{r.CallId}={r.Result}")
            ];
        }
    }

    /// <summary>
    /// The property the sequential isolation test cannot show: two sessions on ONE agent genuinely
    /// overlap. Both runs are started before either is awaited, and the chat client refuses to
    /// answer until both calls are in flight — so if the agent serialized them, or if the run guard
    /// were agent-scoped rather than session-scoped, the first call would block until it times out.
    /// </summary>
    [Fact]
    public async Task TwoSessionsOnOneAgent_RunConcurrently_WithoutContaminatingEachOther()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var probe = new ConcurrencyProbingChatClient(expectedConcurrency: 2);

        var taskQueue = $"session-concurrent-{Guid.NewGuid():N}";
        using var host = await StartWorkerAsync<ConcurrentSessionsWorkflow>(env.Client, probe, taskQueue);
        try
        {
            var result = await env.Client.ExecuteWorkflowAsync(
                (ConcurrentSessionsWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions($"session-concurrent-{Guid.NewGuid():N}", taskQueue));

            _output.WriteLine(
                $"max concurrency observed: {probe.MaxObservedConcurrency}; " +
                $"A: {string.Join("|", result.SessionAUserTexts)}; B: {string.Join("|", result.SessionBUserTexts)}");

            // The load-bearing assertion: both LLM calls were in flight at the same moment. The
            // client only releases once two have arrived, so reaching here at all already proves
            // it — this pins the reason the test passed.
            Assert.Equal(2, probe.MaxObservedConcurrency);

            // Overlap must not cost isolation.
            Assert.Equal(2, result.SessionAEntryCount);
            Assert.Equal(2, result.SessionBEntryCount);
            Assert.Equal(["alpha question"], result.SessionAUserTexts);
            Assert.Equal(["beta question"], result.SessionBUserTexts);
            Assert.NotEqual(result.SessionAId, result.SessionBId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// A chat client that blocks each call until <c>expectedConcurrency</c> calls are in flight,
    /// then releases them all. Turns "did these overlap?" into a pass/fail rather than a race:
    /// without real overlap the first caller waits out the timeout and the test fails loudly.
    /// </summary>
    private sealed class ConcurrencyProbingChatClient : IChatClient
    {
        private readonly int _expectedConcurrency;
        private readonly TaskCompletionSource _allArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _maxObserved;

        public ConcurrencyProbingChatClient(int expectedConcurrency) =>
            _expectedConcurrency = expectedConcurrency;

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var inFlight = Interlocked.Increment(ref _active);

            int seen;
            while (inFlight > (seen = Volatile.Read(ref _maxObserved))
                   && Interlocked.CompareExchange(ref _maxObserved, inFlight, seen) != seen)
            {
                // Another call raised the watermark first; re-read and retry.
            }

            if (inFlight >= _expectedConcurrency)
            {
                _allArrived.TrySetResult();
            }

            try
            {
                await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Reply."));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Workflow("SessionOwnedState.ConcurrentSessions")]
    internal class ConcurrentSessionsWorkflow
    {
        [WorkflowRun]
        public async Task<DistinctSessionsResult> RunAsync()
        {
            var agent = GetTemporalAgent("SubAgent");
            var a = (TemporalAgentSession)await agent.CreateSessionAsync().ConfigureAwait(true);
            var b = (TemporalAgentSession)await agent.CreateSessionAsync().ConfigureAwait(true);

            a.StateBag.SetValue("owner", "a-only");
            b.StateBag.SetValue("owner", "b-only");

            // Both started before either is awaited: RunCoreAsync runs synchronously through
            // EnterRun and the activity schedule, so both turns are genuinely in flight here.
            var runA = agent.RunAsync([new ChatMessage(ChatRole.User, "alpha question")], a);
            var runB = agent.RunAsync([new ChatMessage(ChatRole.User, "beta question")], b);

            await Workflow.WhenAllAsync([runA, runB]).ConfigureAwait(true);

            return new DistinctSessionsResult
            {
                SessionAEntryCount = a.History.Count,
                SessionBEntryCount = b.History.Count,
                SessionAUserTexts = UserTexts(a),
                SessionBUserTexts = UserTexts(b),
                SessionBPromptText = string.Empty,
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
}
