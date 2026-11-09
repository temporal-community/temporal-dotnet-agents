#pragma warning disable MEAI001 // The pinned MEAI reducer is experimental.
using System.Runtime.CompilerServices;
using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

public class WorkerModelContextBudgetTests
{
    private const string AgentName = "BudgetAgent";

    [Theory]
    [InlineData(null, 1024)]
    [InlineData(100000, 0)]
    public async Task Step_EnforcesWorkerBudget_WithMissingOrConflictingFrontendSettings(
        int? frontendLimit,
        int frontendReserve)
    {
        var client = new RecordingChatClient();
        using var services = BuildServices(client);
        var input = MakeInput(new string('x', 70), frontendLimit, frontendReserve);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => RunAsync(services, input));

        Assert.Equal("DurableContextBudgetExceeded", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Step_IgnoresStricterFrontendBudget_AndUsesWorkerReserve()
    {
        var client = new RecordingChatClient();
        using var services = BuildServices(client);
        var result = await RunAsync(
            services,
            MakeInput(new string('x', 60), frontendLimit: 1, frontendReserve: 0));

        Assert.True(result.IsFinal);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(new string('x', 60), Assert.Single(client.LastMessages!).Text);
    }

    [Fact]
    public async Task Step_PerAgentBudgetOverridesWorkerDefaults()
    {
        var client = new RecordingChatClient();
        using var services = BuildServices(client, options => options.AddDurableAgent(AgentName, agent =>
        {
            agent.ChatClient = _ => client;
            agent.MaxModelContextTokens = 200;
            agent.ModelOutputTokenReserve = 30;
        }));

        await RunAsync(services, MakeInput(new string('x', 170)));
        Assert.Equal(1, client.CallCount);
        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => RunAsync(services, MakeInput(new string('x', 171))));
        Assert.Equal("DurableContextBudgetExceeded", failure.ErrorType);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Step_RechecksWorkerBudgetAfterContextProviderAugmentation()
    {
        var client = new RecordingChatClient();
        using var services = BuildServices(client, options => options.AddDurableAgent(AgentName, agent =>
        {
            agent.ChatClient = _ => client;
            agent.AddContextProvider(new InstructionsProvider(new string('i', 50)));
        }));

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => RunAsync(services, MakeInput(new string('x', 20))));

        Assert.Equal("DurableContextBudgetExceeded", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Step_TrimsOnlyPriorTurns_UsingWorkerBudget()
    {
        var client = new RecordingChatClient();
        using var services = BuildServices(client);
        var input = new AgentStepInput
        {
            AgentName = AgentName,
            Request = new RunRequest("current"),
            AccumulatedMessages =
            [
                new(ChatRole.User, new string('x', 40)),
                new(ChatRole.Assistant, new string('y', 40)),
                new(ChatRole.User, "current"),
            ],
            CurrentTurnStartIndex = 2,
            PreviousTurnRanges = [new DurableChatTurnRange(0, 2, IsProtected: false)],
            SessionId = TemporalAgentSessionId.WithRandomKey(AgentName),
        };

        await RunAsync(services, input);

        Assert.Equal("current", Assert.Single(client.LastMessages!).Text);
        Assert.Equal(3, input.AccumulatedMessages.Count);
    }

    [Fact]
    public async Task Step_ReducesPriorContextBeforeApplyingWorkerBudget()
    {
        var client = new RecordingChatClient();
        using var services = BuildServices(
            client,
            options => options.AddDurableAgent(AgentName, agent =>
            {
                agent.ChatClient = _ => client;
                agent.ContextReducerKey = "recent";
            }),
            reducer: new MessageCountingChatReducer(1));
        var input = new AgentStepInput
        {
            AgentName = AgentName,
            Request = new RunRequest("current"),
            AccumulatedMessages =
            [
                new(ChatRole.User, new string('x', 40)),
                new(ChatRole.Assistant, new string('y', 40)),
                new(ChatRole.User, "current"),
            ],
            CurrentTurnStartIndex = 2,
            PreviousTurnRanges = [new DurableChatTurnRange(0, 2, IsProtected: false)],
            SessionId = TemporalAgentSessionId.WithRandomKey(AgentName),
        };

        await RunAsync(services, input);

        Assert.Equal(
            [new string('y', 40), "current"],
            client.LastMessages!.Select(message => message.Text).ToArray());
    }

    [Fact]
    public async Task Step_RejectsInvalidInheritedWorkerBudget_InsteadOfDisablingIt()
    {
        var client = new RecordingChatClient();
        using var services = BuildServices(client, options => options.AddDurableAgent(AgentName, agent =>
        {
            agent.ChatClient = _ => client;
            agent.MaxModelContextTokens = 30;
        }));

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => RunAsync(services, MakeInput("hello")));

        Assert.Equal("DurableConfigurationException", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public void InputBuilder_RejectsInvalidInheritedBudget()
    {
        var options = new TemporalAgentsOptions { DefaultModelOutputTokenReserve = 40 };
        options.AddDurableAgent(AgentName, agent =>
        {
            agent.ChatClient = _ => new RecordingChatClient();
            agent.MaxModelContextTokens = 30;
        });

        var failure = Assert.Throws<InvalidOperationException>(() =>
            DefaultTemporalAgentClient.BuildWorkerAgentWorkflowInputCore(AgentName, options, "worker"));

        Assert.Contains("effective model-context token budget", failure.Message);
    }

    private static AgentStepInput MakeInput(
        string text,
        int? frontendLimit = null,
        int frontendReserve = 1024) => new()
        {
            AgentName = AgentName,
            Request = new RunRequest(text),
            AccumulatedMessages = [new(ChatRole.User, text)],
            SessionId = TemporalAgentSessionId.WithRandomKey(AgentName),
            MaxModelContextTokens = frontendLimit,
            ModelOutputTokenReserve = frontendReserve,
        };

    private static ServiceProvider BuildServices(
        RecordingChatClient client,
        Action<TemporalAgentsOptions>? registerAgent = null,
        IChatReducer? reducer = null)
    {
        var options = new TemporalAgentsOptions
        {
            DefaultMaxModelContextTokens = 100,
            DefaultModelOutputTokenReserve = 40,
        };
        if (registerAgent is null)
        {
            options.AddDurableAgent(AgentName, agent => agent.ChatClient = _ => client);
        }
        else
        {
            registerAgent(options);
        }

        var services = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton<IDurableChatTokenEstimator>(new TextLengthEstimator());
        if (reducer is not null)
        {
            services.AddKeyedSingleton<IChatReducer>("recent", reducer);
        }

        return services.BuildServiceProvider();
    }

    private static Task<AgentStepResult> RunAsync(ServiceProvider services, AgentStepInput input)
    {
        var activities = new AgentActivities(services, services.GetRequiredService<IServiceScopeFactory>());
        var environment = new ActivityEnvironment { TemporalClient = A.Fake<ITemporalClient>() };
        return environment.RunAsync(() => activities.RunDurableAgentStepAsync(input));
    }

    private sealed class TextLengthEstimator : IDurableChatTokenEstimator
    {
        public int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options) =>
            messages.Sum(message => message.Text?.Length ?? 0) + (options.Instructions?.Length ?? 0);
    }

    private sealed class InstructionsProvider(string instructions) : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AIContext
            {
                Instructions = instructions,
                Messages = context.AIContext.Messages,
            });
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = messages.ToArray();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = messages.ToArray();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
