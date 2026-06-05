using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Source-generated JSON serialization context for durable AI types.
/// Uses <see cref="AIJsonUtilities.DefaultOptions"/> as the base to correctly
/// handle <see cref="AIContent"/> polymorphism (TextContent, FunctionCallContent, etc.).
/// </summary>
[JsonSerializable(typeof(ApprovalScope))]
// PatternMatchType is NOT registered standalone here — registering it standalone in source-gen
// would generate a plain integer-based enum info that ignores the [JsonConverter] attribute,
// bypassing PatternMatchTypeJsonConverter. It is inferred from ApprovalScopePattern's property.
[JsonSerializable(typeof(ApprovalScopePattern))]
[JsonSerializable(typeof(DurableChatInput))]
[JsonSerializable(typeof(DurableFunctionInput))]
[JsonSerializable(typeof(DurableFunctionOutput))]
[JsonSerializable(typeof(DurableChatWorkflowInput))]
[JsonSerializable(typeof(DurableApprovalRequest))]
[JsonSerializable(typeof(DurableApprovalDecision))]
[JsonSerializable(typeof(DurableEmbeddingInput))]
[JsonSerializable(typeof(DurableEmbeddingOutput))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(ChatOptions))]
[JsonSerializable(typeof(IList<ChatMessage>))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(DurableSessionEntry))]
[JsonSerializable(typeof(DurableSessionRequest))]
[JsonSerializable(typeof(DurableSessionResponse))]
[JsonSerializable(typeof(CompactionMarkerEntry))]
[JsonSerializable(typeof(IReadOnlyList<DurableSessionEntry>))]
[JsonSerializable(typeof(List<DurableSessionEntry>))]
[JsonSerializable(typeof(DurableChatStepResult))]
[JsonSerializable(typeof(ActivityOptions))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, ActivityOptions>))]
[JsonSerializable(typeof(Dictionary<string, ActivityOptions>))]
[JsonSerializable(typeof(DurableToolOutcome))]
[JsonSerializable(typeof(DurableToolInterceptorInput))]
[JsonSerializable(typeof(DurableToolInterceptorResult))]
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
        options.TypeInfoResolverChain.Add(DurableAIJsonContext.Default);

        // GeneratedEmbeddings<T> implements IList<T>, so both reflection-based and source-gen
        // resolvers treat it as a bare collection and silently drop Usage / AdditionalProperties.
        // Register an explicit converter so the wrapper's own properties round-trip.
        //
        // Rationale: DurableEmbeddingOutput is currently hard-coded to Embedding<float>, so only
        // the <float> closure is active code today. The remaining registrations are defensive:
        // Embedding<T> is generic with no constraint, and MEAI documents <float>, <double>, and
        // <Half> as typical element types. Registering all numerically-plausible closures keeps
        // the converter coverage in lockstep with the type's surface area, so a future broadening
        // of the activity contract (or a downstream consumer that reuses DurableAIDataConverter
        // for its own embedding closures) gets correct wrapper-property handling for free. Cost
        // is one line + tiny metadata footprint per closure.
        // PatternMatchType uses a custom JsonStringEnumConverter with allowIntegerValues: false.
        // Register in Converters in addition to the [JsonConverter] attribute on the enum to
        // guarantee string serialization with integer values rejected in source-gen chain contexts
        // where the [JsonConverter] attribute may not be honoured for all code paths.
        options.Converters.Add(new PatternMatchTypeJsonConverter());

        options.Converters.Add(new GeneratedEmbeddingsJsonConverter<Embedding<float>>());
        options.Converters.Add(new GeneratedEmbeddingsJsonConverter<Embedding<double>>());
        options.Converters.Add(new GeneratedEmbeddingsJsonConverter<Embedding<Half>>());
        options.Converters.Add(new GeneratedEmbeddingsJsonConverter<Embedding<byte>>());
        options.Converters.Add(new GeneratedEmbeddingsJsonConverter<Embedding<int>>());

        // ChatOptions.Tools is IList<AITool>; AITool is polymorphic with no discriminator
        // mapping in AIJsonUtilities, so the Tools list silently collapses to null without
        // this converter. Mirrors the GeneratedEmbeddingsJsonConverter approach — patch
        // the slot in DurableAIJsonUtilities so the converter only applies inside the
        // DurableAIDataConverter wire format, not MEAI's defaults.
        options.Converters.Add(new ChatOptionsToolsJsonConverter());

        options.MakeReadOnly();
        return options;
    }
}
