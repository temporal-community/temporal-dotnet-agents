using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Workflows;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Workflows;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// An <see cref="AIAgent"/> for use outside of Temporal workflows (e.g., HTTP handlers, console apps).
/// Delegates to <see cref="ITemporalAgentClient"/> which communicates with <see cref="AgentWorkflow"/>
/// via Temporal workflow updates — no polling needed.
/// </summary>
/// <remarks>
/// Use from external/host code or from a Temporal activity to interact with a
/// Temporal-hosted agent. Workflow code must invoke it through an activity.
/// </remarks>
internal class TemporalAIAgentProxy(
    string name,
    ITemporalAgentClient agentClient,
    ILogger<TemporalAIAgentProxy>? logger = null) : AIAgent
{
    private readonly ITemporalAgentClient _agentClient = agentClient;
    private readonly ILogger<TemporalAIAgentProxy> _logger =
        logger ?? NullLogger<TemporalAIAgentProxy>.Instance;

    public override string? Name { get; } = name;

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session is not TemporalAgentSession temporalSession)
        {
            throw new InvalidOperationException(
                $"Expected a {nameof(TemporalAgentSession)} but got '{session.GetType().Name}'.");
        }

        return new ValueTask<JsonElement>(temporalSession.Serialize(jsonSerializerOptions));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<AgentSession>(TemporalAgentSession.Deserialize(serializedState, jsonSerializerOptions));
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = TemporalAgentSessionId.WithRandomKey(this.Name!);
        _logger.LogProxySessionCreated(sessionId.AgentName, sessionId.WorkflowId);
        return new ValueTask<AgentSession>(new TemporalAgentSession(sessionId));
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (Workflow.InWorkflow)
        {
            throw new InvalidOperationException(
                "TemporalAIAgentProxy cannot be invoked from inside a Temporal workflow. " +
                "Invoke the registered agent from an activity and pass deterministic session/request IDs " +
                "from the workflow when retries must address the same turn.");
        }

        session ??= await CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        if (session is not TemporalAgentSession temporalSession)
        {
            throw new ArgumentException(
                "The provided session is not valid for a Temporal agent. " +
                "Create a new session using CreateSessionAsync or provide a session previously created by this agent.",
                paramName: nameof(session));
        }

        ValidateSessionOwnership(temporalSession, nameof(session));

        IList<string>? enableToolNames = null;
        bool enableToolCalls = true;
        bool isFireAndForget = false;
        string? callerCorrelationId = null;
        string? callerRequestId = null;
        ChatResponseFormat? responseFormat = null;

        if (options is not null
            && options.GetType() != typeof(AgentRunOptions)
            && options.GetType() != typeof(TemporalAgentRunOptions)
            && options.GetType() != typeof(ChatClientAgentRunOptions))
        {
            throw new NotSupportedException(
                $"Run options of type '{options.GetType().FullName}' are not supported by Temporal agent '{Name}'. " +
                $"Use {nameof(AgentRunOptions)}, {nameof(TemporalAgentRunOptions)}, or {nameof(ChatClientAgentRunOptions)}.");
        }

        if (options is not null)
        {
            ValidateBaseRunOptions(options);
            responseFormat = options.ResponseFormat;
        }

        if (options is TemporalAgentRunOptions temporalOptions)
        {
            enableToolCalls = temporalOptions.EnableToolCalls;
            enableToolNames = temporalOptions.EnableToolNames;
            isFireAndForget = temporalOptions.IsFireAndForget;
            callerCorrelationId = temporalOptions.CorrelationId;
            callerRequestId = temporalOptions.RequestId;
        }
        else if (options is ChatClientAgentRunOptions chatOptions)
        {
            if (chatOptions.ChatClientFactory is not null)
            {
                throw new NotSupportedException(
                    $"ChatClientAgentRunOptions.ChatClientFactory is not supported by Temporal agent '{Name}'. " +
                    "Configure the chat client when registering the Temporal agent.");
            }

            ValidateRunChatOptions(chatOptions.ChatOptions);
            responseFormat = chatOptions.ChatOptions?.ResponseFormat;
        }

        if (options?.ResponseFormat is { } format)
        {
            responseFormat = format;
        }

        var request = new RunRequest([.. messages], responseFormat, enableToolCalls, enableToolNames)
        {
            CorrelationId = string.IsNullOrEmpty(callerCorrelationId)
                ? Guid.NewGuid().ToString("N")
                : callerCorrelationId,
            RequestId = TemporalCommunity.Extensions.AI.Internal.DurableRequestFingerprint
                .ResolveRequestId(callerRequestId),
        };
        var sessionId = temporalSession.SessionId;

        _logger.LogProxyDispatchingRequest(sessionId.AgentName, sessionId.WorkflowId, isFireAndForget);

        if (isFireAndForget)
        {
            await _agentClient.RunAgentFireAndForgetAsync(sessionId, request, cancellationToken).ConfigureAwait(false);
            return new AgentResponse();
        }

        var response = await _agentClient.SendAsync(sessionId, request, cancellationToken).ConfigureAwait(false);
        if (TemporalAgentResponseMetadata.TryGetStateBag(response, out var stateBag, out var revision))
        {
            temporalSession.ApplyObservedStateBag(stateBag, revision);
        }

        return response;
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"RunStreamingAsync is not supported for Temporal agent '{Name}'. " +
            "Use RunAsync for a completed, durable response.");
    }

    private void ValidateRunChatOptions(ChatOptions? options)
    {
        if (options is null)
        {
            return;
        }

        if (options.GetType() != typeof(ChatOptions))
        {
            throw new NotSupportedException(
                $"Custom ChatOptions type '{options.GetType().FullName}' is not supported by Temporal agent '{Name}'.");
        }

        var unsupported = new List<string>();
        if (options.ConversationId is not null) unsupported.Add(nameof(options.ConversationId));
        if (options.Instructions is not null) unsupported.Add(nameof(options.Instructions));
        if (options.Temperature is not null) unsupported.Add(nameof(options.Temperature));
        if (options.MaxOutputTokens is not null) unsupported.Add(nameof(options.MaxOutputTokens));
        if (options.TopP is not null) unsupported.Add(nameof(options.TopP));
        if (options.TopK is not null) unsupported.Add(nameof(options.TopK));
        if (options.FrequencyPenalty is not null) unsupported.Add(nameof(options.FrequencyPenalty));
        if (options.PresencePenalty is not null) unsupported.Add(nameof(options.PresencePenalty));
        if (options.Seed is not null) unsupported.Add(nameof(options.Seed));
        if (options.Reasoning is not null) unsupported.Add(nameof(options.Reasoning));
        if (options.ModelId is not null) unsupported.Add(nameof(options.ModelId));
        if (options.StopSequences is { Count: > 0 }) unsupported.Add(nameof(options.StopSequences));
        if (options.AllowMultipleToolCalls is not null) unsupported.Add(nameof(options.AllowMultipleToolCalls));
        if (options.ToolMode is not null) unsupported.Add(nameof(options.ToolMode));
        if (options.Tools is { Count: > 0 }) unsupported.Add(nameof(options.Tools));
        if (options.AllowBackgroundResponses is true) unsupported.Add(nameof(options.AllowBackgroundResponses));
        if (options.ContinuationToken is not null) unsupported.Add(nameof(options.ContinuationToken));
        if (options.RawRepresentationFactory is not null) unsupported.Add(nameof(options.RawRepresentationFactory));
        if (options.AdditionalProperties is { Count: > 0 }) unsupported.Add(nameof(options.AdditionalProperties));

        if (unsupported.Count > 0)
        {
            throw new NotSupportedException(
                $"Temporal agent '{Name}' only supports ChatClientAgentRunOptions.ChatOptions.ResponseFormat at run time. " +
                $"Unsupported settings: {string.Join(", ", unsupported)}.");
        }
    }

    private void ValidateBaseRunOptions(AgentRunOptions options)
    {
        var unsupported = new List<string>();
        if (options.ContinuationToken is not null) unsupported.Add(nameof(options.ContinuationToken));
        if (options.AllowBackgroundResponses is true) unsupported.Add(nameof(options.AllowBackgroundResponses));
        if (options.AdditionalProperties is { Count: > 0 }) unsupported.Add(nameof(options.AdditionalProperties));

        if (unsupported.Count > 0)
        {
            throw new NotSupportedException(
                $"Temporal agent '{Name}' supports only AgentRunOptions.ResponseFormat from the base run options. " +
                $"Unsupported settings: {string.Join(", ", unsupported)}.");
        }
    }

    private void ValidateSessionOwnership(TemporalAgentSession session, string parameterName)
    {
        if (!string.Equals(session.SessionId.AgentName, this.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The provided session belongs to agent '{session.SessionId.AgentName}', not agent '{this.Name}'. " +
                "Create the session from the same agent proxy that will use it.",
                parameterName);
        }
    }
}
