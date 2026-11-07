using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Trusted metadata supplied to a chat-history memory scope initializer.
/// </summary>
public sealed class TemporalMemoryScopeContext
{
    internal TemporalMemoryScopeContext(string sessionId, string agentName, string collectionName)
    {
        SessionId = sessionId;
        AgentName = agentName;
        CollectionName = collectionName;
    }

    /// <summary>Gets the durable Temporal session workflow ID.</summary>
    public string SessionId { get; }

    /// <summary>Gets the registered Temporal agent name.</summary>
    public string AgentName { get; }

    /// <summary>Gets the configured vector-store collection name.</summary>
    public string CollectionName { get; }
}

/// <summary>Configures the vector store, trusted scope, and recall behavior for MAF chat memory.</summary>
public sealed class TemporalChatHistoryMemoryBuilder
{
    internal const string StateKey = "TemporalCommunity.Extensions.Agents.ChatHistoryMemory";
    internal const string ScopeKey = "TemporalCommunity.Extensions.Agents.ChatHistoryMemory.Scope";

    private Func<IServiceProvider, VectorStore>? _vectorStoreFactory;
    private string? _collectionName;
    private int _vectorDimensions;
    private Func<IServiceProvider, TemporalMemoryScopeContext, CancellationToken,
        Task<ChatHistoryMemoryProvider.State>>? _scopeInitializer;
    private bool _allowCrossSessionRecall;
    private readonly ChatHistoryMemoryProviderOptions _recallOptions = new()
    {
        FunctionToolName = "Search",
        FunctionToolDescription =
            "Allows searching for related previous chat history to help answer the user question.",
    };

    /// <summary>Configures the vector store used by MAF's chat-history memory provider.</summary>
    /// <param name="factory">Resolves the vector store from the current worker activity scope.</param>
    /// <param name="collectionName">The vector-store collection to use.</param>
    /// <param name="vectorDimensions">The embedding-vector dimension count.</param>
    /// <returns>This builder.</returns>
    public TemporalChatHistoryMemoryBuilder UseVectorStore(
        Func<IServiceProvider, VectorStore> factory,
        string collectionName,
        int vectorDimensions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentOutOfRangeException.ThrowIfLessThan(vectorDimensions, 1);
        if (_vectorStoreFactory is not null)
        {
            throw new InvalidOperationException("The vector store is already configured.");
        }

        _vectorStoreFactory = factory;
        _collectionName = collectionName;
        _vectorDimensions = vectorDimensions;
        return this;
    }

    /// <summary>
    /// Configures the trusted scope initializer. It runs in a dedicated activity only when a
    /// workflow session has no previously recorded memory state.
    /// </summary>
    /// <param name="initializer">
    /// Resolves trusted storage and search scopes. The callback may run again if its activity
    /// attempt fails before the workflow records the result, so it must be safe to repeat. It runs
    /// in a dedicated activity with a 30-second start-to-close timeout, at most two attempts, a
    /// one-second initial retry interval, and no heartbeat timeout.
    /// </param>
    /// <param name="allowCrossSessionRecall">
    /// Explicitly permits a search scope with a null or different session ID when it is bounded
    /// by a trusted application or user dimension. Storage remains bound to the current session.
    /// </param>
    /// <returns>This builder.</returns>
    public TemporalChatHistoryMemoryBuilder WithScopeInitializer(
        Func<IServiceProvider, TemporalMemoryScopeContext, CancellationToken,
            Task<ChatHistoryMemoryProvider.State>> initializer,
        bool allowCrossSessionRecall = false)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        _scopeInitializer = initializer;
        _allowCrossSessionRecall = allowCrossSessionRecall;
        return this;
    }

    /// <summary>Configures MAF's recall behavior without changing its workflow-owned state key.</summary>
    /// <param name="configure">
    /// Configures MAF recall options such as search timing and result count. A non-default
    /// <see cref="ChatHistoryMemoryProviderOptions.StateKey"/> is not supported.
    /// </param>
    /// <returns>This builder.</returns>
    public TemporalChatHistoryMemoryBuilder WithRecall(
        Action<ChatHistoryMemoryProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_recallOptions);
        return this;
    }

    internal TemporalMemorySearchRegistration CreateRegistration(string agentName)
    {
        if (_vectorStoreFactory is null || _collectionName is null)
        {
            throw new InvalidOperationException(
                $"Agent '{agentName}' must configure UseVectorStore inside UseChatHistoryMemory.");
        }

        if (!Enum.IsDefined(typeof(ChatHistoryMemoryProviderOptions.SearchBehavior), _recallOptions.SearchTime))
        {
            throw new InvalidOperationException("The configured chat-history memory search mode is invalid.");
        }
        if (_recallOptions.MaxResults is <= 0)
        {
            throw new InvalidOperationException("Chat-history memory MaxResults must be greater than zero.");
        }
        if (_recallOptions.StateKey is not null
            && !string.Equals(_recallOptions.StateKey, StateKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Chat-history memory StateKey is owned by Temporal and must not be changed from '{StateKey}'.");
        }

        var toolName = _recallOptions.FunctionToolName;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new InvalidOperationException("The chat-history memory search tool name cannot be empty.");
        }

        var providerOptions = new ChatHistoryMemoryProviderOptions
        {
            SearchTime = _recallOptions.SearchTime,
            FunctionToolName = toolName,
            FunctionToolDescription = _recallOptions.FunctionToolDescription,
            ContextPrompt = _recallOptions.ContextPrompt,
            MaxResults = _recallOptions.MaxResults,
            EnableSensitiveTelemetryData = _recallOptions.EnableSensitiveTelemetryData,
            Redactor = _recallOptions.Redactor,
            SearchInputMessageFilter = _recallOptions.SearchInputMessageFilter,
            StorageInputRequestMessageFilter = _recallOptions.StorageInputRequestMessageFilter,
            StorageInputResponseMessageFilter = _recallOptions.StorageInputResponseMessageFilter,
            StateKey = StateKey,
        };
        var vectorStoreFactory = _vectorStoreFactory;
        var collectionName = _collectionName;
        var vectorDimensions = _vectorDimensions;
        var scopeInitializer = _scopeInitializer;

        return new TemporalMemorySearchRegistration(
            ProviderFactory: services => new ChatHistoryMemoryProvider(
                vectorStoreFactory(services),
                collectionName,
                vectorDimensions,
                _ => throw new InvalidOperationException(
                    "Temporal chat-history memory state must be initialized by its dedicated activity."),
                providerOptions,
                services.GetService<ILoggerFactory>()),
            ScopeInitializer: scopeInitializer,
            CollectionName: collectionName,
            AllowCrossSessionRecall: _allowCrossSessionRecall,
            SearchTime: _recallOptions.SearchTime,
            ToolName: toolName);
    }
}

