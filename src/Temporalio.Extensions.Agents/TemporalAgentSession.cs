// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// An <see cref="AgentSession"/> implementation for Temporal agents.
/// </summary>
public sealed class TemporalAgentSession : AgentSession
{
    /// <summary>
    /// Initializes a new <see cref="TemporalAgentSession"/> with the given session ID.
    /// Use this to reconnect to an existing agent session by its known workflow ID.
    /// </summary>
    public TemporalAgentSession(TemporalAgentSessionId sessionId)
    {
        this.SessionId = sessionId;
    }

    [JsonConstructor]
    internal TemporalAgentSession(TemporalAgentSessionId sessionId, AgentSessionStateBag stateBag) : base(stateBag)
    {
        this.SessionId = sessionId;
    }

    /// <summary>Gets the Temporal agent session ID.</summary>
    [JsonInclude]
    [JsonPropertyName("sessionId")]
    public TemporalAgentSessionId SessionId { get; }

    internal JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var opts = jsonSerializerOptions ?? TemporalAgentJsonUtilities.DefaultOptions;
        return JsonSerializer.SerializeToElement(this, opts.GetTypeInfo(typeof(TemporalAgentSession)));
    }

    internal static TemporalAgentSession Deserialize(JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        if (!serializedSession.TryGetProperty("sessionId", out JsonElement sessionIdElement) ||
            sessionIdElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Invalid or missing sessionId property.");
        }

        string sessionIdString = sessionIdElement.GetString() ?? throw new JsonException("sessionId property is null.");
        TemporalAgentSessionId sessionId = TemporalAgentSessionId.Parse(sessionIdString);
        AgentSessionStateBag stateBag = serializedSession.TryGetProperty("stateBag", out JsonElement stateBagElement)
            ? AgentSessionStateBag.Deserialize(stateBagElement)
            : new AgentSessionStateBag();

        return new TemporalAgentSession(sessionId, stateBag);
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(TemporalAgentSessionId))
        {
            return this.SessionId;
        }

        return base.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public override string ToString() => this.SessionId.WorkflowId;
}
