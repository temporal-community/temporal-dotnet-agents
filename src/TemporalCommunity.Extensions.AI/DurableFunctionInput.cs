using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Serializable input for the durable function invocation activity.
/// </summary>
internal sealed record class DurableFunctionInput
{

    /// <summary>
    /// The name of the <see cref="Microsoft.Extensions.AI.AIFunction"/> to invoke.
    /// </summary>
    public required string FunctionName { get; init; }

    /// <summary>
    /// The arguments to pass to the function.
    /// </summary>
    public IDictionary<string, object?>? Arguments { get; init; }

    public Internal.DurableFunctionDeclarationSnapshot? Declaration { get; init; }

    public string? ToolsetId { get; init; }

    public string? ActivationKey { get; init; }

    public string? MemberIdentityFingerprint { get; init; }

    public string? AuthorityBindingFingerprint { get; init; }

    public string? ManifestFingerprint { get; init; }

    public JsonElement? RequestData { get; init; }

    public JsonElement? TurnState { get; set; }

    [JsonIgnore]
    public DurableToolDispatchMode DispatchMode { get; init; } = DurableToolDispatchMode.Parallel;

    [JsonInclude]
    [JsonPropertyName("dispatchMode")]
    internal DurableToolDispatchMode? SerializedDispatchMode
    {
        get => this.DispatchMode;
        init
        {
            if (value.HasValue)
            {
                this.DispatchMode = value.Value;
            }
        }
    }

    public string? ToolCallId { get; init; }

    public int ModelIteration { get; init; }

    public int CallIndex { get; init; }

    public string? ConversationId { get; init; }

    public string? CorrelationId { get; init; }

    public int IdempotencyKeyVersion { get; init; }

}
