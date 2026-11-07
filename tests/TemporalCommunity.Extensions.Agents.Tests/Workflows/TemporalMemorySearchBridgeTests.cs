#pragma warning disable MAAI001 // Intentional coverage of the experimental MAF context-provider surface.
using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

public sealed class TemporalMemorySearchBridgeTests
{
    [Fact]
    public void Builder_UsesLibraryOwnedStateKeyAndSupportsBothSearchModes()
    {
        var builder = new TemporalChatHistoryMemoryBuilder()
            .UseVectorStore(_ => A.Fake<VectorStore>(), "memories", 1536)
            .WithRecall(options =>
                options.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling);

        var registration = builder.CreateRegistration("MemoryAgent");
        var provider = registration.ProviderFactory(new ServiceCollection().BuildServiceProvider());

        Assert.Equal("memories", registration.CollectionName);
        Assert.Equal(
            ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling,
            registration.SearchTime);
        Assert.Equal(TemporalChatHistoryMemoryBuilder.StateKey, Assert.Single(provider.StateKeys));
        Assert.Null(registration.ScopeInitializer);

        var beforeCallBuilder = new TemporalChatHistoryMemoryBuilder()
            .UseVectorStore(_ => A.Fake<VectorStore>(), "memories", 1536)
            .WithRecall(options =>
                options.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.BeforeAIInvoke);

        Assert.Equal(
            ChatHistoryMemoryProviderOptions.SearchBehavior.BeforeAIInvoke,
            beforeCallBuilder.CreateRegistration("MemoryAgent").SearchTime);
    }

    [Fact]
    public void Builder_AutomaticRecallDoesNotReserveSearchToolName()
    {
        var options = new TemporalAgentOptions().UseChatHistoryMemory(memory =>
        {
            memory.UseVectorStore(_ => A.Fake<VectorStore>(), "memories", 1536);
            memory.WithRecall(recall =>
                recall.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.BeforeAIInvoke);
        });
        var builder = new DurableAgentBuilder("MemoryAgent")
        {
            ChatClient = _ => A.Fake<IChatClient>(),
        };
        builder.AddTool(AIFunctionFactory.Create(() => "application search", "Search"));

        options.ApplyTo(builder);

        Assert.Equal("Search", Assert.Single(builder.ToolRegistrations).Name);
        Assert.Single(builder.ContextProviderFactories);
    }

