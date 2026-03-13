using System.Text.Json;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Options for controlling structured output deserialization when using
/// <see cref="TemporalAIAgentExtensions.RunAsync{T}"/> or
/// <see cref="TemporalAIAgentProxyExtensions.RunAsync{T}"/>.
/// </summary>
public sealed class StructuredOutputOptions
{
    /// <summary>
    /// Gets or sets the maximum number of retry attempts when JSON deserialization fails.
    /// On each retry the error context is appended to the conversation so the LLM can
    /// self-correct. Defaults to <c>2</c>.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Gets or sets whether to include the deserialization error message in the retry prompt.
    /// When <see langword="true"/>, the LLM receives the <see cref="JsonException"/> message
    /// and a reminder of the expected schema. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IncludeErrorContext { get; set; } = true;

    /// <summary>
    /// Gets or sets custom <see cref="JsonSerializerOptions"/> for deserialization.
    /// When <see langword="null"/>, <see cref="JsonSerializerOptions.Default"/> is used.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }
}
