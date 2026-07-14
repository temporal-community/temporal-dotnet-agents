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
[JsonSerializable(typeof(DurableFunctionInput))]
[JsonSerializable(typeof(DurableFunctionOutput))]
[JsonSerializable(typeof(DurableChatWorkflowInput))]
[JsonSerializable(typeof(DurableApprovalRequest))]
[JsonSerializable(typeof(DurableApprovalDecision))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
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
[JsonSerializable(typeof(IReadOnlyDictionary<string, TimeSpan>))]
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
    /// On the <c>netstandard2.1</c> asset, <see cref="System.Half"/> is unavailable, so the
    /// <c>GeneratedEmbeddings&lt;Embedding&lt;Half&gt;&gt;</c> converter is not registered.
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
        options.Converters.Add(new GeneratedEmbeddingsJsonConverter<Embedding<float>>());
        options.Converters.Add(new GeneratedEmbeddingsJsonConverter<Embedding<double>>());
#if NET5_0_OR_GREATER
        // System.Half is a net5.0+ type — absent on netstandard2.1. Half-precision embeddings
        // are not registered on the down-level leg (float/double/byte/int still covered). This
        // is a narrow, documented down-level limitation.
        options.Converters.Add(new GeneratedEmbeddingsJsonConverter<Embedding<Half>>());
#endif
        options.Converters.Add(new GeneratedEmbeddingsJsonConverter<Embedding<byte>>());
        options.Converters.Add(new GeneratedEmbeddingsJsonConverter<Embedding<int>>());

        options.MakeReadOnly();
        return options;
    }
}