internal sealed record TemporalMemoryScopeSnapshot(
    string? StorageApplicationId,
    string? StorageAgentId,
    string? StorageSessionId,
    string? StorageUserId,
    string? SearchApplicationId,
    string? SearchAgentId,
    string? SearchSessionId,
    string? SearchUserId)
{
    internal static TemporalMemoryScopeSnapshot Create(
        ChatHistoryMemoryProvider.State? state,
        string agentName,
        string sessionId,
        bool allowCrossSessionRecall)
    {
        if (state is null)
        {
            throw InvalidState("The chat-history memory scope initializer returned null state.");
        }

        var storage = state.StorageScope;
        var search = state.SearchScope;
        if (!IsValid(storage.ApplicationId)
            || !IsValid(storage.AgentId)
            || !IsValid(storage.SessionId)
            || !IsValid(storage.UserId)
            || !IsValid(search.ApplicationId)
            || !IsValid(search.AgentId)
            || !IsValid(search.SessionId)
            || !IsValid(search.UserId))
        {
            throw InvalidState("Memory scope values must be nonblank when supplied and no longer than 256 characters.");
        }

        if (!string.Equals(storage.AgentId, agentName, StringComparison.Ordinal)
            || !string.Equals(storage.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw InvalidState("Memory storage scope must match the current Temporal agent and session.");
        }

        if (!string.Equals(search.AgentId, storage.AgentId, StringComparison.Ordinal)
            || !string.Equals(search.ApplicationId, storage.ApplicationId, StringComparison.Ordinal)
            || !string.Equals(search.UserId, storage.UserId, StringComparison.Ordinal))
        {
            throw InvalidState("Memory search scope cannot be broader than its trusted storage scope.");
        }

        if (search.SessionId is null)
        {
            if (!allowCrossSessionRecall)
            {
                throw InvalidState(
                    "A null memory search SessionId requires allowCrossSessionRecall: true.");
            }
            if (search.ApplicationId is null && search.UserId is null)
            {
                throw InvalidState(
                    "Cross-session memory recall must be bounded by ApplicationId or UserId.");
            }
        }
        else if (!allowCrossSessionRecall
                 && !string.Equals(search.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw InvalidState("Memory search scope must remain bound to the current session.");
        }
        else if (allowCrossSessionRecall
                 && !string.Equals(search.SessionId, sessionId, StringComparison.Ordinal)
                 && search.ApplicationId is null
                 && search.UserId is null)
        {
            throw InvalidState(
                "Cross-session memory recall must be bounded by ApplicationId or UserId.");
        }

        return new TemporalMemoryScopeSnapshot(
            storage.ApplicationId,
            storage.AgentId,
            storage.SessionId,
            storage.UserId,
            search.ApplicationId,
            search.AgentId,
            search.SessionId,
            search.UserId);
    }

    internal ChatHistoryMemoryProvider.State ToProviderState() =>
        new(
            new ChatHistoryMemoryProviderScope
            {
                ApplicationId = StorageApplicationId,
                AgentId = StorageAgentId,
                SessionId = StorageSessionId,
                UserId = StorageUserId,
            },
            new ChatHistoryMemoryProviderScope
            {
                ApplicationId = SearchApplicationId,
                AgentId = SearchAgentId,
                SessionId = SearchSessionId,
                UserId = SearchUserId,
            });

    internal bool Matches(ChatHistoryMemoryProvider.State? state) =>
        state is not null
        && ScopeMatches(state.StorageScope, StorageApplicationId, StorageAgentId, StorageSessionId, StorageUserId)
        && ScopeMatches(state.SearchScope, SearchApplicationId, SearchAgentId, SearchSessionId, SearchUserId);

    private static bool ScopeMatches(
        ChatHistoryMemoryProviderScope scope,
        string? applicationId,
        string? agentId,
        string? sessionId,
        string? userId) =>
        string.Equals(scope.ApplicationId, applicationId, StringComparison.Ordinal)
        && string.Equals(scope.AgentId, agentId, StringComparison.Ordinal)
        && string.Equals(scope.SessionId, sessionId, StringComparison.Ordinal)
        && string.Equals(scope.UserId, userId, StringComparison.Ordinal);

    private static bool IsValid(string? value) => value is null || (!string.IsNullOrWhiteSpace(value) && value.Length <= 256);

    private static Temporalio.Exceptions.ApplicationFailureException InvalidState(string message) =>
        new(message, errorType: nameof(TemporalCommunity.Extensions.AI.Exceptions.DurableConfigurationException), nonRetryable: true);
}
