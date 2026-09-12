// HARNESS — not a doc snippet.
//
// Stand-ins for the application types the MAF how-to docs name without defining: domain services,
// chat clients, interceptors, workflows. Nothing here is a claim about the library. The only thing
// that matters is the SHAPE — a snippet that calls `sp.GetRequiredService<OrderService>().LookupOrder`
// needs an `OrderService.LookupOrder` with a method-group-compatible signature for
// `AIFunctionFactory.Create` to bind to.
//
// Bodies are unreachable by construction: this project has no entry point and is never executed.
// They return values rather than throwing only because that keeps the compiler quiet.
using System.Diagnostics.CodeAnalysis;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Temporalio.Testing;
using Temporalio.Workflows;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.AI.Tools;
using TemporalCommunity.Extensions.Agents.Tools;

namespace DocSnippets.Harness;

internal sealed class OrderService
{
    public string LookupOrder(string orderId) => orderId;

    public string CancelOrder(string orderId) => orderId;
}

internal sealed class RefundService
{
    public string ApplyRefund(string orderId, decimal amount) => $"{orderId}:{amount}";
}

internal sealed class EmailService
{
    public string SendEmail(string to, string body) => $"{to}:{body}";
}

internal sealed class WeatherService
{
    public string GetWeather(string city) => city;
}

internal sealed class DataService
{
    public string DeleteRecords(string filter) => filter;

    public string WriteRecord(string payload) => payload;
}

internal sealed class CatalogService
{
    public string SearchProducts(string query) => query;
}

internal sealed class OrderPolicyService;

internal sealed class RiskService;

internal interface IMyService
{
    Task<string> DoThingAsync(string input);
}

internal interface IMyCustomService
{
    string DoThing(string input);
}

internal sealed class MyCustomService : IMyCustomService
{
    public string DoThing(string input) => input;
}

/// <summary>
/// Minimal <see cref="IChatClient"/> stand-in. The docs use several names for "a chat client I
/// already have" — <c>StubChatClient</c>, <c>EchoChatClient</c> — so the aliases below exist purely
/// so each snippet can stay verbatim.
/// </summary>
internal class HarnessChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<ChatResponseUpdate>();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class StubChatClient : HarnessChatClient;

internal sealed class EchoChatClient : HarnessChatClient;

/// <summary>
/// The decorator the docs use to show per-LLM-call visibility. Only the constructor shape is
/// load-bearing: <c>(IChatClient inner, ILogger&lt;AuditingChatClient&gt; logger)</c>.
/// </summary>
internal sealed class AuditingChatClient(IChatClient inner, ILogger<AuditingChatClient> logger)
    : DelegatingChatClient(inner)
{
    private readonly ILogger<AuditingChatClient> _logger = logger;

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("chat call");
        return base.GetResponseAsync(messages, options, cancellationToken);
    }
}

internal sealed class DateTimeProvider : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default) =>
        new(new AIContext());
}

internal sealed class OrderPolicyInterceptor(OrderPolicyService policy) : IAgentToolInterceptor
{
    private readonly OrderPolicyService _policy = policy;

    public Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext context,
        CancellationToken cancellationToken = default)
    {
        _ = _policy;
        return Task.FromResult(DurableToolDecision.Proceed());
    }
}

internal sealed class RiskScoringInterceptor(RiskService risk) : IAgentToolInterceptor
{
    private readonly RiskService _risk = risk;

    public Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext context,
        CancellationToken cancellationToken = default)
    {
        _ = _risk;
        return Task.FromResult(DurableToolDecision.Proceed());
    }
}

/// <summary>
/// Agent wrapper used by the <c>ConfigureAgentPipeline</c> examples.
/// </summary>
internal sealed class TimingAgent(AIAgent inner, ILogger<TimingAgent> logger) : DelegatingAIAgent(inner)
{
    private readonly ILogger<TimingAgent> _logger = logger;

    public void Log() => _logger.LogDebug("timing");
}

[Workflow]
internal sealed class CustomerServiceWorkflow
{
    [WorkflowRun]
    public Task RunAsync() => Task.CompletedTask;
}

[Workflow]
internal sealed class DynamicRoutingWorkflow
{
    [WorkflowRun]
    public Task RunAsync() => Task.CompletedTask;
}

[Workflow]
internal sealed class RefundWorkflow
{
    [WorkflowRun]
    public Task RunAsync() => Task.CompletedTask;
}

internal sealed class RoutingActivities
{
    [Activity]
    public string Route(string input) => input;
}

/// <summary>
/// Stand-in for the Agents integration-test helper the testing doc tells readers to use. The real
/// one lives in the Agents integration-test project (not referenced here); this reproduces only the
/// signature the snippet binds against, so the snippet can stay verbatim.
/// </summary>
[SuppressMessage(
    "Usage",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Compile-only stand-in; never instantiated.")]
internal static class TestEnvironmentHelper
{
    public static Task<WorkflowEnvironment> StartLocalAsync() =>
        WorkflowEnvironment.StartLocalAsync();
}

/// <summary>
/// Stand-in for the scoped dependency in the dos-and-donts "factory slots share a lifetime"
/// section — the case the doc warns must NOT be captured in a tool factory.
/// </summary>
internal sealed class MyDbContext
{
    public Task<string> LookupAsync(string id) => Task.FromResult(id);
}

/// <summary>
/// The orchestrating workflow from the MAF quickstart's step 3. Registered on the worker in the
/// step 1 snippet, which is why it has to exist for that snippet to compile.
/// </summary>
[Workflow]
internal class AskWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string question)
    {
        var agent = WorkflowAgents.GetTemporalAgent("Assistant");
        var session = await agent.CreateSessionAsync().ConfigureAwait(true);
        var reply = await agent.RunAsync([new ChatMessage(ChatRole.User, question)], session)
            .ConfigureAwait(true);

        return reply.Messages[^1].Text ?? string.Empty;
    }
}