    [Fact]
    public void Builder_OnDemandRecallHonorsConfiguredToolNameAndRejectsCollision()
    {
        var options = new TemporalAgentOptions().UseChatHistoryMemory(memory =>
        {
            memory.UseVectorStore(_ => A.Fake<VectorStore>(), "memories", 1536);
            memory.WithRecall(recall =>
            {
                recall.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling;
                recall.FunctionToolName = "Recall";
            });
        });
        var noCollisionBuilder = new DurableAgentBuilder("MemoryAgent")
        {
            ChatClient = _ => A.Fake<IChatClient>(),
        };

        options.ApplyTo(noCollisionBuilder);

        Assert.Equal("Recall", Assert.Single(noCollisionBuilder.ToolRegistrations).Name);

        var builder = new DurableAgentBuilder("MemoryAgent")
        {
            ChatClient = _ => A.Fake<IChatClient>(),
        };
        builder.AddTool(AIFunctionFactory.Create(() => "application search", "Recall"));

        var exception = Assert.Throws<ArgumentException>(() => options.ApplyTo(builder));

        Assert.Contains("Recall", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_RejectsCallerOwnedMemoryStateKey()
    {
        var options = new TemporalAgentOptions().UseChatHistoryMemory(memory =>
        {
            memory.UseVectorStore(_ => A.Fake<VectorStore>(), "memories", 1536);
            memory.WithRecall(recall => recall.StateKey = "application.memory");
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            options.ApplyTo(new DurableAgentBuilder("MemoryAgent")
            {
                ChatClient = _ => A.Fake<IChatClient>(),
            }));

        Assert.Contains("StateKey is owned by Temporal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_AllowsExplicitLibraryOwnedMemoryStateKey()
    {
        var builder = new TemporalChatHistoryMemoryBuilder()
            .UseVectorStore(_ => A.Fake<VectorStore>(), "memories", 1536)
            .WithRecall(recall => recall.StateKey = TemporalChatHistoryMemoryBuilder.StateKey);

        var provider = builder.CreateRegistration("MemoryAgent")
            .ProviderFactory(new ServiceCollection().BuildServiceProvider());

        Assert.Equal(TemporalChatHistoryMemoryBuilder.StateKey, Assert.Single(provider.StateKeys));
    }

    [Fact]
    public void Builder_RequiresVectorStoreAndValidDimensions()
    {
        var options = new TemporalAgentOptions().UseChatHistoryMemory(_ => { });
        var builder = new DurableAgentBuilder("MemoryAgent")
        {
            ChatClient = _ => A.Fake<IChatClient>(),
        };

        Assert.Throws<InvalidOperationException>(() => options.ApplyTo(builder));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TemporalChatHistoryMemoryBuilder().UseVectorStore(
                _ => A.Fake<VectorStore>(),
                "memories",
                0));
    }

    [Fact]
    public async Task Initializer_UsesDefaultSessionScopeAndRunsOnlyOnce()
    {
        var initializationCount = 0;
        var contextSeen = false;
        var (activities, environment) = BuildHarness(
            memoryOptions => memoryOptions.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(_ => A.Fake<VectorStore>(), "support-memory", 1536);
                memory.WithScopeInitializer((_, context, _) =>
                {
                    initializationCount++;
                    contextSeen = context.SessionId == "ta-MemoryAgent-session-1"
                        && context.AgentName == "MemoryAgent"
                        && context.CollectionName == "support-memory";
                    var storage = new ChatHistoryMemoryProviderScope
                    {
                        AgentId = context.AgentName,
                        SessionId = context.SessionId,
                    };
                    return Task.FromResult(new ChatHistoryMemoryProvider.State(storage));
                });
            }));

        var result = await environment.RunAsync(() =>
            activities.InitializeChatHistoryMemoryAsync(new InitializeChatHistoryMemoryInput
            {
                AgentName = "MemoryAgent",
                SessionId = "ta-MemoryAgent-session-1",
            }));

        Assert.True(contextSeen);
        Assert.Equal(1, initializationCount);
        Assert.Equal("MemoryAgent", result.Scope!.StorageAgentId);
        Assert.Equal("ta-MemoryAgent-session-1", result.Scope.StorageSessionId);
        Assert.Equal(result.Scope.StorageSessionId, result.Scope.SearchSessionId);

        var sessionId = TemporalAgentSessionId.Parse("ta-MemoryAgent-session-1");
        var session = TemporalAgentSession.FromStateBag(sessionId, serializedStateBag: null);
        session.ApplyTrustedStateBagDelta(result.StateBagDelta);
        var repeated = await environment.RunAsync(() =>
            activities.InitializeChatHistoryMemoryAsync(new InitializeChatHistoryMemoryInput
            {
                AgentName = "MemoryAgent",
                SessionId = "ta-MemoryAgent-session-1",
                SerializedStateBag = session.SerializeStateBag(),
            }));

        Assert.Equal(1, initializationCount);
        Assert.Equal(result.Scope, repeated.Scope);
        Assert.Null(repeated.StateBagDelta);
    }

    [Theory]
    [InlineData(ChatHistoryMemoryProviderOptions.SearchBehavior.BeforeAIInvoke)]
    [InlineData(ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling)]
    public async Task PrepareAgentTurn_RejectsChangedRetainedScopeBeforeMemoryRead(
        ChatHistoryMemoryProviderOptions.SearchBehavior searchBehavior)
    {
        var (activities, environment, stateBag, scope) = await CreateInitializedMemoryHarness(searchBehavior);
        var sessionId = "ta-MemoryAgent-session-1";
        var changedScope = scope with { StorageUserId = "different-user" };

        var exception = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => environment.RunAsync(() =>
                activities.PrepareAgentTurnAsync(new PrepareAgentTurnInput
                {
                    AgentName = "MemoryAgent",
                    SessionId = sessionId,
                    Messages = [new ChatMessage(ChatRole.User, "hello")],
                    SerializedStateBag = stateBag,
                    MemoryScope = changedScope,
                })));

        Assert.True(exception.NonRetryable);
        Assert.Contains("retained", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnDemandSearchReconstruction_RejectsChangedRetainedScopeBeforeMemoryRead()
    {
        var (_, _, stateBag, scope) = await CreateInitializedMemoryHarness(
            ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling);
        var sessionId = "ta-MemoryAgent-session-1";
        var registration = new TemporalChatHistoryMemoryBuilder()
            .UseVectorStore(_ => A.Fake<VectorStore>(), "support-memory", 1536)
            .WithRecall(recall =>
                recall.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling)
            .CreateRegistration("MemoryAgent");
        var provider = registration.ProviderFactory(new ServiceCollection().BuildServiceProvider());
        var session = TemporalAgentSession.FromStateBag(
            TemporalAgentSessionId.Parse(sessionId),
            stateBag);
        var providerContext = new Microsoft.Agents.AI.AIContextProvider.InvokingContext(
            A.Fake<AIAgent>(),
            session,
            new Microsoft.Agents.AI.AIContext { Messages = [] });
        var context = await provider.InvokingAsync(providerContext);
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(context.Tools!));
        var declaration = TemporalCommunity.Extensions.AI.Internal.DurableFunctionDeclarationSnapshot
            .Create(function.AsDeclarationOnly());
        var changedScope = scope with { StorageUserId = "different-user" };

        var exception = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => AgentActivities.ResolveTemporalMemorySearchFunctionAsync(
                registration,
                new PreparedAgentTool
                {
                    Name = "Search",
                    IsFrozenProviderSelection = true,
                    IsTemporalMemorySearch = true,
                    MemoryScope = changedScope,
                    MemorySearchScope = new ChatHistoryMemoryProviderScope
                    {
                        AgentId = scope.SearchAgentId,
                        SessionId = scope.SearchSessionId,
                    },
                    Declaration = declaration,
                },
                TemporalAgentSessionId.Parse(sessionId),
                stateBag,
                A.Fake<AIAgent>(),
                new ServiceCollection().BuildServiceProvider(),
                CancellationToken.None));

        Assert.True(exception.NonRetryable);
        Assert.Contains("retained", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAgentTurn_RejectsChangedRetainedScopeBeforeMemoryWrite()
    {
        var (activities, environment, stateBag, scope) = await CreateInitializedMemoryHarness(
            ChatHistoryMemoryProviderOptions.SearchBehavior.BeforeAIInvoke);
        var changedScope = scope with { StorageUserId = "different-user" };

        var exception = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => environment.RunAsync(() =>
                activities.CompleteAgentTurnAsync(new CompleteAgentTurnInput
                {
                    AgentName = "MemoryAgent",
                    SessionId = "ta-MemoryAgent-session-1",
                    RequestMessages = [new ChatMessage(ChatRole.User, "hello")],
                    ResponseMessages = [new ChatMessage(ChatRole.Assistant, "response")],
                    SerializedStateBag = stateBag,
                    MemoryScope = changedScope,
                })));

        Assert.True(exception.NonRetryable);
        Assert.Contains("retained", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ta-MemoryAgent-other-session")]
    public async Task Initializer_RejectsCrossSessionSearchWithoutOptIn(string? searchSessionId)
    {
        var (activities, environment) = BuildHarness(
            memoryOptions => memoryOptions.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(_ => A.Fake<VectorStore>(), "support-memory", 1536);
                memory.WithScopeInitializer((_, context, _) =>
                {
                    var storage = new ChatHistoryMemoryProviderScope
                    {
                        ApplicationId = "app",
                        AgentId = context.AgentName,
                        SessionId = context.SessionId,
                        UserId = "user",
                    };
                    var search = new ChatHistoryMemoryProviderScope
                    {
                        ApplicationId = "app",
                        AgentId = context.AgentName,
                        SessionId = searchSessionId,
                        UserId = "user",
                    };
                    return Task.FromResult(new ChatHistoryMemoryProvider.State(storage, search));
                });
            }));

        var exception = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => environment.RunAsync(() =>
                activities.InitializeChatHistoryMemoryAsync(new InitializeChatHistoryMemoryInput
                {
                    AgentName = "MemoryAgent",
                    SessionId = "ta-MemoryAgent-session-1",
                })));

        Assert.True(exception.NonRetryable);
        Assert.Contains(
            searchSessionId is null ? "allowCrossSessionRecall" : "current session",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initializer_RejectsForeignStorageSessionEvenWithCrossSessionOptIn(
        bool allowCrossSessionRecall)
    {
        var (activities, environment) = BuildHarness(
            memoryOptions => memoryOptions.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(_ => A.Fake<VectorStore>(), "support-memory", 1536);
                memory.WithScopeInitializer((_, context, _) =>
                {
                    var storage = new ChatHistoryMemoryProviderScope
                    {
                        ApplicationId = "app",
                        AgentId = context.AgentName,
                        SessionId = "ta-MemoryAgent-other-session",
                    };
                    return Task.FromResult(new ChatHistoryMemoryProvider.State(storage));
                }, allowCrossSessionRecall);
            }));

        var exception = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => environment.RunAsync(() =>
                activities.InitializeChatHistoryMemoryAsync(new InitializeChatHistoryMemoryInput
                {
                    AgentName = "MemoryAgent",
                    SessionId = "ta-MemoryAgent-session-1",
                })));

        Assert.True(exception.NonRetryable);
        Assert.Contains("storage scope must match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initializer_AllowsBoundedCrossSessionSearchOnlyWithOptIn()
    {
        var (activities, environment) = BuildHarness(
            memoryOptions => memoryOptions.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(_ => A.Fake<VectorStore>(), "support-memory", 1536);
                memory.WithScopeInitializer((_, context, _) =>
                {
                    var storage = new ChatHistoryMemoryProviderScope
                    {
                        ApplicationId = "app",
                        AgentId = context.AgentName,
                        SessionId = context.SessionId,
                        UserId = "user",
                    };
                    var search = new ChatHistoryMemoryProviderScope
                    {
                        ApplicationId = "app",
                        AgentId = context.AgentName,
                        UserId = "user",
                    };
                    return Task.FromResult(new ChatHistoryMemoryProvider.State(storage, search));
                }, allowCrossSessionRecall: true);
            }));

        var result = await environment.RunAsync(() =>
            activities.InitializeChatHistoryMemoryAsync(new InitializeChatHistoryMemoryInput
            {
                AgentName = "MemoryAgent",
                SessionId = "ta-MemoryAgent-session-1",
            }));

        Assert.Null(result.Scope!.SearchSessionId);
        Assert.Equal("app", result.Scope.SearchApplicationId);
        Assert.Equal("user", result.Scope.SearchUserId);
        Assert.Equal("ta-MemoryAgent-session-1", result.Scope.StorageSessionId);
    }

