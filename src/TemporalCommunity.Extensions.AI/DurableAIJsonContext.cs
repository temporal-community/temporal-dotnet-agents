using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Source-generated JSON serialization context for durable AI types.
/// Uses <see cref="AIJsonUtilities.DefaultOptions"/> as the base to correctly
/// handle <see cref="AIContent"/> polymorphism (TextContent, FunctionCallContent, etc.).
/// </summary>
[JsonSerializable(typeof(DurableChatInput))]
[JsonSerializable(typeof(DurableHistorySummaryTurn))]
[JsonSerializable(typeof(DurableHistorySummarizationInput))]
[JsonSerializable(typeof(DurableHistorySummarizationResult))]
[JsonSerializable(typeof(IReadOnlyList<DurableHistorySummaryTurn>))]
[JsonSerializable(typeof(DurableChatTurnRange))]
[JsonSerializable(typeof(DurableFunctionInput))]
[JsonSerializable(typeof(DurableFunctionOutput))]
[JsonSerializable(typeof(DurableChatWorkflowInput))]
[JsonSerializable(typeof(DurableHistoryRecoveryState))]
[JsonSerializable(typeof(DurableHistoryRecoveryResult))]
[JsonSerializable(typeof(DurableHistoryCompactionStrategy))]
[JsonSerializable(typeof(DurableApprovalRequest))]
[JsonSerializable(typeof(DurableApprovalDecision))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(ChatOptions))]
[JsonSerializable(typeof(IList<ChatMessage>))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(DurableSessionEntry))]
[JsonSerializable(typeof(DurableSessionRequest))]
[JsonSerializable(typeof(DurableSessionResponse))]
[JsonSerializable(typeof(DurableChatTerminalOutcome))]
[JsonSerializable(typeof(DurableSessionSummary))]
[JsonSerializable(typeof(CompactionMarkerEntry))]
[JsonSerializable(typeof(TruncationMarkerEntry))]
[JsonSerializable(typeof(IReadOnlyList<DurableSessionEntry>))]
[JsonSerializable(typeof(List<DurableSessionEntry>))]
[JsonSerializable(typeof(DurableChatStepResult))]
[JsonSerializable(typeof(ActivityOptions))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, ActivityOptions>))]
[JsonSerializable(typeof(Dictionary<string, ActivityOptions>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, TimeSpan>))]
[JsonSerializable(typeof(DurableToolOutcome))]
[JsonSerializable(typeof(DurableToolInterceptorInput))]
[JsonSerializable(typeof(DurableToolInterceptorResult))]
[JsonSerializable(typeof(DurableToolDispatchMode))]
[JsonSerializable(typeof(DurableTurnCompletionReason))]
[JsonSerializable(typeof(DurableTurnCancellationStatus))]
[JsonSerializable(typeof(DurableTurnCancellationResult))]
[JsonSerializable(typeof(DurableTurnOptions))]
[JsonSerializable(typeof(Internal.DurableFunctionDeclarationSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<Internal.DurableFunctionDeclarationSnapshot>))]
[JsonSerializable(typeof(Internal.DurableToolsetResolutionRequest))]
[JsonSerializable(typeof(Internal.DurableToolsetManifest))]
[JsonSerializable(typeof(Internal.DurableToolsetManifestMember))]
[JsonSerializable(typeof(IReadOnlyList<Internal.DurableToolsetManifestMember>))]
[JsonSerializable(typeof(Internal.DurableTerminalTurnOutcome))]
[JsonSerializable(typeof(IReadOnlyList<Internal.DurableTerminalTurnOutcome>))]
[JsonSerializable(typeof(Internal.DurableTerminalTurnFailure))]
[JsonSerializable(typeof(Internal.DurableTurnOutcomeTooLargeDetails))]
[JsonSerializable(typeof(Exceptions.DurableToolCallOutcomeSummary))]
[JsonSerializable(typeof(IReadOnlyList<Exceptions.DurableToolCallOutcomeSummary>))]
[JsonSerializable(typeof(Exceptions.DurableToolCallOutcomeCounts))]
[JsonSerializable(typeof(Exceptions.DurableToolCallOutcomeStatus))]
[JsonSerializable(typeof(DurableManagedLoopResult))]
internal partial class DurableAIJsonContext : JsonSerializerContext;

/// <summary>
/// JSON serialization utilities for the durable AI library.
/// </summary>
public static class DurableAIJsonUtilities
{
    /// <summary>
    /// Gets the default <see cref="JsonSerializerOptions"/> configured with MEAI type support.
    /// This leverages <see cref="AIJsonUtilities.DefaultOptions"/> which handles
    /// <see cref="AIContent"/> polymorphism correctly.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        // Start from AIJsonUtilities.DefaultOptions which already handles
        // AIContent polymorphism (TextContent, FunctionCallContent, etc.)
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        // AIJsonUtilities includes a reflection resolver. Resolver selection is first-match-wins,
        // so durable DTO metadata must precede that fallback to preserve the source-generated
        // serialization and trimming contract. DurableToolsetResolutionRequest carries its
        // absent-version compatibility behavior in an explicit converter, so it remains on the
        // source-generated path too.
        // Preserve MEAI's source-generated metadata for its own contracts (notably
        // UsageDetails), then place durable metadata ahead of the reflection fallback.
        options.TypeInfoResolverChain.Insert(1, DurableAIJsonContext.Default);

        // MEAI registers a string-enum converter in its shared options. These two values are
        // Temporal wire contracts, so their numeric representation must win in both reflection
        // and source-generated paths. Converter-list entries take precedence over attributes.
        options.Converters.Insert(0, new JsonNumberEnumConverter<DurableTurnCompletionReason>());
        options.Converters.Insert(0, new JsonNumberEnumConverter<DurableToolDispatchMode>());

        options.MakeReadOnly();
        return options;
    }
}
