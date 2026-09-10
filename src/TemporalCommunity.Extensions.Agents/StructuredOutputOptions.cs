using System.Text.Json;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Options for controlling structured output deserialization when using
/// <see cref="StructuredOutputExtensions.RunStructuredAsync{T}(TemporalAIAgent, System.Collections.Generic.IList{Microsoft.Extensions.AI.ChatMessage}, Microsoft.Agents.AI.AgentSession, StructuredOutputOptions, string, System.Threading.CancellationToken)"/>
/// and its <see cref="Microsoft.Agents.AI.AIAgent"/> and <see cref="ITemporalAgentClient"/> overloads.
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
    /// When <see langword="null"/>, web defaults are used — camelCase and case-insensitive,
    /// matching what models emit and what MAF's own structured output expects.
    /// </summary>
    /// <remarks>
    /// Setting this replaces the default entirely. Beware
    /// <see cref="JsonSerializerOptions.Default"/> here: it is PascalCase and case-sensitive, and
    /// against camelCase model output it does not throw — it returns an instance with every member
    /// defaulted, which the retry loop cannot detect because no exception is raised.
    /// </remarks>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }
}
