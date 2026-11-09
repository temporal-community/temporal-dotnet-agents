using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents;

internal static class AgentWorkflowWorkerFactory
{
    internal static WorkflowDefinition CreateDefinition(
        TemporalAgentsOptions options,
        string taskQueue)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskQueue);

        return WorkflowDefinition.Create(
            typeof(AgentWorkflow),
            "TemporalCommunity.Extensions.Agents.AgentWorkflow",
            args => CreateInstance(args, options, taskQueue));
    }

    internal static AgentWorkflow CreateInstance(
        object?[] args,
        TemporalAgentsOptions options,
        string taskQueue)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);
        if (args.Length != 1 || args[0] is not AgentWorkflowInput input)
        {
            throw new InvalidOperationException(
                "AgentWorkflow requires one AgentWorkflowInput start argument.");
        }

        try
        {
            var workerInput = input.ResolvedWorkerConfig is not null
                ? input
                : DefaultTemporalAgentClient.BuildWorkerAgentWorkflowInputCore(
                    input.AgentName,
                    options,
                    taskQueue);
            return new AgentWorkflow(workerInput);
        }
        catch (AgentNotRegisteredException exception)
        {
            // Creation failures bypass update validation and retry the workflow task. Defer
            // this configuration failure so the run fails and admission can reject it.
            return new AgentWorkflow(exception);
        }
    }
}
