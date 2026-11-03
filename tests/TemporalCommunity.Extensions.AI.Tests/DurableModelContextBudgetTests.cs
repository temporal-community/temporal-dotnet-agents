using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableModelContextBudgetTests
{
    [Fact]
    public void Fit_TrimsOnlyOldestCompleteTurns_AndKeepsCurrentTurn()
    {
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "old turn one"),
            new(ChatRole.User, "old turn two"),
            new(ChatRole.User, "current turn"),
        };
        var options = new ChatOptions();

        var fitted = DurableModelContextBudget.Fit(
            messages,
            currentTurnStartIndex: 2,
            previousTurnRanges: [new(0, 1, false), new(1, 2, false)],
            options,
            maximumContextTokens: 24,
            outputTokenReserve: 4,
            new CountingEstimator());

        Assert.Collection(
            fitted,
            message => Assert.Equal("old turn two", message.Text),
            message => Assert.Equal("current turn", message.Text));
    }

    [Fact]
    public void Fit_DoesNotTrimProtectedHistoryOrCurrentTurn()
    {
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "protected approval context"),
            new(ChatRole.User, "current turn"),
        };

        var exception = Assert.Throws<DurableContextBudgetExceededException>(() =>
            DurableModelContextBudget.Fit(
                messages,
                currentTurnStartIndex: 1,
                previousTurnRanges: [new(0, 1, true)],
                new ChatOptions(),
                maximumContextTokens: 15,
                outputTokenReserve: 5,
                new CountingEstimator()));

        Assert.Equal(20, exception.EstimatedInputTokens);
        Assert.Equal(5, exception.OutputTokenReserve);
        Assert.Equal(15, exception.MaximumContextTokens);
    }

    [Fact]
    public void Fit_PassesEffectiveInstructionsToolsAndSchemaToCustomEstimator()
    {
        var options = new ChatOptions
        {
            Instructions = "instructions",
            Tools = [AIFunctionFactory.Create((string value) => value, name: "echo")],
            ResponseFormat = ChatResponseFormat.Text,
        };
        var estimator = new CapturingEstimator();

        var fitted = DurableModelContextBudget.Fit(
            [new ChatMessage(ChatRole.User, "current")],
            currentTurnStartIndex: 0,
            previousTurnRanges: [],
            options,
            maximumContextTokens: 10,
            outputTokenReserve: 2,
            estimator);

        Assert.Single(fitted);
        Assert.Same(options, estimator.Options);
        Assert.Equal("instructions", estimator.Options!.Instructions);
        Assert.Single(estimator.Options.Tools!);
        Assert.Same(ChatResponseFormat.Text, estimator.Options.ResponseFormat);
    }

    [Fact]
    public void EnsureFits_RejectsProviderAddedContextThatExceedsBudget()
    {
        var exception = Assert.Throws<DurableContextBudgetExceededException>(() =>
            DurableModelContextBudget.EnsureFits(
                [
                    new ChatMessage(ChatRole.System, "provider context"),
                    new ChatMessage(ChatRole.User, "current"),
                ],
                new ChatOptions { Instructions = "instructions" },
                maximumContextTokens: 10,
                outputTokenReserve: 2,
                new CountingEstimator()));

        Assert.Equal(20, exception.EstimatedInputTokens);
    }

    [Fact]
    public async Task GetResponseAsync_SendsOnlyFittedCompletePriorTurnsToModel()
    {
        var client = new CapturingChatClient();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .AddSingleton<IDurableChatTokenEstimator>(new CountingEstimator())
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);
        var input = new DurableChatInput
        {
            Messages =
            [
                new ChatMessage(ChatRole.User, "old turn one"),
                new ChatMessage(ChatRole.User, "old turn two"),
                new ChatMessage(ChatRole.User, "current turn"),
            ],
            CurrentTurnStartIndex = 2,
            PreviousTurnRanges = [new(0, 1, false), new(1, 2, false)],
            MaxModelContextTokens = 24,
            ModelOutputTokenReserve = 4,
        };

        await activities.GetResponseAsync(input);

        Assert.Collection(
            client.Messages!,
            message => Assert.Equal("old turn two", message.Text),
            message => Assert.Equal("current turn", message.Text));
    }

    [Fact]
    public void Fit_RejectsInvalidTurnRanges()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DurableModelContextBudget.Fit(
                [new ChatMessage(ChatRole.User, "current")],
                currentTurnStartIndex: 1,
                previousTurnRanges: [new(1, 2, false)],
                new ChatOptions(),
                maximumContextTokens: 10,
                outputTokenReserve: 1,
                new CountingEstimator()));
    }

    private sealed class CountingEstimator : IDurableChatTokenEstimator
    {
        public int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options) =>
            messages.Count * 10;
    }

    private sealed class CapturingEstimator : IDurableChatTokenEstimator
    {
        public ChatOptions? Options { get; private set; }

        public int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options)
        {
            Options = options;
            return 1;
        }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? Messages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Messages = messages.ToArray();
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.Messages = messages.ToArray();
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
