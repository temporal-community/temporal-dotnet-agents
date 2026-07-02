using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.HistoryStore;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Immutable snapshot of a <see cref="DurableAgentBuilder"/> taken at the end of the
/// <c>TemporalAgentsOptions.AddDurableAgent</c> configure delegate. Phase 2 stores this on
/// <see cref="TemporalAgentsOptions"/>; Phase 3 reads it from the workflow loop.
/// </summary>
internal sealed record DurableAgentRegistration(
    string Name,
    string? Description,
    string? Instructions,
    Func<IServiceProvider, IChatClient> ChatClient,
    ChatOptions? ChatOptions,
    IReadOnlyList<DurableToolRegistration> Tools,
    IReadOnlyList<Func<IServiceProvider, AIContextProvider>> ContextProviderFactories,
    Func<IServiceProvider, IAgentHistoryStore>? HistoryStore,
    TimeSpan? TimeToLive,
    TimeSpan? ApprovalTimeout,
    TimeSpan? ActivityTimeout,
    TimeSpan? HeartbeatTimeout,
    RetryPolicy? RetryPolicy,
    int? MaxEntryCount,
    int MaxToolCallsPerTurn,
    Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer,
    string? HistoryReducerKey,
    Action<AIAgentBuilder>? ConfigureAgentPipeline,
    string? CompactionStrategyKey,
    Func<IServiceProvider, IDurableToolInterceptor<AgentToolContext>>? ToolInterceptorFactory = null,
    bool UseApprovalScopes = false,
    ApprovalScopesOptions? ApprovalScopesOptions = null);
