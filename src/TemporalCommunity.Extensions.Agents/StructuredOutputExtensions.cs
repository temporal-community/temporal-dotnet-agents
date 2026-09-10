using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Adds fence-tolerant, self-correcting typed output on top of MAF's structured output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the name is not <c>RunAsync&lt;T&gt;</c>.</b> MAF declares <c>AIAgent.RunAsync&lt;T&gt;</c>
/// as an instance method, and an instance method always beats an extension method in overload
/// resolution. An extension named <c>RunAsync&lt;T&gt;</c> is therefore unreachable from the call
/// shape most callers write — silently, because the only symptom is a different return type.
/// <c>RunStructuredAsync&lt;T&gt;</c> cannot be shadowed and states at the call site which
/// behaviour is in play.
/// </para>
/// <para>
/// <b>Relationship to MAF.</b> These methods call MAF's typed run internally, so the JSON schema
/// generated from the target type still reaches the model — you do not give that up by
/// choosing this API. What they add on top is markdown-code-fence stripping and a retry loop that
/// feeds the parse error back to the model. Use MAF's <c>RunAsync&lt;T&gt;</c> directly when the
/// provider reliably honours schema-constrained decoding and a hard failure is acceptable.
/// </para>
/// </remarks>
public static class StructuredOutputExtensions
{
    /// <summary>
    /// Web defaults (camelCase, case-insensitive) — what models actually emit. Cached because
    /// <see cref="JsonSerializerOptions"/> is expensive to construct and is frozen on first use.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonSerializerOptions.Default"/> would be PascalCase and case-SENSITIVE, which
    /// does not throw on camelCase input — it constructs the target with every member defaulted.
    /// That produced an all-null result with no exception, so the retry loop never fired.
    /// </remarks>
    internal static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs the agent and deserializes the response into <typeparamref name="T"/>, tolerating
    /// markdown code fences and retrying with error context so the model can self-correct.
    /// </summary>
    /// <param name="correlationId">
    /// Optional caller-supplied correlation ID for the underlying agent run. When
    /// <see langword="null"/>/empty, the library auto-generates one — using
    /// <c>Workflow.NewGuid()</c> when invoked from workflow context (deterministic on replay)
    /// or <c>Guid.NewGuid()</c> otherwise.
    /// </param>
    public static async Task<T> RunStructuredAsync<T>(
        this TemporalAIAgent agent,
        IList<ChatMessage> messages,
        AgentSession? session = null,
        StructuredOutputOptions? options = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StructuredOutputOptions();
        var serializerOptions = options.JsonSerializerOptions ?? WebDefaults;
        var workingMessages = new List<ChatMessage>(messages);
        var runOptions = BuildRunOptions(correlationId);

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            // MAF's typed run sets ResponseFormat = ChatResponseFormat.ForJsonSchema<T>() and
            // wraps non-object schemas. Going through it means the model is schema-constrained on
            // the first attempt rather than only being asked nicely.
            var response = await agent.RunAsync<T>(
                workingMessages, session, serializerOptions, runOptions, cancellationToken);

            if (TryRead(response, serializerOptions, attempt < options.MaxRetries,
                        out var value, out var text, out var failure))
            {
                return value!;
            }

            AppendCorrection<T>(workingMessages, text, failure!, options);
        }

