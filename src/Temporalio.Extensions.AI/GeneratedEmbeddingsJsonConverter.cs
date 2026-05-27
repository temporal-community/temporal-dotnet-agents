using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// JSON converter for <see cref="GeneratedEmbeddings{TEmbedding}"/> that preserves the wrapper's
/// own properties (<see cref="GeneratedEmbeddings{TEmbedding}.Usage"/> and
/// <see cref="GeneratedEmbeddings{TEmbedding}.AdditionalProperties"/>) in addition to the
/// element sequence.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GeneratedEmbeddings{TEmbedding}"/> implements <see cref="IList{T}"/>. By default,
/// <see cref="JsonSerializer"/> (both reflection-based and source-generated resolvers) treats
/// any <see cref="IList{T}"/>-implementing type as a bare collection and emits only its
/// element sequence — silently dropping <c>Usage</c> and <c>AdditionalProperties</c>.
/// MEAI itself does not register a converter to round-trip these properties, so this library
/// supplies one for use with <see cref="DurableAIDataConverter"/>.
/// </para>
/// <para>
/// Wire shape: <c>{"embeddings":[...],"usage":{...},"additionalProperties":{...}}</c>.
/// </para>
/// </remarks>
internal sealed class GeneratedEmbeddingsJsonConverter<TEmbedding> : JsonConverter<GeneratedEmbeddings<TEmbedding>>
    where TEmbedding : Embedding
{
    public override GeneratedEmbeddings<TEmbedding>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        // Bare-array fallback for histories written before this converter existed (wire format was
        // the source-gen IList<T> collapse). New histories always use the envelope shape.
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var legacy = JsonSerializer.Deserialize<List<TEmbedding>>(ref reader, options) ?? new List<TEmbedding>();
            return new GeneratedEmbeddings<TEmbedding>(legacy);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"Expected StartObject or StartArray when reading GeneratedEmbeddings, got {reader.TokenType}.");
        }

        var result = new GeneratedEmbeddings<TEmbedding>();
        List<TEmbedding>? embeddings = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (embeddings is not null)
                {
                    result.AddRange(embeddings);
                }
                return result;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException(
                    $"Expected PropertyName, got {reader.TokenType}.");
            }

            var propertyName = reader.GetString();
            reader.Read();

            switch (propertyName)
            {
                case "embeddings":
                    embeddings = JsonSerializer.Deserialize<List<TEmbedding>>(ref reader, options);
                    break;
                case "usage":
                    result.Usage = JsonSerializer.Deserialize<UsageDetails>(ref reader, options);
                    break;
                case "additionalProperties":
                    result.AdditionalProperties = JsonSerializer.Deserialize<AdditionalPropertiesDictionary>(ref reader, options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Unexpected end of JSON when reading GeneratedEmbeddings.");
    }

    public override void Write(
        Utf8JsonWriter writer, GeneratedEmbeddings<TEmbedding> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("embeddings");
        writer.WriteStartArray();
        foreach (var embedding in value)
        {
            JsonSerializer.Serialize(writer, embedding, options);
        }
        writer.WriteEndArray();

        if (value.Usage is not null)
        {
            writer.WritePropertyName("usage");
            JsonSerializer.Serialize(writer, value.Usage, options);
        }

        if (value.AdditionalProperties is not null)
        {
            writer.WritePropertyName("additionalProperties");
            JsonSerializer.Serialize(writer, value.AdditionalProperties, options);
        }

        writer.WriteEndObject();
    }
}
