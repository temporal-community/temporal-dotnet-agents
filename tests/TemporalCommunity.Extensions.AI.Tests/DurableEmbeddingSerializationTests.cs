using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Verifies that <see cref="DurableEmbeddingOutput"/> (containing
/// <see cref="GeneratedEmbeddings{T}"/> of <see cref="Embedding{T}"/> with float vectors)
/// round-trips correctly through <see cref="DurableAIDataConverter"/>.
///
/// This is load-bearing because <see cref="Embedding{T}.Vector"/> is
/// <see cref="System.ReadOnlyMemory{T}"/>, which has historically been a tripwire
/// for JSON source-gen pipelines.
/// </summary>
public class DurableEmbeddingSerializationTests
{
    [Fact]
    public void DurableEmbeddingOutput_RoundTrips_SingleEmbedding()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var vector = new float[] { 0.1f, 0.2f, 0.3f };
        var embedding = new Embedding<float>(vector)
        {
            ModelId = "text-embedding-3-small",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var generated = new GeneratedEmbeddings<Embedding<float>>([embedding]);
        var output = new DurableEmbeddingOutput { Embeddings = generated };

        var payload = converter.ToPayload(output);
        var deserialized = (DurableEmbeddingOutput)converter.ToValue(
            payload, typeof(DurableEmbeddingOutput))!;

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Embeddings);
        Assert.Single(deserialized.Embeddings);

        var roundTripped = deserialized.Embeddings[0];
        Assert.Equal(vector, roundTripped.Vector.ToArray());
        Assert.Equal("text-embedding-3-small", roundTripped.ModelId);
        Assert.Equal(embedding.CreatedAt, roundTripped.CreatedAt);
    }

    [Fact]
    public void DurableEmbeddingOutput_RoundTrips_MultipleEmbeddings()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var v1 = new float[] { 0.1f, 0.2f, 0.3f };
        var v2 = new float[] { -0.4f, 0.5f, -0.6f, 0.7f };
        var e1 = new Embedding<float>(v1) { ModelId = "model-A" };
        var e2 = new Embedding<float>(v2) { ModelId = "model-B" };
        var generated = new GeneratedEmbeddings<Embedding<float>>([e1, e2]);
        var output = new DurableEmbeddingOutput { Embeddings = generated };

        var payload = converter.ToPayload(output);
        var deserialized = (DurableEmbeddingOutput)converter.ToValue(
            payload, typeof(DurableEmbeddingOutput))!;

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Embeddings.Count);
        Assert.Equal(v1, deserialized.Embeddings[0].Vector.ToArray());
        Assert.Equal(v2, deserialized.Embeddings[1].Vector.ToArray());
        Assert.Equal("model-A", deserialized.Embeddings[0].ModelId);
        Assert.Equal("model-B", deserialized.Embeddings[1].ModelId);
    }

    [Fact]
    public void DurableEmbeddingOutput_RoundTrips_EmptyVector()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var embedding = new Embedding<float>(Array.Empty<float>());
        var generated = new GeneratedEmbeddings<Embedding<float>>([embedding]);
        var output = new DurableEmbeddingOutput { Embeddings = generated };

        var payload = converter.ToPayload(output);
        var deserialized = (DurableEmbeddingOutput)converter.ToValue(
            payload, typeof(DurableEmbeddingOutput))!;

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Embeddings);
        Assert.Empty(deserialized.Embeddings[0].Vector.ToArray());
    }

    [Fact]
    public void DurableEmbeddingOutput_RoundTrips_UsageDetailsOnGeneratedEmbeddings()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var embedding = new Embedding<float>(new float[] { 1.0f, 2.0f });
        var generated = new GeneratedEmbeddings<Embedding<float>>([embedding])
        {
            Usage = new UsageDetails { InputTokenCount = 3, TotalTokenCount = 3 },
        };
        var output = new DurableEmbeddingOutput { Embeddings = generated };

        var payload = converter.ToPayload(output);
        var deserialized = (DurableEmbeddingOutput)converter.ToValue(
            payload, typeof(DurableEmbeddingOutput))!;

        // BUG: GeneratedEmbeddings.Usage is dropped on serialize. The source-gen
        // context for DurableEmbeddingOutput treats GeneratedEmbeddings<T> as a
        // bare collection and does not emit its non-element properties
        // (Usage, AdditionalProperties). Wire JSON observed:
        //   {"embeddings":[{"vector":[1,2]}]}
        // The vectors themselves round-trip correctly (ReadOnlyMemory<float> is fine);
        // the loss is restricted to the GeneratedEmbeddings wrapper's own properties.
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Embeddings.Usage);
        Assert.Equal(3, deserialized.Embeddings.Usage!.InputTokenCount);
        Assert.Equal(3, deserialized.Embeddings.Usage!.TotalTokenCount);
        Assert.Equal(new float[] { 1.0f, 2.0f }, deserialized.Embeddings[0].Vector.ToArray());
    }

    [Fact]
    public void DurableEmbeddingOutput_RoundTrips_AdditionalPropertiesAndUsageOnGeneratedEmbeddings()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var embedding = new Embedding<float>(new float[] { 0.5f, -0.5f });
        var generated = new GeneratedEmbeddings<Embedding<float>>([embedding])
        {
            // Bonus: Usage + AdditionalProperties set simultaneously to ensure the
            // converter writes BOTH (guards against ordering bugs in the converter).
            Usage = new UsageDetails { InputTokenCount = 7, TotalTokenCount = 7 },
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["provider"] = "openai",
                ["request_id"] = "req_abc123",
                ["latency_ms"] = 42,
            },
        };
        var output = new DurableEmbeddingOutput { Embeddings = generated };

        var payload = converter.ToPayload(output);
        var deserialized = (DurableEmbeddingOutput)converter.ToValue(
            payload, typeof(DurableEmbeddingOutput))!;

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Embeddings.AdditionalProperties);
        var props = deserialized.Embeddings.AdditionalProperties!;
        Assert.Equal(3, props.Count);

        // STJ deserializes IDictionary<string, object?> values as JsonElement.
        // Verify each value survives by extracting via the JsonElement API rather
        // than direct CLR-type comparison.
        Assert.True(props.ContainsKey("provider"));
        Assert.True(props.ContainsKey("request_id"));
        Assert.True(props.ContainsKey("latency_ms"));

        AssertStringValue(props["provider"], "openai");
        AssertStringValue(props["request_id"], "req_abc123");
        AssertInt32Value(props["latency_ms"], 42);

        // Bonus assertion: Usage also survives when set alongside AdditionalProperties.
        Assert.NotNull(deserialized.Embeddings.Usage);
        Assert.Equal(7, deserialized.Embeddings.Usage!.InputTokenCount);
        Assert.Equal(7, deserialized.Embeddings.Usage!.TotalTokenCount);

        static void AssertStringValue(object? value, string expected)
        {
            if (value is JsonElement el)
            {
                Assert.Equal(JsonValueKind.String, el.ValueKind);
                Assert.Equal(expected, el.GetString());
            }
            else
            {
                Assert.Equal(expected, value);
            }
        }

        static void AssertInt32Value(object? value, int expected)
        {
            if (value is JsonElement el)
            {
                Assert.Equal(JsonValueKind.Number, el.ValueKind);
                Assert.Equal(expected, el.GetInt32());
            }
            else
            {
                Assert.Equal(expected, Convert.ToInt32(value));
            }
        }
    }

    /// <summary>
    /// Regression: histories written before <see cref="GeneratedEmbeddingsJsonConverter{TEmbedding}"/>
    /// existed used the source-gen IList&lt;T&gt; collapse — i.e. a bare JSON array of embeddings
    /// with no envelope. The Read() path MUST accept that shape so in-flight workflows on the prior
    /// library version don't fail replay when the new converter is loaded.
    ///
    /// Wire shape (pre-converter, legacy): <c>[{"vector":[...]}, ...]</c>
    /// Wire shape (current, envelope):    <c>{"embeddings":[...], "usage":..., "additionalProperties":...}</c>
    /// </summary>
    [Fact]
    public void GeneratedEmbeddingsJsonConverter_ReadsBareArrayFallback()
    {
        const string legacyWire = "[{\"vector\":[0.1,0.2,0.3]}]";

        var deserialized = JsonSerializer.Deserialize<GeneratedEmbeddings<Embedding<float>>>(
            legacyWire, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized!);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f }, deserialized[0].Vector.ToArray());
        Assert.Null(deserialized.Usage);
        // Constructor leaves AdditionalProperties null; legacy wire never carried it.
        Assert.Null(deserialized.AdditionalProperties);
    }

    [Fact]
    public void GeneratedEmbeddingsJsonConverter_ReadsBareArrayFallback_Empty()
    {
        const string legacyWire = "[]";

        var deserialized = JsonSerializer.Deserialize<GeneratedEmbeddings<Embedding<float>>>(
            legacyWire, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Empty(deserialized!);
        Assert.Null(deserialized.Usage);
        Assert.Null(deserialized.AdditionalProperties);
    }

    [Fact]
    public void GeneratedEmbeddingsJsonConverter_ReadsBareArrayFallback_PreservesOrder()
    {
        const string legacyWire =
            "[{\"vector\":[1.0,0.0,0.0]},{\"vector\":[0.0,1.0,0.0]},{\"vector\":[0.0,0.0,1.0]}]";

        var deserialized = JsonSerializer.Deserialize<GeneratedEmbeddings<Embedding<float>>>(
            legacyWire, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized!.Count);
        Assert.Equal(new float[] { 1.0f, 0.0f, 0.0f }, deserialized[0].Vector.ToArray());
        Assert.Equal(new float[] { 0.0f, 1.0f, 0.0f }, deserialized[1].Vector.ToArray());
        Assert.Equal(new float[] { 0.0f, 0.0f, 1.0f }, deserialized[2].Vector.ToArray());
    }
}
