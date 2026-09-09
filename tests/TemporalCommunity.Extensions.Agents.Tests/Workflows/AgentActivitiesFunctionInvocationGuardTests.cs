using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

/// <summary>
/// Guard behaviour exercised through the real activity path, not through the helper in isolation.
/// </summary>
/// <remarks>
/// <see cref="Internal.DurableFunctionInvocationGuardTests"/> covers detection itself. These cover
/// the things a direct helper call cannot: that the production call site invokes the guard
/// unconditionally, that the chat-client factory is not part of the non-retryable boundary, and
/// that the factory is only ever invoked from an activity.
/// </remarks>
public class AgentActivitiesFunctionInvocationGuardTests
{
    private static (AgentActivities Activities, T State) BuildHarness<T>(
        Action<TemporalAgentsOptions> configure, T state)
    {
        var options = (TemporalAgentsOptions)Activator.CreateInstance(
            typeof(TemporalAgentsOptions), nonPublic: true)!;
        configure(options);

        var services = new ServiceCollection();
        services.AddSingleton(options);
        var sp = services.BuildServiceProvider();

        return (new AgentActivities(sp, sp.GetRequiredService<IServiceScopeFactory>()), state);
    }

    private static AgentStepInput MakeInput(string agentName) => new()
    {
        AgentName = agentName,
        Request = new RunRequest("hello"),
        AccumulatedMessages = [new ChatMessage(ChatRole.User, "hello")],
        SessionId = TemporalAgentSessionId.WithRandomKey(agentName),
    };

    private static ActivityEnvironment NewEnv() =>
        new() { TemporalClient = A.Fake<ITemporalClient>() };

    private sealed class RecordingChatClient : IChatClient
    {
        internal int ResponseCalls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ResponseCalls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ResponseCalls);
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub"));
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// REGRESSION GUARD for the production call site — do not delete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The helper-level AdditionalTools test cannot protect this. Wrapping the guard call in
    /// <c>if (registration.Tools.Count &gt; 0)</c> would leave the helper test green (it calls the
    /// guard directly) and the main integration test green (it registers a tool). This is the only
    /// test that fails if the disproven tool-count gate is reintroduced at the call site.
    /// </para>
    /// <para>
    /// The agent registers NO durable tools, and the middleware carries its callable function in
    /// <see cref="FunctionInvokingChatClient.AdditionalTools"/> — which MEAI consults for tools the
    /// request did not carry. That is precisely the path a tool-count gate would wave through.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ToollessAgent_WithAdditionalToolsFunctionInvocation_IsRejectedAtTheCallSite()
    {
        var additionalToolRan = false;
        var inner = new RecordingChatClient();

        var additional = AIFunctionFactory.Create(
            () => { additionalToolRan = true; return "ran"; },
            new AIFunctionFactoryOptions { Name = "not_on_the_request" });

        var (activities, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("ToollessAgent", agent =>
            {
                // Deliberately no agent.AddTool(...) anywhere.
                agent.ChatClient = _ => new ChatClientBuilder(inner)
                    .UseFunctionInvocation(configure: fic => fic.AdditionalTools = [additional])
                    .Build();
            });
        }, state: 0);

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => NewEnv().RunAsync(() =>
                activities.RunDurableAgentStepAsync(MakeInput("ToollessAgent"))));

        Assert.True(ex.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), ex.ErrorType);
        Assert.IsType<DurableFunctionInvocationConflictException>(ex.InnerException);

        // Rejected before anything ran — no model call, no additional tool.
        Assert.Equal(0, inner.ResponseCalls);
        Assert.False(additionalToolRan);
    }

    /// <summary>
    /// A failure from the user's own factory must keep normal retry behaviour. The narrow catch
    /// exists precisely so a transient failure — a cold dependency, deferred credential acquisition
    /// — is not permanently fatal.
    /// </summary>
    [Fact]
    public async Task GenericChatClientFactoryFailure_IsNotConvertedToNonRetryable()
    {
        var (activities, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("ThrowingFactoryAgent", agent =>
            {
                agent.ChatClient = _ => throw new InvalidOperationException("transient boom");
            });
        }, state: 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewEnv().RunAsync(() =>
                activities.RunDurableAgentStepAsync(MakeInput("ThrowingFactoryAgent"))));

        Assert.Equal("transient boom", ex.Message);
    }

    /// <summary>
    /// The factory's documented lifecycle is "invoked from the activity's scoped provider on every
    /// LLM-step attempt". Registration must not invoke it; the first attempt must.
    /// </summary>
    [Fact]
    public async Task ChatClientFactory_IsNotInvokedUntilTheFirstActivityAttempt()
    {
        var factoryCalls = 0;
        var inner = new RecordingChatClient();

        var (activities, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("LazyFactoryAgent", agent =>
            {
                agent.ChatClient = _ =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    return inner;
                };
            });
        }, state: 0);

        // Registering the agent and constructing the activities must not touch the factory.
        Assert.Equal(0, factoryCalls);

        await NewEnv().RunAsync(() =>
            activities.RunDurableAgentStepAsync(MakeInput("LazyFactoryAgent")));

        Assert.Equal(1, factoryCalls);

        // A second attempt resolves it again from its own scope rather than reusing an instance.
        await NewEnv().RunAsync(() =>
            activities.RunDurableAgentStepAsync(MakeInput("LazyFactoryAgent")));

        Assert.Equal(2, factoryCalls);
    }
}
