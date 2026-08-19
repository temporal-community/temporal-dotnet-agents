using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// A <see cref="DelegatingAIFunction"/> that wraps tool calls as Temporal activities
/// when running inside a workflow, providing per-tool durability, retry, and timeout.
/// </summary>
public sealed class DurableAIFunction : DelegatingAIFunction
{
    private readonly DurableExecutionOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableAIFunction"/> class.
    /// </summary>
    /// <param name="innerFunction">The inner function to wrap.</param>
    /// <param name="options">
    /// Durable timeout and retry configuration. <see cref="DurableExecutionOptions.TaskQueue"/>
    /// is intentionally ignored: function activities run on the calling workflow's task queue.
    /// </param>
    public DurableAIFunction(AIFunction innerFunction, DurableExecutionOptions? options = null)
        : base(innerFunction)
    {
        _options = options ?? new DurableExecutionOptions();
    }

    /// <inheritdoc/>
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!Workflow.InWorkflow)
        {
            // Outside a workflow — pass through to inner function.
            return await base.InvokeCoreAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
        }

        // Inside a workflow — dispatch as a Temporal activity.
        var input = new DurableFunctionInput
        {
            FunctionName = Name,
            Arguments = ConvertArguments(arguments),
        };

        var activityOptions = CreateActivityOptions(Name, _options);

        // Keep this continuation on Temporal's workflow task scheduler. ConfigureAwait(false)
        // opts out of TaskScheduler.Current, so later workflow commands would no longer execute
        // through the active workflow context.
        var output = await Workflow.ExecuteActivityAsync(
            (DurableFunctionActivities a) => a.InvokeFunctionAsync(input),
            activityOptions);

        return output.Result;
    }

    /// <summary>
    /// Converts <see cref="AIFunctionArguments"/> to a serializable dictionary.
    /// </summary>
    private static Dictionary<string, object?>? ConvertArguments(AIFunctionArguments arguments)
    {
        return arguments.Count == 0 ? null : new Dictionary<string, object?>(arguments);
    }

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the function name; returns null when the name is missing so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(string? functionName) =>
        string.IsNullOrWhiteSpace(functionName) ? null : functionName;

    /// <summary>Creates activity options for a direct durable-function invocation.</summary>
    internal static ActivityOptions CreateActivityOptions(
        string? functionName,
        DurableExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Unlike direct chat and embedding adapters, AsDurable functions intentionally share the
        // calling workflow's task queue so the worker's AddDurableTools registry is colocated.
        var activityOptions = new ActivityOptions
        {
            StartToCloseTimeout = options.ActivityTimeout,
            RetryPolicy = Internal.DefaultRetryPolicy.ResolveForTool(options.RetryPolicy),
            Summary = BuildActivitySummary(functionName),
        };

        return activityOptions;
    }
}
