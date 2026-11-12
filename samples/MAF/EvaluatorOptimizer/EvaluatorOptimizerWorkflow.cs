using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Workflows;

namespace EvaluatorOptimizer;

/// <summary>
/// Orchestrating workflow that implements the Evaluator-Optimizer pattern:
/// a Generator agent iteratively produces drafts, while an Evaluator agent
/// reviews them and either approves or provides actionable feedback for revision.
/// </summary>
/// <remarks>
/// Both agents run as durable Temporal activities, so:
/// <list type="bullet">
///   <item>Each generation/evaluation turn is independently retried on failure.</item>
///   <item>If the worker crashes mid-loop, the workflow resumes from the last completed turn.</item>
///   <item>The full revision history is preserved in the workflow event history.</item>
/// </list>
/// </remarks>
[Workflow("EvaluatorOptimizer.EvaluatorOptimizerWorkflow")]
public class EvaluatorOptimizerWorkflow
{
    private static readonly ActivityOptions AgentActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(35),
        ScheduleToCloseTimeout = TimeSpan.FromHours(2),
        RetryPolicy = new()
        {
            MaximumAttempts = 3,
            NonRetryableErrorTypes =
            [
                nameof(DurableTurnFailedException),
                nameof(DurableTurnOutcomeTooLargeException),
            ],
        },
    };

    private const string ApprovalToken = "APPROVED";

    /// <summary>
    /// Runs the evaluator-optimizer loop for a given <paramref name="task"/>.
    /// </summary>
    /// <param name="task">The writing or generation task description.</param>
    /// <param name="maxIterations">
    /// Maximum number of generator+evaluator cycles before returning the best draft found.
    /// Defaults to 3.
    /// </param>
    /// <returns>The final approved (or best) draft.</returns>
    [WorkflowRun]
    public async Task<string> RunAsync(string task, int maxIterations = 3)
    {
        var genSession = WorkflowAgents.NewAgentSessionId("Generator");
        var evalSession = WorkflowAgents.NewAgentSessionId("Evaluator");

        var draft = string.Empty;
        var feedback = string.Empty;

        for (int i = 0; i < maxIterations; i++)
        {
            // ── Generation turn ────────────────────────────────────────────
            // Note: on revisions, the session history already contains the previous
            // draft as the assistant's last response. The explicit "Previous draft:"
            // re-injection below is intentional for pedagogical clarity — it makes
            // the full context visible in the prompt without requiring the reader to
            // infer what the session carries.
            var genPrompt = i == 0
                ? task
                : $"Revise your previous draft based on this feedback:\n\n{feedback}\n\nPrevious draft:\n{draft}";

            draft = await Workflow.ExecuteActivityAsync(
                (AgentInvocationActivities activities) => activities.RunAsync(
                    "Generator", genSession.WorkflowId, Workflow.NewGuid().ToString("N"), genPrompt),
                AgentActivityOptions).ConfigureAwait(true);

            // ── Evaluation turn ────────────────────────────────────────────
            var evaluationPrompt =
                $"Evaluate the following draft. Reply with '{ApprovalToken}' if it is ready, " +
                $"or give concise, specific feedback for improvement.\n\n{draft}";
            feedback = await Workflow.ExecuteActivityAsync(
                (AgentInvocationActivities activities) => activities.RunAsync(
                    "Evaluator", evalSession.WorkflowId, Workflow.NewGuid().ToString("N"), evaluationPrompt),
                AgentActivityOptions).ConfigureAwait(true);

            if (feedback.Contains(ApprovalToken, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return draft;
    }
}
