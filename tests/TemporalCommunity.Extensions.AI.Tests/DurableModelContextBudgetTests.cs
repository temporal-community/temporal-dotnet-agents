using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
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
    public void Fit_DefaultEstimatorPreservesSerializedSuffixEstimateAndFixedContext()
    {
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "old request — café 🌍"),
            new(ChatRole.Assistant, [
                new FunctionCallContent("call-1", "echo", new Dictionary<string, object?> { ["value"] = "old" })
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "old result")]),
            new(ChatRole.Assistant, "old response"),
            new(ChatRole.User, "recent request"),
            new(ChatRole.Assistant, "recent response"),
            new(ChatRole.User, "current request"),
        };
        var options = new ChatOptions
        {
            Instructions = "Answer concisely — résumé 🧭.",
            Tools = [AIFunctionFactory.Create((string value) => value, name: "echo")],
            ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    answer = new { type = "string", description = "Unicode-safe response — 東京" },
                    confidence = new { type = "number", minimum = 0, maximum = 1 },
                },
                required = new[] { "answer", "confidence" },
                additionalProperties = false,
            })),
        };
        var expectedSuffix = messages.Skip(4).ToArray();
        const int outputTokenReserve = 17;
        var maximumContextTokens = EstimateSerializedInputTokens(expectedSuffix, options) + outputTokenReserve;

        var fitted = DurableModelContextBudget.Fit(
            messages,
            currentTurnStartIndex: 6,
            previousTurnRanges: [new(0, 4, false), new(4, 6, false)],
            options,
            maximumContextTokens,
            outputTokenReserve,
            estimator: null);

        Assert.Equal(expectedSuffix.Select(message => message.Text), fitted.Select(message => message.Text));
    }

    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    public void Fit_DefaultEstimatorSerializesEachMessageOnceWhileLegacySuffixEstimatesAreQuadratic(int messageCount)
    {
        var messages = Enumerable.Range(0, messageCount)
            .Select(index => new ChatMessage(ChatRole.User, $"message-{index}")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["serializationProbe"] = new SerializationProbe(),
                },
            })
            .ToArray();
        var options = new ChatOptions();
        var probe = messages
            .Select(message => (SerializationProbe)message.AdditionalProperties!["serializationProbe"]!)
            .ToArray();
        var ranges = Enumerable.Range(0, messageCount - 1)
            .Select(index => new DurableChatTurnRange(index, index + 1, false))
            .ToArray();
        var maximumContextTokens = EstimateSerializedInputTokens(messages[^1..], options);
        foreach (var item in probe)
        {
            item.Reset();
        }

        var fitted = DurableModelContextBudget.Fit(
            messages,
            currentTurnStartIndex: messageCount - 1,
            previousTurnRanges: ranges,
            options,
            maximumContextTokens,
            outputTokenReserve: 0,
            estimator: null);
        var optimizedVisits = probe.Sum(item => item.ReadCount);

        foreach (var item in probe)
        {
            item.Reset();
        }

        LegacyFitWithDefaultEstimator(messages, messageCount - 1, ranges, options, maximumContextTokens);
        var legacyVisits = probe.Sum(item => item.ReadCount);

        Assert.Equal(messages[^1].Text, Assert.Single(fitted).Text);
        Assert.Equal(messageCount, optimizedVisits);
        Assert.Equal(messageCount * (messageCount + 1) / 2, legacyVisits);
        Assert.True(legacyVisits >= optimizedVisits * (messageCount / 2));
    }

    [Fact]
    public void Fit_CustomEstimatorRetainsNonMonotonicSuffixSemantics()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "oldest"),
            new ChatMessage(ChatRole.User, "middle"),
            new ChatMessage(ChatRole.User, "newest"),
            new ChatMessage(ChatRole.User, "current"),
        };
        var estimator = new NonMonotonicEstimator();

        var fitted = DurableModelContextBudget.Fit(
            messages,
            currentTurnStartIndex: 3,
            previousTurnRanges: [new(0, 1, false), new(1, 2, false), new(2, 3, false)],
            new ChatOptions(),
            maximumContextTokens: 50,
            outputTokenReserve: 0,
            estimator);

        Assert.Equal(new[] { "newest", "current" }, fitted.Select(message => message.Text));
        Assert.Equal(new[] { 4, 3, 2 }, estimator.MessageCounts);
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
    public async Task GetResponseAsync_AppliesContextReducerOnlyToEligiblePriorTurns()
    {
        var client = new CapturingChatClient();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .AddKeyedSingleton<IChatReducer>("recent", new MessageCountingChatReducer(1))
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);
        var input = new DurableChatInput
        {
            Messages =
            [
                new ChatMessage(ChatRole.System, "system instructions"),
                new ChatMessage(ChatRole.User, "old request"),
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "old result")]),
                new ChatMessage(ChatRole.Assistant, "old response"),
                new ChatMessage(ChatRole.User, "current request"),
            ],
            ContextReducerKey = "recent",
            CurrentTurnStartIndex = 5,
            PreviousTurnRanges = [new(1, 5, false)],
        };

        await activities.GetResponseAsync(input);

        Assert.Collection(
            client.Messages!,
            message => Assert.Equal("system instructions", message.Text),
            message => Assert.Equal("old response", message.Text),
            message => Assert.Equal("current request", message.Text));
        Assert.Equal("old request", input.Messages[1].Text);
        Assert.Null(input.Messages[1].AdditionalProperties);
    }

    [Fact]
    public async Task GetChatStepAsync_AppliesContextReducerOnlyToEligiblePriorTurns()
    {
        var client = new CapturingChatClient();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .AddKeyedSingleton<IChatReducer>("recent", new MessageCountingChatReducer(1))
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);
        var input = new DurableChatInput
        {
            Messages =
            [
                new ChatMessage(ChatRole.System, "system instructions"),
                new ChatMessage(ChatRole.User, "old request"),
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "old result")]),
                new ChatMessage(ChatRole.Assistant, "old response"),
                new ChatMessage(ChatRole.User, "current request"),
            ],
            ContextReducerKey = "recent",
            CurrentTurnStartIndex = 5,
            PreviousTurnRanges = [new(1, 5, false)],
        };

        var result = await activities.GetChatStepAsync(input);

        Assert.True(result.IsFinal);
        Assert.Collection(
            client.Messages!,
            message => Assert.Equal("system instructions", message.Text),
            message => Assert.Equal("old response", message.Text),
            message => Assert.Equal("current request", message.Text));
        Assert.Equal("old request", input.Messages[1].Text);
        Assert.Null(input.Messages[1].AdditionalProperties);
    }

    [Fact]
    public async Task GetResponseAsync_RejectsReducerOutputThatStillExceedsBudgetBeforeProviderCall()
    {
        var client = new CapturingChatClient();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .AddSingleton<IDurableChatTokenEstimator>(new CountingEstimator())
            .AddKeyedSingleton<IChatReducer>("recent", new ExpandingReducer())
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);
        var input = new DurableChatInput
        {
            Messages =
            [
                new ChatMessage(ChatRole.User, "old request"),
                new ChatMessage(ChatRole.Assistant, "old response"),
                new ChatMessage(ChatRole.User, "current request"),
            ],
            ContextReducerKey = "recent",
            CurrentTurnStartIndex = 2,
            PreviousTurnRanges = [new(0, 2, false)],
            MaxModelContextTokens = 40,
            ModelOutputTokenReserve = 1,
        };

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => activities.GetResponseAsync(input));

        Assert.True(exception.NonRetryable);
        Assert.Equal("DurableContextBudgetExceeded", exception.ErrorType);
        Assert.Null(client.Messages);
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

    [Theory]
    [InlineData("system")]
    [InlineData("developer")]
    public void Fit_DoesNotDropInstructionBearingPriorTurn(string role)
    {
        Assert.Throws<DurableContextBudgetExceededException>(() =>
            DurableModelContextBudget.Fit(
                [new ChatMessage(new ChatRole(role), "keep instructions"), new(ChatRole.User, "current")],
                currentTurnStartIndex: 1,
                previousTurnRanges: [new(0, 1, false)],
                new ChatOptions(),
                maximumContextTokens: 10,
                outputTokenReserve: 0,
                new CountingEstimator()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ContextReducer_RunsBeforeBudgetFitAndPreservesProtectedPrefix(
        bool managedStep,
        bool protectedPrefix)
    {
        var client = new CapturingChatClient();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .AddSingleton<IDurableChatTokenEstimator>(new CountingEstimator())
            .AddKeyedSingleton<IChatReducer>("recent", new MessageCountingChatReducer(1))
            .BuildServiceProvider();
        var input = new DurableChatInput
        {
            Messages =
            [
                new ChatMessage(protectedPrefix ? ChatRole.System : ChatRole.User, "prefix"),
                new ChatMessage(ChatRole.User, "old request"),
                new ChatMessage(ChatRole.Assistant, "old response"),
                new ChatMessage(ChatRole.User, "current"),
            ],
            CurrentTurnStartIndex = 3,
            PreviousTurnRanges = protectedPrefix ? [new(1, 3, false)] : [new(0, 3, false)],
            ContextReducerKey = "recent",
            MaxModelContextTokens = protectedPrefix ? 30 : 20,
            ModelOutputTokenReserve = 0,
        };
        var activities = new DurableChatActivities(services, loggerFactory: null);

        if (managedStep)
        {
            await activities.GetChatStepAsync(input);
        }
        else
        {
            await activities.GetResponseAsync(input);
        }

        var expected = protectedPrefix
            ? new[] { "prefix", "old response", "current" }
            : new[] { "old response", "current" };
        Assert.Equal(expected, client.Messages!.Select(message => message.Text));
        Assert.Equal(4, input.Messages.Count);
        Assert.Equal("old request", input.Messages[1].Text);
    }

    private sealed class CountingEstimator : IDurableChatTokenEstimator
    {
        public int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options) =>
            messages.Count * 10;
    }

    private sealed class ExpandingReducer : IChatReducer
    {
        public Task<IEnumerable<ChatMessage>> ReduceAsync(
            IEnumerable<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IEnumerable<ChatMessage>>(
            [
                new(ChatRole.User, "summary part 1"),
                new(ChatRole.Assistant, "summary part 2"),
                new(ChatRole.User, "summary part 3"),
                new(ChatRole.Assistant, "summary part 4"),
                new(ChatRole.User, "summary part 5"),
            ]);
        }
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

    private sealed class NonMonotonicEstimator : IDurableChatTokenEstimator
    {
        public List<int> MessageCounts { get; } = [];

        public int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options)
        {
            this.MessageCounts.Add(messages.Count);
            return messages.Count switch
            {
                4 => 90,
                3 => 100,
                2 => 1,
                _ => throw new InvalidOperationException("Unexpected suffix length."),
            };
        }
    }

    private static int EstimateSerializedInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options)
    {
        var byteCount = JsonSerializer.SerializeToUtf8Bytes(
            messages,
            AIJsonUtilities.DefaultOptions).Length;

        if (!string.IsNullOrEmpty(options.Instructions))
        {
            byteCount += Encoding.UTF8.GetByteCount(options.Instructions);
        }

        if (options.ResponseFormat is not null)
        {
            byteCount += JsonSerializer.SerializeToUtf8Bytes(
                options.ResponseFormat,
                AIJsonUtilities.DefaultOptions).Length;
        }

        if (options.Tools is not null)
        {
            foreach (var tool in options.Tools)
            {
                byteCount += Encoding.UTF8.GetByteCount(tool.Name);
                byteCount += Encoding.UTF8.GetByteCount(tool.Description);
                if (tool is AIFunction function)
                {
                    byteCount += Encoding.UTF8.GetByteCount(function.JsonSchema.GetRawText());
                }
            }
        }

        return checked((byteCount + 2) / 3 + (messages.Count * 4));
    }

    private static IList<ChatMessage> LegacyFitWithDefaultEstimator(
        IReadOnlyList<ChatMessage> messages,
        int currentTurnStartIndex,
        IReadOnlyList<DurableChatTurnRange> previousTurnRanges,
        ChatOptions options,
        int maximumContextTokens)
    {
        var estimate = EstimateSerializedInputTokens(messages, options);
        if (estimate <= maximumContextTokens)
        {
            return messages.ToList();
        }

        var dropThrough = 0;
        foreach (var range in previousTurnRanges)
        {
            if (range.StartIndex != dropThrough
                || range.EndIndex <= range.StartIndex
                || range.EndIndex > currentTurnStartIndex
                || range.IsProtected
                || messages.Skip(range.StartIndex).Take(range.EndIndex - range.StartIndex)
                    .Any(message => message.Role == ChatRole.System || message.Role == new ChatRole("developer")))
            {
                break;
            }

            dropThrough = range.EndIndex;
            var candidate = messages.Skip(dropThrough).ToArray();
            estimate = EstimateSerializedInputTokens(candidate, options);
            if (estimate <= maximumContextTokens)
            {
                return candidate;
            }
        }

        throw new DurableContextBudgetExceededException(estimate, 0, maximumContextTokens);
    }

    public sealed class SerializationProbe
    {
        private int readCount;

        public int ReadCount => this.readCount;

        public int Value
        {
            get
            {
                this.readCount++;
                return 1;
            }
        }

        public void Reset() => this.readCount = 0;
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
