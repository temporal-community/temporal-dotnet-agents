using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// JSON converter for <see cref="ChatOptions"/> that preserves the names of any
/// <see cref="ChatOptions.Tools"/> entries across the activity boundary.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AITool"/> is an abstract base type with multiple subclasses
/// (<see cref="AIFunction"/>, hosted-tool subtypes, etc.). MEAI's
/// <see cref="AIJsonUtilities.DefaultOptions"/> does not declare a polymorphic
/// discriminator mapping for <see cref="AITool"/>, so the default
/// <see cref="ChatOptions.Tools"/> property silently collapses to <see langword="null"/>
/// on the deserialize side — every subtype is unrecognized.
/// </para>
/// <para>
/// This converter ships a name-only wire format: serialization writes the list of
/// tool <see cref="AITool.Name"/> values into a sidecar <c>$toolNames</c> property
/// (instead of trying to round-trip the full tool instance), and deserialization
/// materializes <see cref="ToolNamePlaceholder"/> instances with only <see cref="AITool.Name"/>
/// populated. The activity layer (<c>DurableChatActivities.GetChatStepAsync</c> /
/// <c>GetResponseAsync</c>) is responsible for swapping each placeholder with the
/// real <see cref="AIFunction"/> from <c>DurableFunctionRegistry</c> before the LLM
/// call. Hosted-tool subtypes (no registry entry) are out of scope — they were
/// already silently dropped before this fix.
/// </para>
/// <para>
/// Wire shape: <c>{ ... ChatOptions scalars ..., "$toolNames": ["a","b"] }</c> when
/// <see cref="ChatOptions.Tools"/> is non-empty; otherwise the sidecar is omitted.
/// Old workflow histories without <c>$toolNames</c> deserialize as
/// <see cref="ChatOptions.Tools"/> = <see langword="null"/> (the registry-auto-populate
/// fallback already in place).
/// </para>
/// </remarks>
internal sealed class ChatOptionsToolsJsonConverter : JsonConverter<ChatOptions>
{
    // Sidecar property name. The leading '$' both signals "non-MEAI-canonical wire
    // metadata" and guarantees no collision with future MEAI ChatOptions properties.
    private const string ToolNamesProperty = "$toolNames";

    /// <summary>
    /// Sibling <see cref="JsonSerializerOptions"/> used to serialize / deserialize the
    /// "everything except Tools" portion of <see cref="ChatOptions"/>. Built lazily from
    /// the caller-supplied options minus *this* converter to avoid infinite recursion.
    /// </summary>
    private JsonSerializerOptions? _siblingOptionsCache;
    private readonly object _siblingLock = new();

    private JsonSerializerOptions GetSiblingOptions(JsonSerializerOptions options)
    {
        if (_siblingOptionsCache is not null)
        {
            return _siblingOptionsCache;
        }

        lock (_siblingLock)
        {
            if (_siblingOptionsCache is not null)
            {
                return _siblingOptionsCache;
            }

            var clone = new JsonSerializerOptions(options);
            for (var i = clone.Converters.Count - 1; i >= 0; i--)
            {
                if (clone.Converters[i] is ChatOptionsToolsJsonConverter)
                {
                    clone.Converters.RemoveAt(i);
                }
            }
            clone.MakeReadOnly();
            _siblingOptionsCache = clone;
            return clone;
        }
    }

    public override ChatOptions? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        // Read the whole object into a JsonNode so we can extract our sidecar field,
        // then defer remaining-property deserialization to the sibling options.
        var node = JsonNode.Parse(ref reader);
        if (node is not JsonObject obj)
        {
            // ChatOptions is always an object on the wire; null was handled above.
            throw new JsonException(
                $"Expected JSON object for ChatOptions, got {node?.GetValueKind().ToString() ?? "null"}.");
        }

