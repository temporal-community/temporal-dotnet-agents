#pragma warning disable MAAI001 // Intentional coverage of the experimental MAF memory provider.
using FakeItEasy;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Activities;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Worker.Interceptors;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ChatHistoryMemoryIntegrationTests
{
    [Fact]
    public async Task BeforeAIInvoke_UsesSessionScopedVectorMemoryAcrossTurns()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "I will remember the user's preference.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "You prefer strong coffee.")),
        ]);
        Action<TemporalAgentOptions> options = temporal => temporal.UseChatHistoryMemory(memory =>
        {
            memory.UseVectorStore(
                services => services.GetRequiredService<VectorStore>(),
                "memory-before-invoke",
                32);
            memory.WithRecall(recall =>
            {
                recall.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.BeforeAIInvoke;
                recall.MaxResults = 3;
            });
        });

        var queue = $"memory-before-invoke-{Guid.NewGuid():N}";
        using var host = BuildHost(
            environment.Client,
            queue,
            chatClient,
            options,
            vectorStore: new InMemoryVectorStore(new InMemoryVectorStoreOptions
            {
                EmbeddingGenerator = new DeterministicEmbeddingGenerator(),
            }));
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("MemoryAgent");
            var session = await proxy.CreateSessionAsync();

            var first = await proxy.RunAsync("I prefer strong coffee.", session);
            var second = await proxy.RunAsync("What kind of coffee do I prefer?", session);

            Assert.Contains("remember", first.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("strong coffee", second.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, chatClient.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task OnDemandFunctionCalling_DispatchesScopedMemorySearchAsToolActivity()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "I will remember your coffee preference.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "memory-search-1",
                    "Search",
                    new Dictionary<string, object?> { ["userQuestion"] = "coffee preference" }),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "You prefer strong coffee.")),
        ]);
        Action<TemporalAgentOptions> options = temporal => temporal.UseChatHistoryMemory(memory =>
        {
            memory.UseVectorStore(
                services => services.GetRequiredService<VectorStore>(),
                "memory-on-demand",
                32);
            memory.WithScopeInitializer((_, context, _) =>
            {
                var storageScope = new ChatHistoryMemoryProviderScope
                {
                    ApplicationId = "coffee-app",
                    AgentId = context.AgentName,
                    SessionId = context.SessionId,
                    UserId = "user-42",
                };
                var searchScope = new ChatHistoryMemoryProviderScope
                {
                    ApplicationId = "coffee-app",
                    AgentId = context.AgentName,
                    SessionId = context.SessionId,
                    UserId = "user-42",
                };
                return Task.FromResult(
                    new ChatHistoryMemoryProvider.State(storageScope, searchScope));
            });
            memory.WithRecall(recall =>
            {
                recall.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling;
                recall.MaxResults = 3;
            });
        });

        var queue = $"memory-on-demand-{Guid.NewGuid():N}";
        using var host = BuildHost(
            environment.Client,
            queue,
            chatClient,
            options,
            vectorStore: new InMemoryVectorStore(new InMemoryVectorStoreOptions
            {
                EmbeddingGenerator = new DeterministicEmbeddingGenerator(),
            }));
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("MemoryAgent");
            var session = await proxy.CreateSessionAsync();

            var first = await proxy.RunAsync("I prefer strong coffee every morning.", session);
            var second = await proxy.RunAsync("What kind of coffee do I prefer?", session);

            Assert.Contains("remember", first.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("strong coffee", second.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, chatClient.CallCount);
            Assert.Contains(
                chatClient.Calls[2].Messages
                    .SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>(),
                result => result.Result?.ToString()?.Contains(
                    "strong coffee",
                    StringComparison.OrdinalIgnoreCase) == true);

            var sessionId = ((TemporalAgentSession)session).SessionId;
            var workflow = environment.Client.GetWorkflowHandle(sessionId.WorkflowId);
            var searchActivityCount = 0;
            await foreach (var historyEvent in workflow.FetchHistoryEventsAsync())
            {
                if (historyEvent.ActivityTaskScheduledEventAttributes is { } scheduled
                    && scheduled.ActivityType.Name == "TemporalCommunity.Extensions.Agents.InvokeAgentTool")
                {
                    searchActivityCount++;
                }
            }

            Assert.Equal(1, searchActivityCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task OnDemandSearch_ReconstructsFromRetainedScopeAfterWorkerRestart()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var initializerCalls = 0;
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "I will remember your coffee preference.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "memory-search-after-restart",
                    "Search",
                    new Dictionary<string, object?> { ["userQuestion"] = "coffee preference" }),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "You prefer strong coffee.")),
        ]);
        var retryPolicy = new RetryPolicy
        {
            MaximumAttempts = 2,
            InitialInterval = TimeSpan.FromMilliseconds(10),
        };
        Action<TemporalAgentOptions> options = temporal =>
        {
            temporal.RetryPolicy = retryPolicy;
            temporal.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(
                    services => services.GetRequiredService<VectorStore>(),
                    "memory-restart",
                    32);
                memory.WithScopeInitializer((_, context, _) =>
                {
                    Interlocked.Increment(ref initializerCalls);
                    var scope = new ChatHistoryMemoryProviderScope
                    {
                        ApplicationId = "restart-test-app",
                        AgentId = context.AgentName,
                        SessionId = context.SessionId,
                        UserId = "restart-test-user",
                    };
                    return Task.FromResult(new ChatHistoryMemoryProvider.State(scope, scope));
                });
                memory.WithRecall(recall =>
                {
                    recall.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling;
                    recall.MaxResults = 3;
                });
            });
        };

        var queue = $"memory-restart-{Guid.NewGuid():N}";
        var vectorStore = new InMemoryVectorStore(new InMemoryVectorStoreOptions
        {
            EmbeddingGenerator = new DeterministicEmbeddingGenerator(),
        });
        var blocker = new BlockFirstMemorySearchActivityInterceptor();
        using var firstHost = BuildHost(
            environment.Client,
            queue,
            chatClient,
            options,
            vectorStore: vectorStore,
            workerInterceptor: blocker);
        await firstHost.StartAsync();
        var proxy = firstHost.Services.GetTemporalAgentProxy("MemoryAgent");
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        try
        {
            var first = await proxy.RunAsync("I prefer strong coffee every morning.", session);
            Assert.Contains("remember", first.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, initializerCalls);
            var retainedStateBeforeSearch =
                System.Text.Json.JsonSerializer.Serialize(GetMemoryState(session));

            var pendingSearch = proxy.RunAsync("What kind of coffee do I prefer?", session);
            await blocker.SearchActivityStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // The interceptor holds the first search attempt before provider reconstruction.
            // Stopping this worker abandons that attempt; a replacement worker must replay the
            // workflow task and rebuild the stock search function from the retained StateBag.
            await firstHost.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            using var replacementHost = BuildHost(
                environment.Client,
                queue,
                chatClient,
                options,
                vectorStore: vectorStore);
            await replacementHost.StartAsync();

            var second = await pendingSearch.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains("strong coffee", second.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, initializerCalls);
            Assert.Equal(3, chatClient.CallCount);
            Assert.True(session.StateBag.TryGetValue<ChatHistoryMemoryProvider.State>(
                TemporalCommunity.Extensions.Agents.TemporalChatHistoryMemoryBuilder.StateKey,
                out var memoryState));
            Assert.Equal("restart-test-app", memoryState!.SearchScope.ApplicationId);
            Assert.Equal("restart-test-user", memoryState.SearchScope.UserId);
            Assert.Equal(session.SessionId.WorkflowId, memoryState.SearchScope.SessionId);
            var workflow = environment.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            Assert.Equal(
                retainedStateBeforeSearch,
                System.Text.Json.JsonSerializer.Serialize(
                    GetMemoryState(session)));
            Assert.Contains(
                2,
                await GetActivityAttemptsAsync(
                    workflow,
                    "TemporalCommunity.Extensions.Agents.InvokeAgentTool"));
            var completionAttempts = await GetActivityAttemptsAsync(
                workflow,
                "TemporalCommunity.Extensions.Agents.CompleteAgentTurn");
            Assert.Equal(2, completionAttempts.Count);
            Assert.All(completionAttempts, attempt => Assert.Equal(1, attempt));
        }
        finally
        {
            await firstHost.StopAsync();
        }
    }

    [Fact]
    public async Task OnDemandSearch_RetryAfterReadBeforeCompletion_RepeatsScopedRead()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var initializerCalls = 0;
        var completionLoss = new LoseFirstMemorySearchCompletionInterceptor();
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "I will remember your coffee preference.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "memory-search-retry",
                    "Search",
                    new Dictionary<string, object?> { ["userQuestion"] = "coffee preference" }),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "You prefer strong coffee.")),
        ]);
        Action<TemporalAgentOptions> options = temporal =>
        {
            temporal.RetryPolicy = new RetryPolicy
            {
                MaximumAttempts = 2,
                InitialInterval = TimeSpan.FromMilliseconds(10),
            };
            temporal.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(
                    services => services.GetRequiredService<VectorStore>(),
                    "memory-lost-completion",
                    32);
                memory.WithScopeInitializer((_, context, _) =>
                {
                    Interlocked.Increment(ref initializerCalls);
                    var scope = new ChatHistoryMemoryProviderScope
                    {
                        ApplicationId = "retry-test-app",
                        AgentId = context.AgentName,
                        SessionId = context.SessionId,
                        UserId = "retry-test-user",
                    };
                    return Task.FromResult(new ChatHistoryMemoryProvider.State(scope, scope));
                });
                memory.WithRecall(recall =>
                {
                    recall.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling;
                    recall.MaxResults = 3;
                });
            });
        };

        var queue = $"memory-search-retry-{Guid.NewGuid():N}";
        using var host = BuildHost(
            environment.Client,
            queue,
            chatClient,
            options,
            vectorStore: new InMemoryVectorStore(new InMemoryVectorStoreOptions
            {
                EmbeddingGenerator = new DeterministicEmbeddingGenerator(),
            }),
            workerInterceptor: completionLoss);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("MemoryAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            await proxy.RunAsync("I prefer strong coffee every morning.", session);
            var retainedStateBeforeSearch =
                System.Text.Json.JsonSerializer.Serialize(GetMemoryState(session));
            var recalled = await proxy.RunAsync("What kind of coffee do I prefer?", session);

            Assert.Contains("strong coffee", recalled.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, initializerCalls);
            Assert.Equal(3, chatClient.CallCount);
            Assert.Equal(2, completionLoss.SearchAttempts);
            Assert.Equal(1, completionLoss.InjectedFailures);
            Assert.Equal(2, completionLoss.SearchResults.Count);
            Assert.All(completionLoss.SearchResults, result =>
                Assert.Contains("strong coffee", result, StringComparison.OrdinalIgnoreCase));
            Assert.True(session.StateBag.TryGetValue<ChatHistoryMemoryProvider.State>(
                TemporalCommunity.Extensions.Agents.TemporalChatHistoryMemoryBuilder.StateKey,
                out var memoryState));
            Assert.Equal("retry-test-app", memoryState!.SearchScope.ApplicationId);
            Assert.Equal("retry-test-user", memoryState.SearchScope.UserId);
            Assert.Equal(session.SessionId.WorkflowId, memoryState.SearchScope.SessionId);
            Assert.Equal(
                retainedStateBeforeSearch,
                System.Text.Json.JsonSerializer.Serialize(GetMemoryState(session)));
            var workflow = environment.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            var completionAttempts = await GetActivityAttemptsAsync(
                workflow,
                "TemporalCommunity.Extensions.Agents.CompleteAgentTurn");
            Assert.Equal(2, completionAttempts.Count);
            Assert.All(completionAttempts, attempt => Assert.Equal(1, attempt));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BeforeAIInvoke_CompletionRetryRevalidatesRetainedScopeBeforeStorage()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var initializerCalls = 0;
        var completionRetry = new RetryFirstMemoryCompletionInterceptor();
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "I will remember your coffee preference.")),
        ]);
        Action<TemporalAgentOptions> options = temporal => temporal.UseChatHistoryMemory(memory =>
        {
            memory.UseVectorStore(
                services => services.GetRequiredService<VectorStore>(),
                "memory-completion-retry",
                32);
            memory.WithScopeInitializer((_, context, _) =>
            {
                Interlocked.Increment(ref initializerCalls);
                var scope = new ChatHistoryMemoryProviderScope
                {
                    ApplicationId = "completion-retry-app",
                    AgentId = context.AgentName,
                    SessionId = context.SessionId,
                    UserId = "completion-retry-user",
                };
                return Task.FromResult(new ChatHistoryMemoryProvider.State(scope, scope));
            });
        });

        var queue = $"memory-completion-retry-{Guid.NewGuid():N}";
        using var host = BuildHost(
            environment.Client,
            queue,
            chatClient,
            options,
            vectorStore: new InMemoryVectorStore(new InMemoryVectorStoreOptions
            {
                EmbeddingGenerator = new DeterministicEmbeddingGenerator(),
            }),
            workerInterceptor: completionRetry);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("MemoryAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            var response = await proxy.RunAsync("I prefer strong coffee every morning.", session);

            Assert.Contains("remember", response.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, initializerCalls);
            Assert.Equal(2, completionRetry.CompletionCalls);
            Assert.Equal(1, completionRetry.InjectedFailures);
            Assert.True(session.StateBag.TryGetValue<ChatHistoryMemoryProvider.State>(
                TemporalCommunity.Extensions.Agents.TemporalChatHistoryMemoryBuilder.StateKey,
                out var memoryState));
            Assert.Equal("completion-retry-app", memoryState!.StorageScope.ApplicationId);
            Assert.Equal("completion-retry-user", memoryState.StorageScope.UserId);
            Assert.Equal(session.SessionId.WorkflowId, memoryState.StorageScope.SessionId);
            var workflow = environment.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            Assert.Contains(
                2,
                await GetActivityAttemptsAsync(
                    workflow,
                    "TemporalCommunity.Extensions.Agents.CompleteAgentTurn"));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BeforeAIInvoke_LogsCaughtSearchAndStorageFailuresWhenLoggingIsConfigured()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var loggerProvider = new CapturingErrorLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Error);
            logging.AddProvider(loggerProvider);
        });
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Response despite memory errors.")),
        ]);
        Action<TemporalAgentOptions> options = temporal => temporal.UseChatHistoryMemory(memory =>
        {
            memory.UseVectorStore(
                services => services.GetRequiredService<VectorStore>(),
                "memory-failing-embeddings",
                32);
            memory.WithRecall(recall =>
                recall.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.BeforeAIInvoke);
        });
        var vectorStore = new InMemoryVectorStore(new InMemoryVectorStoreOptions
        {
            EmbeddingGenerator = new FailingEmbeddingGenerator(),
        });

        var queue = $"memory-provider-logging-{Guid.NewGuid():N}";
        using var host = BuildHost(
            environment.Client,
            queue,
            chatClient,
            options,
            vectorStore: vectorStore,
            loggerFactory: loggerFactory);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("MemoryAgent");
            var session = await proxy.CreateSessionAsync();

            var response = await proxy.RunAsync("Exercise search and storage failures.", session);

            Assert.Contains("Response despite memory errors.", response.Text, StringComparison.Ordinal);
            Assert.Contains(loggerProvider.Errors, message => message.Contains(
                "Failed to search for chat history",
                StringComparison.Ordinal));
            Assert.Contains(loggerProvider.Errors, message => message.Contains(
                "Failed to add messages to chat history vector store",
                StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Initializer_RetriesThenRunsOnceForTheSession()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var initializerAttempts = 0;
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "First response.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Second response.")),
        ]);
        Action<TemporalAgentOptions> options = temporal => temporal.UseChatHistoryMemory(memory =>
        {
            memory.UseVectorStore(_ => A.Fake<VectorStore>(), "support-memory", 1536);
            memory.WithRecall(recall =>
            {
                recall.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling;
                recall.StorageInputRequestMessageFilter = _ => [];
                recall.StorageInputResponseMessageFilter = _ => [];
            });
            memory.WithScopeInitializer((_, context, _) =>
            {
                if (Interlocked.Increment(ref initializerAttempts) == 1)
                {
                    throw new InvalidOperationException("Transient scope service failure.");
                }

                var storage = new ChatHistoryMemoryProviderScope
                {
                    AgentId = context.AgentName,
                    SessionId = context.SessionId,
                };
                return Task.FromResult(new ChatHistoryMemoryProvider.State(storage));
            });
        });

        var queue = $"memory-init-retry-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, queue, chatClient, options);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("MemoryAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            var first = await proxy.RunAsync("First turn.", session);
            var second = await proxy.RunAsync("Second turn.", session);

            Assert.Contains("First response.", first.Text);
            Assert.Contains("Second response.", second.Text);
            Assert.Equal(2, initializerAttempts);
            Assert.Equal(2, chatClient.CallCount);
            Assert.True(session.StateBag.TryGetValue<ChatHistoryMemoryProvider.State>(
                TemporalCommunity.Extensions.Agents.TemporalChatHistoryMemoryBuilder.StateKey,
                out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task InitializerExhaustion_FailsBeforeModelInvocation()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var initializerAttempts = 0;
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Must not run.")),
        ]);
        Action<TemporalAgentOptions> options = temporal => temporal.UseChatHistoryMemory(memory =>
        {
            memory.UseVectorStore(_ => A.Fake<VectorStore>(), "support-memory", 1536);
            memory.WithScopeInitializer((_, _, _) =>
            {
                Interlocked.Increment(ref initializerAttempts);
                throw new InvalidOperationException("Scope service unavailable.");
            });
        });

        var queue = $"memory-init-exhaustion-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, queue, chatClient, options);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("MemoryAgent");
            var session = await proxy.CreateSessionAsync();

            await Assert.ThrowsAsync<DurableTurnFailedException>(
                () => proxy.RunAsync("This must fail before inference.", session));

            Assert.Equal(2, initializerAttempts);
            Assert.Equal(0, chatClient.CallCount);
            Assert.False(session.StateBag.TryGetValue<ChatHistoryMemoryProvider.State>(
                TemporalCommunity.Extensions.Agents.TemporalChatHistoryMemoryBuilder.StateKey,
                out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task MemoryScopeSurvivesContinueAsNewAndReplay()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var initializerAttempts = 0;
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "First response.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Second response.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Third response.")),
        ]);
        Action<TemporalAgentOptions> options = temporal =>
        {
            temporal.MaxEntryCount = 4;
            temporal.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(_ => A.Fake<VectorStore>(), "support-memory", 1536);
                memory.WithRecall(recall =>
                {
                    recall.SearchTime = ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling;
                    recall.StorageInputRequestMessageFilter = _ => [];
                    recall.StorageInputResponseMessageFilter = _ => [];
                });
                memory.WithScopeInitializer((_, context, _) =>
                {
                    Interlocked.Increment(ref initializerAttempts);
                    var storage = new ChatHistoryMemoryProviderScope
                    {
                        AgentId = context.AgentName,
                        SessionId = context.SessionId,
                    };
                    return Task.FromResult(new ChatHistoryMemoryProvider.State(storage));
                });
            });
        };

        var queue = $"memory-init-can-{Guid.NewGuid():N}";
        const string historyReducerKey = "memory-can-keep-last-turn";
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> keepLastTurn =
            entries => entries.TakeLast(2).ToList();
        using var host = BuildHost(
            environment.Client,
            queue,
            chatClient,
            options,
            historyReducerKey,
            keepLastTurn);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("MemoryAgent");
            var sessionId = TemporalAgentSessionId.WithRandomKey("MemoryAgent");
            var workflow = await environment.Client.StartWorkflowAsync(
                (AgentWorkflow agentWorkflow) => agentWorkflow.RunAsync(new AgentWorkflowInput
                {
                    AgentName = "MemoryAgent",
                    TaskQueue = queue,
                    EnableSearchAttributes = false,
                    MaxEntryCount = 4,
                    HistoryReducerKey = historyReducerKey,
                    ActivityTimeout = TimeSpan.FromSeconds(15),
                    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                }),
                new WorkflowOptions(sessionId.WorkflowId, queue));
            var session = new TemporalAgentSession(sessionId);

            await proxy.RunAsync("First turn.", session);
            var originalRunId = (await workflow.DescribeAsync()).RunId;
            await proxy.RunAsync("Second turn.", session);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((await workflow.DescribeAsync()).RunId == originalRunId && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.NotEqual(originalRunId, (await workflow.DescribeAsync()).RunId);
            await proxy.RunAsync("Third turn.", session);
            Assert.Equal(1, initializerAttempts);
            Assert.Equal(3, chatClient.CallCount);

            await host.Services.GetRequiredService<ITemporalAgentClient>().ShutdownAsync(sessionId);
            await workflow.GetResultAsync();

            var replayOptions = new WorkflowReplayerOptions
            {
                DataConverter = TemporalAgentDataConverter.Instance,
            };
            replayOptions.AddWorkflow<AgentWorkflow>();
            var replayer = new WorkflowReplayer(replayOptions);
            var originalRun = environment.Client.GetWorkflowHandle<AgentWorkflow>(
                sessionId.WorkflowId,
                originalRunId);
            foreach (var run in new[] { originalRun, workflow })
            {
                var replay = await replayer.ReplayWorkflowAsync(
                    await run.FetchHistoryAsync(),
                    throwOnReplayFailure: false);
                Assert.Null(replay.ReplayFailure);
            }
            Assert.Equal(1, initializerAttempts);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        Action<TemporalAgentOptions> configureTemporalOptions,
        string? historyReducerKey = null,
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? historyReducer = null,
        VectorStore? vectorStore = null,
        ILoggerFactory? loggerFactory = null,
        IWorkerInterceptor? workerInterceptor = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        if (loggerFactory is not null)
        {
            builder.Services.AddSingleton(loggerFactory);
        }
        if (vectorStore is not null)
        {
            builder.Services.AddSingleton<VectorStore>(vectorStore);
        }
        if (historyReducerKey is not null && historyReducer is not null)
        {
            builder.Services.AddKeyedSingleton<
                Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
                    historyReducerKey,
                    (_, _) => historyReducer);
        }
        var workerBuilder = builder.Services.AddHostedTemporalWorker(taskQueue);
        if (workerInterceptor is not null)
        {
            workerBuilder.ConfigureOptions(workerOptions =>
                workerOptions.Interceptors = [workerInterceptor]);
        }
        workerBuilder
            .AddTemporalAgents(options =>
            {
                options.DefaultHistoryReducerKey = historyReducerKey;
                options.Agent(
                    "MemoryAgent",
                    _ => chatClient,
                    new ChatClientAgentOptions(),
                    configureTemporalOptions);
            });
        return builder.Build();
    }

    private static async Task<List<int>> GetActivityAttemptsAsync(
        WorkflowHandle workflow,
        string activityTypeName)
    {
        var scheduledEventIds = new HashSet<long>();
        var attempts = new List<int>();
        await foreach (var historyEvent in workflow.FetchHistoryEventsAsync())
        {
            if (historyEvent.ActivityTaskScheduledEventAttributes is { } scheduled
                && scheduled.ActivityType.Name == activityTypeName)
            {
                scheduledEventIds.Add(historyEvent.EventId);
            }
            else if (historyEvent.ActivityTaskStartedEventAttributes is { } started
                     && scheduledEventIds.Contains(started.ScheduledEventId))
            {
                attempts.Add(started.Attempt);
            }
        }

        return attempts;
    }

    private static ChatHistoryMemoryProvider.State GetMemoryState(TemporalAgentSession session)
    {
        Assert.True(session.StateBag.TryGetValue<ChatHistoryMemoryProvider.State>(
            TemporalCommunity.Extensions.Agents.TemporalChatHistoryMemoryBuilder.StateKey,
            out var state));
        return state!;
    }

    private sealed class DeterministicEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = values.Select(value =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vector = new float[32];
                foreach (var character in value.ToLowerInvariant())
                {
                    vector[character % vector.Length] += 1;
                }

                var magnitude = MathF.Sqrt(vector.Sum(component => component * component));
                if (magnitude > 0)
                {
                    for (var index = 0; index < vector.Length; index++)
                    {
                        vector[index] /= magnitude;
                    }
                }

                return new Embedding<float>(vector);
            });

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class BlockFirstMemorySearchActivityInterceptor : IWorkerInterceptor
    {
        public TaskCompletionSource SearchActivityStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
            new BlockFirstMemorySearchActivityInbound(nextInterceptor, SearchActivityStarted);

        private sealed class BlockFirstMemorySearchActivityInbound(
            ActivityInboundInterceptor next,
            TaskCompletionSource searchActivityStarted) : ActivityInboundInterceptor(next)
        {
            public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
            {
                if (ActivityExecutionContext.Current.Info.ActivityType
                    == "TemporalCommunity.Extensions.Agents.InvokeAgentTool"
                    && ActivityExecutionContext.Current.Info.Attempt == 1)
                {
                    searchActivityStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ActivityExecutionContext.Current.CancellationToken);
                }

                return await base.ExecuteActivityAsync(input);
            }
        }
    }

    private sealed class LoseFirstMemorySearchCompletionInterceptor : IWorkerInterceptor
    {
        private int _searchAttempts;
        private int _injectedFailures;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _searchResults = new();

        public int SearchAttempts => Volatile.Read(ref _searchAttempts);
        public int InjectedFailures => Volatile.Read(ref _injectedFailures);
        public IReadOnlyCollection<string> SearchResults => _searchResults.ToArray();

        public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
            new LoseFirstMemorySearchCompletionInbound(nextInterceptor, this);

        private sealed class LoseFirstMemorySearchCompletionInbound(
            ActivityInboundInterceptor next,
            LoseFirstMemorySearchCompletionInterceptor owner) : ActivityInboundInterceptor(next)
        {
            public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
            {
                var result = await base.ExecuteActivityAsync(input);
                var activity = ActivityExecutionContext.Current.Info;
                if (activity.ActivityType == "TemporalCommunity.Extensions.Agents.InvokeAgentTool")
                {
                    Interlocked.Increment(ref owner._searchAttempts);
                    var resultText = System.Text.Json.JsonSerializer.Serialize(result);
                    owner._searchResults.Enqueue(resultText);
                    if (activity.Attempt == 1)
                    {
                        Interlocked.Increment(ref owner._injectedFailures);
                        throw new InvalidOperationException(
                            "Injected failure after on-demand memory search completed but before Temporal recorded activity completion.");
                    }
                }

                return result;
            }
        }
    }

    private sealed class RetryFirstMemoryCompletionInterceptor : IWorkerInterceptor
    {
        private int _completionCalls;
        private int _injectedFailures;

        public int CompletionCalls => Volatile.Read(ref _completionCalls);
        public int InjectedFailures => Volatile.Read(ref _injectedFailures);

        public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
            new RetryFirstMemoryCompletionInbound(nextInterceptor, this);

        private sealed class RetryFirstMemoryCompletionInbound(
            ActivityInboundInterceptor next,
            RetryFirstMemoryCompletionInterceptor owner) : ActivityInboundInterceptor(next)
        {
            public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
            {
                var isMemoryCompletion = ActivityExecutionContext.Current.Info.ActivityType
                    == "TemporalCommunity.Extensions.Agents.CompleteAgentTurn";
                var execution = isMemoryCompletion
                    ? Interlocked.Increment(ref owner._completionCalls)
                    : 0;
                var result = await base.ExecuteActivityAsync(input);
                if (execution == 1)
                {
                    Interlocked.Increment(ref owner._injectedFailures);
                    throw new InvalidOperationException(
                        "Injected failure after memory completion writes, before Temporal recorded activity completion.");
                }

                return result;
            }
        }
    }

    private sealed class FailingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                new InvalidOperationException("Intentional embedding failure for logging coverage."));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CapturingErrorLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _errors = new();

        public IReadOnlyCollection<string> Errors => _errors.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingErrorLogger(_errors);

        public void Dispose() { }

        private sealed class CapturingErrorLogger(
            System.Collections.Concurrent.ConcurrentQueue<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    errors.Enqueue(formatter(state, exception));
                }
            }
        }
    }

}
