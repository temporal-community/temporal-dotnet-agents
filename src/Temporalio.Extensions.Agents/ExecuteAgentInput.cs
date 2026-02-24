// Copyright (c) Microsoft. All rights reserved.

using Temporalio.Extensions.Agents.State;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Input for the <see cref="AgentActivities.ExecuteAgentAsync"/> activity.
/// </summary>
internal sealed class ExecuteAgentInput
{
    public ExecuteAgentInput(string agentName, RunRequest request, IReadOnlyList<TemporalAgentStateEntry> conversationHistory)
    {
        AgentName = agentName;
        Request = request;
        ConversationHistory = conversationHistory;
    }

    /// <summary>Gets the name of the agent to invoke.</summary>
    public string AgentName { get; }

    /// <summary>Gets the run request (contains the new messages + options).</summary>
    public RunRequest Request { get; }

    /// <summary>
    /// Gets the full conversation history at the time of the activity call,
    /// including the new <see cref="TemporalAgentStateRequest"/> entry for this turn.
    /// </summary>
    public IReadOnlyList<TemporalAgentStateEntry> ConversationHistory { get; }
}
