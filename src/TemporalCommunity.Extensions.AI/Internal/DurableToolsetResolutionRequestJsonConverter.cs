using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Preserves the version-one default for toolset requests written before
/// <see cref="DurableToolsetResolutionRequest.ResolutionVersion"/> was emitted.
/// </summary>
/// <remarks>
/// Source-generated metadata does not apply the record's property initializer for an absent JSON
/// member. Keeping this tiny converter explicit preserves the replay wire contract without
/// falling back to reflection for the durable DTO.
/// </remarks>
internal sealed class DurableToolsetResolutionRequestJsonConverter
    : JsonConverter<DurableToolsetResolutionRequest>
{
    public override DurableToolsetResolutionRequest Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A durable toolset resolution request must be a JSON object.");
        }

        var resolutionVersion = DurableToolsetResolutionRequest.CurrentVersion;
        var useWorkerDefaults = false;
        List<string>? toolsetIds = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A durable toolset resolution request contains malformed JSON.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("A durable toolset resolution request contains malformed JSON.");
            }

            if (PropertyEquals(propertyName, "resolutionVersion", options))
            {
                resolutionVersion = reader.GetInt32();
            }
            else if (PropertyEquals(propertyName, "useWorkerDefaults", options))
            {
                useWorkerDefaults = reader.GetBoolean();
            }
            else if (PropertyEquals(propertyName, "toolsetIds", options))
            {
                toolsetIds = ReadToolsetIds(ref reader);
            }
            else
            {
                reader.Skip();
            }
        }

        return new DurableToolsetResolutionRequest
        {
            ResolutionVersion = resolutionVersion,
            UseWorkerDefaults = useWorkerDefaults,
            ToolsetIds = toolsetIds,
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        DurableToolsetResolutionRequest value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("resolutionVersion", value.ResolutionVersion);
        writer.WriteBoolean("useWorkerDefaults", value.UseWorkerDefaults);
        if (value.ToolsetIds is not null)
        {
            writer.WritePropertyName("toolsetIds");
            writer.WriteStartArray();
            foreach (var toolsetId in value.ToolsetIds)
            {
                writer.WriteStringValue(toolsetId);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static List<string>? ReadToolsetIds(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Durable toolset IDs must be a JSON array or null.");
        }

        var toolsetIds = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("A durable toolset ID must be a JSON string.");
            }

            toolsetIds.Add(reader.GetString()!);
        }

        return toolsetIds;
    }

    private static bool PropertyEquals(string? actual, string expected, JsonSerializerOptions options) =>
        string.Equals(
            actual,
            expected,
            options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