        JsonArray? toolNames = null;
        if (obj.TryGetPropertyValue(ToolNamesProperty, out var sidecar) && sidecar is JsonArray arr)
        {
            toolNames = arr;
            // Strip before deserializing — sibling options has no knowledge of $toolNames.
            obj.Remove(ToolNamesProperty);
        }

        var sibling = GetSiblingOptions(options);
        // Re-emit the stripped object to JSON, then deserialize into ChatOptions via sibling.
        // (Cannot deserialize a JsonNode directly while we still hold the original reader.)
        var json = obj.ToJsonString(sibling);
        var result = JsonSerializer.Deserialize<ChatOptions>(json, sibling);
        if (result is null)
        {
            return null;
        }

        if (toolNames is not null && toolNames.Count > 0)
        {
            var placeholders = new List<AITool>(toolNames.Count);
            foreach (var entry in toolNames)
            {
                var name = entry?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                placeholders.Add(new ToolNamePlaceholder(name!));
            }
            result.Tools = placeholders;
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer, ChatOptions value, JsonSerializerOptions options)
    {
        var sibling = GetSiblingOptions(options);

        // Snapshot + temporarily null the Tools list so the sibling serializer doesn't
        // try (and fail silently) to write polymorphic AITool entries. We restore the
        // original list on the caller's instance before returning so this converter is
        // observationally pure with respect to the input.
        var originalTools = value.Tools;
        IReadOnlyList<string>? toolNames = null;
        if (originalTools is { Count: > 0 })
        {
            var names = new List<string>(originalTools.Count);
            foreach (var tool in originalTools)
            {
                // AITool.Name has a fallback of GetType().Name — placeholders and real
                // AIFunction instances both expose a meaningful Name here.
                names.Add(tool.Name);
            }
            toolNames = names;
            value.Tools = null;
        }

        try
        {
            // Serialize ChatOptions WITHOUT this converter, into a JsonNode, then inject
            // our $toolNames sidecar before writing to the original writer. Going through
            // JsonNode keeps us from having to enumerate / re-emit every ChatOptions
            // property by hand (forward-compat with new MEAI fields).
            var node = JsonSerializer.SerializeToNode(value, sibling);
            if (node is not JsonObject obj)
            {
                throw new JsonException(
                    "ChatOptions did not serialize to a JSON object (unexpected).");
            }

            if (toolNames is not null)
            {
                var arr = new JsonArray();
                foreach (var name in toolNames)
                {
                    arr.Add(name);
                }
                obj[ToolNamesProperty] = arr;
            }

            obj.WriteTo(writer, sibling);
        }
        finally
        {
            value.Tools = originalTools;
        }
    }
}

/// <summary>
/// Placeholder <see cref="AIFunction"/> instance that survives serialization with only
/// its <see cref="AITool.Name"/> populated. Activity-layer code is responsible for
/// swapping these out with real <see cref="AIFunction"/> entries from
/// <c>DurableFunctionRegistry</c> before invoking the LLM.
/// </summary>
/// <remarks>
/// Deriving from <see cref="AIFunction"/> (rather than <see cref="AITool"/> directly)
/// makes the placeholder type-equivalent to the real tools the registry holds, so
/// callers that do <c>options.Tools.OfType&lt;AIFunction&gt;()</c> still see entries
/// post-deserialize. <see cref="InvokeCoreAsync"/> throws — the placeholder is never
/// meant to be invoked directly; if it leaks past the activity-side swap, that's a
/// bug worth surfacing loudly rather than silently no-op-ing.
/// </remarks>
internal sealed class ToolNamePlaceholder : AIFunction
{
    public ToolNamePlaceholder(string name)
    {
        Name = name;
    }

    /// <inheritdoc/>
    public override string Name { get; }

    /// <inheritdoc/>
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            $"Tool placeholder '{Name}' was not swapped for a real registry entry before invocation. " +
            "This is a bug in Temporalio.Extensions.AI — please file an issue.");
}
