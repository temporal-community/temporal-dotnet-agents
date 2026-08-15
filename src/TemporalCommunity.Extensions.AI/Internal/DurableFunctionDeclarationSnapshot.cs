using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal sealed record DurableFunctionDeclarationSnapshot
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required JsonElement JsonSchema { get; init; }

    public JsonElement? ReturnJsonSchema { get; init; }

    public required string JsonSchemaFingerprint { get; init; }

    public string? ReturnJsonSchemaFingerprint { get; init; }

    public static DurableFunctionDeclarationSnapshot Create(AIFunctionDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ValidateNoAdditionalProperties(declaration, "declaration");

        var schema = declaration.JsonSchema.Clone();
        var returnSchema = declaration.ReturnJsonSchema?.Clone();
        return new DurableFunctionDeclarationSnapshot
        {
            Name = declaration.Name,
            Description = declaration.Description,
            JsonSchema = schema,
            ReturnJsonSchema = returnSchema,
            JsonSchemaFingerprint = DurableJsonSchemaFingerprint.Create(schema),
            ReturnJsonSchemaFingerprint = returnSchema is { } value
                ? DurableJsonSchemaFingerprint.Create(value)
                : null,
        };
    }

    public AIFunctionDeclaration ToDeclaration() => new SnapshotDeclaration(this);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)
            || !string.Equals(
                JsonSchemaFingerprint,
                DurableJsonSchemaFingerprint.Create(JsonSchema),
                StringComparison.Ordinal)
            || !string.Equals(
                ReturnJsonSchemaFingerprint,
                ReturnJsonSchema is { } returnSchema
                    ? DurableJsonSchemaFingerprint.Create(returnSchema)
                    : null,
                StringComparison.Ordinal))
        {
            throw DurableToolsetManifest.Failure(
                "A durable function declaration snapshot is invalid.",
                DurableToolsetValidationReasons.InvalidDeclaration);
        }
    }

    public void ValidateImplementation(AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        try
        {
            ValidateNoAdditionalProperties(function, "implementation");

            var actualSchema = DurableJsonSchemaFingerprint.Create(function.JsonSchema);
            var actualReturnSchema = function.ReturnJsonSchema is { } returnSchema
                ? DurableJsonSchemaFingerprint.Create(returnSchema)
                : null;

            if (!string.Equals(Name, function.Name, StringComparison.Ordinal)
                || !string.Equals(JsonSchemaFingerprint, actualSchema, StringComparison.Ordinal)
                || !string.Equals(ReturnJsonSchemaFingerprint, actualReturnSchema, StringComparison.Ordinal))
            {
                throw new DurableConfigurationException(
                    $"Durable tool implementation '{function.Name}' does not match frozen declaration '{Name}'.");
            }
        }
        catch (DurableConfigurationException exception)
        {
            throw new ApplicationFailureException(
                exception.Message,
                exception,
                errorType: nameof(DurableConfigurationException),
                nonRetryable: true);
        }
    }

    private static void ValidateNoAdditionalProperties(AITool tool, string role)
    {
        if (tool.AdditionalProperties.Count == 0)
        {
            return;
        }

        var keys = string.Join(", ", tool.AdditionalProperties.Keys.OrderBy(k => k, StringComparer.Ordinal));
        throw new DurableConfigurationException(
            $"Durable tool {role} '{tool.Name}' has unsupported AdditionalProperties: {keys}. " +
            "Durable declaration snapshots require an empty AdditionalProperties dictionary.");
    }

    private sealed class SnapshotDeclaration(DurableFunctionDeclarationSnapshot snapshot)
        : AIFunctionDeclaration
    {
        public override string Name => snapshot.Name;

        public override string Description => snapshot.Description;

        public override JsonElement JsonSchema => snapshot.JsonSchema;

        public override JsonElement? ReturnJsonSchema => snapshot.ReturnJsonSchema;
    }
}

internal static class DurableJsonSchemaFingerprint
{
    public static string Create(JsonElement schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, schema);
        }

        var bytes = stream.ToArray();
#if NET10_0_OR_GREATER
        var hash = SHA256.HashData(bytes);
#else
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);
#endif
        var result = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject().ToList();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in properties)
                {
                    if (!names.Add(property.Name))
                    {
                        throw new DurableConfigurationException(
                            $"JSON schema contains duplicate property '{property.Name}'.");
                    }
                }

                foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;

            case JsonValueKind.Number:
                if (value.TryGetDecimal(out var decimalValue))
                {
                    writer.WriteNumberValue(decimalValue);
                }
                else if (value.TryGetDouble(out var doubleValue))
                {
                    writer.WriteNumberValue(doubleValue);
                }
                else
                {
                    writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                }

                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new DurableConfigurationException(
                    $"Unsupported JSON schema value kind '{value.ValueKind}'.");
        }
    }
}
