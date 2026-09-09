using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Tools;
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
    IReadOnlyList<ContextProviderRegistration> ContextProviderFactories,
    TimeSpan? TimeToLive,
    TimeSpan? ApprovalTimeout,
    TimeSpan? ActivityTimeout,
    TimeSpan? HeartbeatTimeout,
    RetryPolicy? RetryPolicy,
    int? MaxEntryCount,
    int MaxToolCallsPerTurn,
    string? HistoryReducerKey,
    Action<AIAgentBuilder>? ConfigureAgentPipeline,
    Func<IServiceProvider, IDurableToolInterceptor<AgentToolContext>>? ToolInterceptorFactory = null,
    bool UseApprovalScopes = false,
    ApprovalScopesOptions? ApprovalScopesOptions = null,
    IReadOnlyList<(string ToolName, string SourceProviderType)>? ProviderContributedTools = null);

/// <summary>
/// One registered context provider, plus the registration-time fact the activity cannot recover
/// from the delegate alone: whether this provider's durable tool declarations were registered.
/// </summary>
/// <remarks>
/// Both <c>AddContextProvider</c> overloads collapse to a
/// <see cref="Func{IServiceProvider, AIContextProvider}"/>, so at activity time an instance
/// registration and a factory registration are indistinguishable. That matters because an
/// <c>IDurableToolSource</c> can only have its declarations collected on the instance path — a
/// factory-resolved one would otherwise have its tools stripped silently, and be excluded from the
/// provider-tool warning precisely because it implements the interface. Recording the fact here is
/// what lets the activity tell "declared and registered" from "declared and lost".
/// </remarks>
/// <param name="Factory">Resolves the provider from the activity's scoped service provider.</param>
/// <param name="DeclarationsRegistered">
/// <see langword="true"/> when durable tool declarations for this provider were registered at
/// build time, either from explicit specs or from <c>IDurableToolSource.GetDurableTools()</c>.
/// </param>
internal sealed record ContextProviderRegistration(
    Func<IServiceProvider, AIContextProvider> Factory,
    bool DeclarationsRegistered);