    [Fact]
    public async Task Initializer_RejectsUnboundedCrossSessionRecall()
    {
        var (activities, environment) = BuildHarness(
            memoryOptions => memoryOptions.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(_ => A.Fake<VectorStore>(), "support-memory", 1536);
                memory.WithScopeInitializer((_, context, _) =>
                {
                    var storage = new ChatHistoryMemoryProviderScope
                    {
                        AgentId = context.AgentName,
                        SessionId = context.SessionId,
                    };
                    var search = new ChatHistoryMemoryProviderScope
                    {
                        AgentId = context.AgentName,
                    };
                    return Task.FromResult(new ChatHistoryMemoryProvider.State(storage, search));
                }, allowCrossSessionRecall: true);
            }));

        var exception = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => environment.RunAsync(() =>
                activities.InitializeChatHistoryMemoryAsync(new InitializeChatHistoryMemoryInput
                {
                    AgentName = "MemoryAgent",
                    SessionId = "ta-MemoryAgent-session-1",
                })));

        Assert.True(exception.NonRetryable);
        Assert.Contains("bounded", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static (AgentActivities Activities, ActivityEnvironment Environment) BuildHarness(
        Action<TemporalAgentOptions> configureTemporalOptions)
    {
        var temporalAgents = (TemporalAgentsOptions)Activator.CreateInstance(
            typeof(TemporalAgentsOptions),
            nonPublic: true)!;
        temporalAgents.Agent(
            "MemoryAgent",
            _ => A.Fake<IChatClient>(),
            new ChatClientAgentOptions(),
            configureTemporalOptions);

        var services = new ServiceCollection()
            .AddSingleton(temporalAgents)
            .BuildServiceProvider();
        return (
            new AgentActivities(services, services.GetRequiredService<IServiceScopeFactory>()),
            new ActivityEnvironment { TemporalClient = A.Fake<ITemporalClient>() });
    }

    private static async Task<(
        AgentActivities Activities,
        ActivityEnvironment Environment,
        System.Text.Json.JsonElement? StateBag,
        TemporalMemoryScopeSnapshot Scope)> CreateInitializedMemoryHarness(
            ChatHistoryMemoryProviderOptions.SearchBehavior searchBehavior)
    {
        var (activities, environment) = BuildHarness(options =>
            options.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(_ => A.Fake<VectorStore>(), "support-memory", 1536);
                memory.WithRecall(recall => recall.SearchTime = searchBehavior);
            }));
        var initialized = await environment.RunAsync(() =>
            activities.InitializeChatHistoryMemoryAsync(new InitializeChatHistoryMemoryInput
            {
                AgentName = "MemoryAgent",
                SessionId = "ta-MemoryAgent-session-1",
            }));
        var stateBag = StateBagMerge.ApplyTrustedDelta(null, initialized.StateBagDelta);

        return (activities, environment, stateBag, initialized.Scope!);
    }
}
