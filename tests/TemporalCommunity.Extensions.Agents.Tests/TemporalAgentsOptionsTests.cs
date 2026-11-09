using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Temporalio.Common;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

public class TemporalAgentsOptionsTests
{
    private static Func<IServiceProvider, IChatClient> NewChatClient() =>
        _ => A.Fake<IChatClient>();

    [Fact]
    public void DefaultTimeToLive_Is14Days()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(TimeSpan.FromDays(14), options.DefaultTimeToLive);
    }

    [Fact]
    public void DefaultActivityTimeout_Is5Minutes()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(TimeSpan.FromMinutes(5), options.DefaultActivityTimeout);
    }

    [Fact]
    public void DefaultHeartbeatTimeout_Is2Minutes()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(TimeSpan.FromMinutes(2), options.DefaultHeartbeatTimeout);
    }

    [Fact]
    public void DefaultApprovalTimeout_Is7Days()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(TimeSpan.FromDays(7), options.DefaultApprovalTimeout);
    }

    [Fact]
    public void DefaultMaxEntryCount_Is1000()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(1000, options.DefaultMaxEntryCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Validate_RejectsDefaultMaxEntryCountThatCannotRetainACompleteTurn(int maxEntryCount)
    {
        var options = new TemporalAgentsOptions { DefaultMaxEntryCount = maxEntryCount };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("DefaultMaxEntryCount", exception.Message);
    }

    [Fact]
    public void DefaultRetryPolicy_DefaultsToNull()
    {
        var options = new TemporalAgentsOptions();
        Assert.Null(options.DefaultRetryPolicy);
    }

    [Fact]
    public void EnableSearchAttributes_DefaultsTrue()
    {
        var options = new TemporalAgentsOptions();
        Assert.True(options.EnableSearchAttributes);
    }

    [Fact]
    public void TimeToLive_NoPerAgentTtl_UsesWorkerDefault()
    {
        var options = new TemporalAgentsOptions { DefaultTimeToLive = TimeSpan.FromDays(7) };
        options.AddDurableAgent("Agent", agent => agent.ChatClient = NewChatClient());

        Assert.Null(options.DurableAgentRegistrations["Agent"].TimeToLive);
        Assert.Equal(TimeSpan.FromDays(7), options.DefaultTimeToLive);
    }

    [Fact]
    public void TimeToLive_PerAgentOverride_IsStoredOnRegistration()
    {
        var options = new TemporalAgentsOptions { DefaultTimeToLive = TimeSpan.FromDays(14) };
        var perAgentTtl = TimeSpan.FromHours(1);

        options.AddDurableAgent("FastAgent", agent =>
        {
            agent.ChatClient = NewChatClient();
            agent.TimeToLive = perAgentTtl;
        });

        Assert.Equal(perAgentTtl, options.DurableAgentRegistrations["FastAgent"].TimeToLive);
    }

    [Fact]
    public void GetRegisteredAgentNames_ReturnsAllNames()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Alpha", agent => agent.ChatClient = NewChatClient());
        options.AddDurableAgent("Beta", agent => agent.ChatClient = NewChatClient());
        options.AddAgentProxy("Gamma");

        var names = options.GetRegisteredAgentNames();
        Assert.Equal(3, names.Count);
        Assert.Contains("Alpha", names);
        Assert.Contains("Beta", names);
        Assert.Contains("Gamma", names);
    }

    [Fact]
    public void GetRegisteredAgentNames_Empty_ReturnsEmpty()
    {
        var options = new TemporalAgentsOptions();
        Assert.Empty(options.GetRegisteredAgentNames());
    }

    [Fact]
    public void IsAgentRegistered_DurableAgent_ReturnsTrue()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", agent => agent.ChatClient = NewChatClient());
        Assert.True(options.IsAgentRegistered("MyAgent"));
    }

    [Fact]
    public void IsAgentRegistered_ProxyAgent_ReturnsTrue()
    {
        var options = new TemporalAgentsOptions();
        options.AddAgentProxy("ProxyAgent");
        Assert.True(options.IsAgentRegistered("ProxyAgent"));
    }

    [Fact]
    public void IsAgentRegistered_CaseInsensitive()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", agent => agent.ChatClient = NewChatClient());

        Assert.True(options.IsAgentRegistered("myagent"));
        Assert.True(options.IsAgentRegistered("MYAGENT"));
    }

    [Fact]
    public void IsAgentRegistered_UnknownName_ReturnsFalse()
    {
        var options = new TemporalAgentsOptions();
        Assert.False(options.IsAgentRegistered("DoesNotExist"));
    }

    [Fact]
    public void IsAgentRegistered_NullOrEmpty_ReturnsFalse()
    {
        var options = new TemporalAgentsOptions();
        Assert.False(options.IsAgentRegistered(null!));
        Assert.False(options.IsAgentRegistered(""));
    }

    [Fact]
    public void AddDurableAgent_WithDescription_AppearsInGetAgentDescriptors()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("SpecialistAgent", agent =>
        {
            agent.Description = "Handles specialist tasks.";
            agent.ChatClient = NewChatClient();
        });

        var descriptors = options.GetAgentDescriptors();
        Assert.Single(descriptors);
        Assert.Equal("SpecialistAgent", descriptors[0].Name);
        Assert.Equal("Handles specialist tasks.", descriptors[0].Description);
    }

    [Fact]
    public void AddDurableAgent_WithoutDescription_ExcludedFromGetAgentDescriptors()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("NoDescriptionAgent", agent => agent.ChatClient = NewChatClient());
        Assert.Empty(options.GetAgentDescriptors());
    }

    [Fact]
    public void GetAgentDescription_ReturnsStoredDescription()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", agent =>
        {
            agent.Description = "Does something useful.";
            agent.ChatClient = NewChatClient();
        });

        Assert.Equal("Does something useful.", options.GetAgentDescription("MyAgent"));
    }

    [Fact]
    public void GetAgentDescription_CaseInsensitive()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", agent =>
        {
            agent.Description = "Does something useful.";
            agent.ChatClient = NewChatClient();
        });

        Assert.Equal("Does something useful.", options.GetAgentDescription("myagent"));
    }

    [Fact]
    public void GetAgentDescription_UnknownAgent_ReturnsNull()
    {
        var options = new TemporalAgentsOptions();
        Assert.Null(options.GetAgentDescription("DoesNotExist"));
    }

    [Fact]
    public void GetAgentDescription_NullOrEmpty_ReturnsNull()
    {
        var options = new TemporalAgentsOptions();
        Assert.Null(options.GetAgentDescription(null!));
        Assert.Null(options.GetAgentDescription(""));
    }

    [Fact]
    public void AddAgentProxy_DuplicateName_Throws()
    {
        var options = new TemporalAgentsOptions();
        options.AddAgentProxy("Agent");

        Assert.Throws<InvalidOperationException>(() => options.AddAgentProxy("Agent"));
    }

    [Fact]
    public void Agent_MapsMafOptionsAndTemporalSettings()
    {
        static string GetWeather(string city) => $"Weather in {city}.";

        var tool = AIFunctionFactory.Create(GetWeather, name: "get_weather");
        var firstProvider = new TestContextProvider();
        var secondProvider = new TestContextProvider();
        var chatClient = A.Fake<IChatClient>();
        var agentOptions = new ChatClientAgentOptions
        {
            Name = "Weather",
            Description = "Answers weather questions.",
            ChatOptions = new ChatOptions
            {
                Instructions = "Use the weather tool.",
                Temperature = 0.25f,
                Tools = [tool],
            },
            AIContextProviders = [firstProvider, secondProvider],
        };
        Action<TemporalAgentOptions> configureTemporalOptions = temporal =>
        {
            temporal.TimeToLive = TimeSpan.FromHours(2);
            temporal.ActivityTimeout = TimeSpan.FromMinutes(3);
            temporal.ApprovalTimeout = TimeSpan.FromMinutes(7);
            temporal.MaxEntryCount = 40;
            temporal.MaxModelContextTokens = 8192;
            temporal.ModelOutputTokenReserve = 256;
            temporal.HeartbeatTimeout = TimeSpan.FromSeconds(12);
            temporal.RetryPolicy = new RetryPolicy { MaximumAttempts = 2 };
            temporal.ToolMiddleware = (_, context, next, cancellationToken) =>
                next(context, cancellationToken);
            temporal.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(_ => A.Fake<VectorStore>(), "memory", 1536);
                memory.WithRecall(options =>
                    options.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.BeforeAIInvoke);
            });
        };

        var options = new TemporalAgentsOptions().Agent(
            "Weather",
            _ => chatClient,
            agentOptions,
            configureTemporalOptions);

        var registration = options.DurableAgentRegistrations["Weather"];
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        Assert.Equal("Answers weather questions.", registration.Description);
        Assert.Equal("Use the weather tool.", registration.Instructions);
        Assert.Equal(0.25f, registration.ChatOptions!.Temperature);
        Assert.Same(chatClient, registration.ChatClient(serviceProvider));
        Assert.Same(tool, registration.Tools.Single().Factory(serviceProvider));
        Assert.Collection(
            registration.ContextProviderFactories.Take(2),
            provider => Assert.Same(firstProvider, provider.Factory(serviceProvider)),
            provider => Assert.Same(secondProvider, provider.Factory(serviceProvider)));
        Assert.Equal(TimeSpan.FromHours(2), registration.TimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(3), registration.ActivityTimeout);
        Assert.Equal(TimeSpan.FromMinutes(7), registration.ApprovalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), registration.HeartbeatTimeout);
        Assert.Equal(2, registration.RetryPolicy!.MaximumAttempts);
        Assert.Equal(40, registration.MaxEntryCount);
        Assert.Equal(8192, registration.MaxModelContextTokens);
        Assert.Equal(256, registration.ModelOutputTokenReserve);
        Assert.NotNull(registration.ToolMiddleware);
        Assert.NotNull(registration.ContextProviderFactories[^1].MemorySearch);
    }

    [Fact]
    public void Agent_RegisteredChatClientOverloadUsesTemporalDefaultsWhenCallbackOmitted()
    {
        var options = new TemporalAgentsOptions().Agent(
            "Weather",
            new ChatClientAgentOptions());

        var registration = options.DurableAgentRegistrations["Weather"];

        Assert.Equal(TimeSpan.FromDays(14), options.DefaultTimeToLive);
        Assert.Null(registration.ActivityTimeout);
        Assert.Null(registration.HeartbeatTimeout);
        Assert.Null(registration.ToolMiddleware);
        Assert.Null(registration.ContextProviderFactories.SingleOrDefault()?.MemorySearch);
    }

    [Fact]
    public void Agent_ChatClientFactoryOverloadUsesTemporalDefaultsWhenCallbackOmitted()
    {
        var chatClient = A.Fake<IChatClient>();
        var options = new TemporalAgentsOptions().Agent(
            "Weather",
            _ => chatClient,
            new ChatClientAgentOptions());

        var registration = options.DurableAgentRegistrations["Weather"];

        Assert.Same(chatClient, registration.ChatClient(new ServiceCollection().BuildServiceProvider()));
        Assert.Equal(TimeSpan.FromDays(14), options.DefaultTimeToLive);
        Assert.Null(registration.ActivityTimeout);
        Assert.Null(registration.ToolMiddleware);
        Assert.Empty(registration.ContextProviderFactories);
    }

    [Fact]
    public void Agent_RejectsHistoryProvider()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            new TemporalAgentsOptions().Agent(
                "Weather",
                _ => A.Fake<IChatClient>(),
                new ChatClientAgentOptions
                {
                    ChatHistoryProvider = A.Fake<ChatHistoryProvider>(),
                }));

        Assert.Contains(nameof(ChatClientAgentOptions.ChatHistoryProvider), exception.Message);
        Assert.Contains("workflow owns chat history", exception.Message);
    }

    [Fact]
    public void Agent_RejectsUnsupportedFunctionInvocationOptions()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            new TemporalAgentsOptions().Agent(
                "Weather",
                _ => A.Fake<IChatClient>(),
                new ChatClientAgentOptions { AllowConcurrentInvocation = true }));

        Assert.Contains(nameof(ChatClientAgentOptions.AllowConcurrentInvocation), exception.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Agent_RejectsServiceManagedChatHistory(bool hasConversationId, bool allowBackgroundResponses)
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            new TemporalAgentsOptions().Agent(
                "Weather",
                _ => A.Fake<IChatClient>(),
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions
                    {
                        ConversationId = hasConversationId ? "service-conversation" : null,
                        AllowBackgroundResponses = allowBackgroundResponses ? true : null,
                    },
                }));

        Assert.Contains("workflow owns session history", exception.Message);
    }

    [Fact]
    public void Agent_RejectsNonFunctionTools()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            new TemporalAgentsOptions().Agent(
                "Weather",
                _ => A.Fake<IChatClient>(),
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok"), new TestTool()] },
                }));

        Assert.Contains("only AIFunction", exception.Message);
    }

    private sealed class TestContextProvider : AIContextProvider
    {
    }

    private sealed class TestTool : AITool
    {
        public override string Name => "test-tool";
    }
}