        throw new InvalidOperationException("Structured output retry loop exited unexpectedly.");
    }

    /// <summary>
    /// Runs the agent proxy and deserializes the response into <typeparamref name="T"/>, tolerating
    /// markdown code fences and retrying with error context so the model can self-correct.
    /// </summary>
    /// <param name="correlationId">
    /// Optional caller-supplied correlation ID for the underlying agent run. When
    /// <see langword="null"/>/empty, the proxy auto-generates one (<c>Guid.NewGuid()</c>).
    /// </param>
    public static async Task<T> RunStructuredAsync<T>(
        this AIAgent agent,
        IList<ChatMessage> messages,
        AgentSession session,
        StructuredOutputOptions? options = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StructuredOutputOptions();
        var serializerOptions = options.JsonSerializerOptions ?? WebDefaults;
        var workingMessages = new List<ChatMessage>(messages);
        var runOptions = BuildRunOptions(correlationId);

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            var response = await agent.RunAsync<T>(
                workingMessages, session, serializerOptions, runOptions, cancellationToken);

            if (TryRead(response, serializerOptions, attempt < options.MaxRetries,
                        out var value, out var text, out var failure))
            {
                return value!;
            }

            AppendCorrection<T>(workingMessages, text, failure!, options);
        }

        throw new InvalidOperationException("Structured output retry loop exited unexpectedly.");
    }

    /// <summary>
    /// Runs the agent via <see cref="ITemporalAgentClient"/> and deserializes the response into
    /// <typeparamref name="T"/>, tolerating markdown code fences and retrying with error context.
    /// </summary>
    /// <param name="correlationId">
    /// Optional correlation ID for the first attempt. When <see langword="null"/>/empty, the
    /// existing <see cref="RunRequest.CorrelationId"/> on <paramref name="request"/> is used.
    /// Each retry attempt always generates a fresh correlation ID via <c>Guid.NewGuid()</c>.
    /// </param>
    public static async Task<T> RunStructuredAsync<T>(
        this ITemporalAgentClient client,
        TemporalAgentSessionId sessionId,
        RunRequest request,
        StructuredOutputOptions? options = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StructuredOutputOptions();
        var serializerOptions = options.JsonSerializerOptions ?? WebDefaults;
        var workingMessages = new List<ChatMessage>(request.Messages);

        // The client path has no MAF typed overload to borrow the schema from, so derive the same
        // response format here. A caller-supplied format on the request wins — they asked for it.
        var responseFormat = request.ResponseFormat ?? ChatResponseFormat.ForJsonSchema<T>(serializerOptions);

        var firstAttemptCorrelationId = string.IsNullOrEmpty(correlationId)
            ? request.CorrelationId
            : correlationId;

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            var currentRequest = new RunRequest(
                attempt == 0 ? request.Messages : workingMessages,
                responseFormat,
                request.EnableToolCalls,
                request.EnableToolNames)
            {
                CorrelationId = attempt == 0 ? firstAttemptCorrelationId : Guid.NewGuid().ToString("N"),
                OrchestrationId = request.OrchestrationId,
            };

            var response = await client.SendAsync(sessionId, currentRequest, cancellationToken);

            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new JsonException("Agent returned an empty response; cannot deserialize.");
            }

            try
            {
                return Deserialize<T>(text, wrappedInObject: false, serializerOptions);
            }
            catch (Exception ex) when (IsParseFailure(ex) && attempt < options.MaxRetries)
            {
                AppendCorrection<T>(workingMessages, text, ex, options);
            }
        }

        throw new InvalidOperationException("Structured output retry loop exited unexpectedly.");
    }

    /// <summary>
    /// Reads <paramref name="response"/> as <typeparamref name="T"/>, first as MAF would and then
    /// again with markdown code fences stripped. Returns <see langword="false"/> only when a retry
    /// remains; otherwise the parse failure is rethrown to the caller.
    /// </summary>
    private static bool TryRead<T>(
        AgentResponse<T> response,
        JsonSerializerOptions serializerOptions,
        bool retryRemains,
        out T? value,
        out string text,
        out Exception? failure)
    {
        text = response.Text;
        value = default;
        failure = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("Agent returned an empty response; cannot deserialize.");
        }

        try
        {
            // Fast path: schema-compliant output needs no salvage, and MAF's own reader already
            // unwraps a wrapped non-object schema and tolerates trailing prose.
            value = response.Result;
            return true;
        }
        catch (Exception first) when (IsParseFailure(first))
        {
            var stripped = MarkdownCodeFenceHelper.StripMarkdownCodeFences(text);
            if (!string.Equals(stripped, text, StringComparison.Ordinal))
            {
                try
                {
                    value = Deserialize<T>(stripped, response.IsWrappedInObject, serializerOptions);
                    return true;
                }
                catch (Exception second) when (IsParseFailure(second) && retryRemains)
                {
                    failure = second;
                    return false;
                }
            }

            if (!retryRemains)
            {
                throw;
            }

            failure = first;
            return false;
        }
    }

    /// <summary>
    /// Deserializes through MAF's own reader so wrapped non-object schemas and trailing prose are
    /// handled identically to <see cref="AgentResponse{T}.Result"/>. The unwrap helper MAF uses is
    /// internal to that assembly, so reconstructing the response is how it is reached.
    /// </summary>
    private static T Deserialize<T>(string text, bool wrappedInObject, JsonSerializerOptions serializerOptions) =>
        new AgentResponse<T>(new AgentResponse(new ChatMessage(ChatRole.Assistant, text)), serializerOptions)
        {
            IsWrappedInObject = wrappedInObject,
        }.Result;

    /// <summary>
    /// <see cref="AgentResponse{T}.Result"/> reports malformed JSON as <see cref="JsonException"/>
    /// but a null or absent payload as <see cref="InvalidOperationException"/>. Both are recoverable
    /// by asking the model again; neither should swallow an unrelated fault.
    /// </summary>
    private static bool IsParseFailure(Exception ex) =>
        ex is JsonException or InvalidOperationException;

    private static void AppendCorrection<T>(
        List<ChatMessage> workingMessages,
        string text,
        Exception failure,
        StructuredOutputOptions options)
    {
        if (!options.IncludeErrorContext)
        {
            return;
        }

        workingMessages.Add(new ChatMessage(ChatRole.Assistant, text));
        workingMessages.Add(new ChatMessage(ChatRole.User,
            $"Your response could not be parsed as valid JSON. Error: {failure.Message}\n" +
            $"Please respond with ONLY valid JSON matching the expected schema for type '{typeof(T).Name}'. " +
            "Do not wrap it in markdown code fences."));
    }

    /// <summary>
    /// Builds a <see cref="TemporalAgentRunOptions"/> carrying only the supplied correlation ID,
    /// or returns <see langword="null"/> when none was supplied so the caller can pass the
    /// original (possibly null) options through unchanged.
    /// </summary>
    private static TemporalAgentRunOptions? BuildRunOptions(string? correlationId) =>
        string.IsNullOrEmpty(correlationId) ? null : new TemporalAgentRunOptions { CorrelationId = correlationId };
}
