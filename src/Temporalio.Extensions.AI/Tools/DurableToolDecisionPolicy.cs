using Temporalio.Workflows;

namespace Temporalio.Extensions.AI.Tools;

/// <summary>
/// Shared policy helpers for the tool-interceptor dispatch loop.
/// Used by both the MEAI path (<c>DurableChatWorkflow</c>) and the MAF paths
/// (<c>AgentWorkflow</c>, <c>AgentJobWorkflow</c>, <c>TemporalAIAgent</c>).
/// All methods are pure / allocation-minimal and safe to call from workflow context.
/// </summary>
internal static class DurableToolDecisionPolicy
{
    /// <summary>
    /// Determines the effective outcome for a tool call.
    /// Rule 2: <c>RequireApproval</c> is an absolute floor — every non-Block outcome
    /// is upgraded to <see cref="DurableToolOutcome.PauseForApproval"/> when the tool
    /// is listed in <paramref name="requiresApprovalTools"/>. Block is never overridden.
    /// </summary>
    /// <param name="interceptorOutcome">
    /// The outcome returned by the interceptor activity, or <see langword="null"/> when
    /// no interceptor ran (defaults to <see cref="DurableToolOutcome.Proceed"/>).
    /// </param>
    /// <param name="toolName">The name of the tool being dispatched.</param>
    /// <param name="requiresApprovalTools">
    /// Optional set of tool names that require human approval regardless of interceptor outcome.
    /// Matched case-insensitively.
    /// </param>
    /// <returns>The effective <see cref="DurableToolOutcome"/> to act on.</returns>
    internal static DurableToolOutcome GetEffectiveOutcome(
        DurableToolOutcome? interceptorOutcome,
        string toolName,
        IReadOnlyList<string>? requiresApprovalTools)
    {
        var outcome = interceptorOutcome ?? DurableToolOutcome.Proceed;

        // RequireApproval floor: Block is strictly stricter and is always honoured as-is;
        // every other outcome (Proceed, Skip, PauseForApproval) is overridden (BLOCK-3 fix).
        var toolRequiresApproval = requiresApprovalTools is not null
            && requiresApprovalTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);

        if (toolRequiresApproval && outcome != DurableToolOutcome.Block)
        {
            outcome = DurableToolOutcome.PauseForApproval;
        }

        return outcome;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="toolName"/> is present in
    /// <paramref name="skippedTools"/> (case-insensitive OrdinalIgnoreCase).
    /// Returns <see langword="false"/> when <paramref name="skippedTools"/> is
    /// <see langword="null"/> or the tool is not listed.
    /// </summary>
    internal static bool IsToolSkipped(
        string toolName,
        IReadOnlyList<string>? skippedTools) =>
        skippedTools is not null
        && skippedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves <see cref="ActivityOptions"/> for the interceptor activity.
    /// When <paramref name="perToolOptions"/> contains an entry for <paramref name="toolName"/>,
    /// that entry is used as the base; otherwise <paramref name="sharedOptions"/> is the base.
    /// In both cases the returned options are a fresh instance with
    /// <c>Summary = $"intercept:{toolName}"</c> — the reference is never shared with the inputs.
    /// </summary>
    internal static ActivityOptions ResolveInterceptorActivityOptions(
        string toolName,
        ActivityOptions sharedOptions,
        IReadOnlyDictionary<string, ActivityOptions>? perToolOptions)
    {
        var baseOpts = perToolOptions is not null
            && perToolOptions.TryGetValue(toolName, out var toolSpecific)
                ? toolSpecific
                : sharedOptions;

        return new ActivityOptions
        {
            StartToCloseTimeout = baseOpts.StartToCloseTimeout,
            HeartbeatTimeout = baseOpts.HeartbeatTimeout,
            RetryPolicy = baseOpts.RetryPolicy,
            Summary = $"intercept:{toolName}",
        };
    }

    /// <summary>
    /// Returns the effective arguments for a tool dispatch.
    /// When the interceptor supplied modified arguments, those are used directly.
    /// Otherwise a fresh copy of the original arguments is returned.
    /// Returns <see langword="null"/> when both sources are <see langword="null"/>.
    /// </summary>
    /// <param name="interceptorModifiedArgs">
    /// Replacement arguments from the interceptor result, or <see langword="null"/>.
    /// </param>
    /// <param name="originalArgs">
    /// Original LLM-supplied arguments from the tool call, or <see langword="null"/>.
    /// </param>
    internal static Dictionary<string, object?>? GetEffectiveArguments(
        Dictionary<string, object?>? interceptorModifiedArgs,
        IReadOnlyDictionary<string, object?>? originalArgs)
    {
        if (interceptorModifiedArgs is not null)
        {
            return interceptorModifiedArgs;
        }

        return originalArgs is null ? null : new Dictionary<string, object?>(originalArgs);
    }

    /// <summary>
    /// Returns the human-readable description for a <see cref="DurableToolOutcome.PauseForApproval"/>
    /// request. Resolution order:
    /// <list type="number">
    ///   <item><see cref="DurableToolInterceptorResult.EnrichedDescription"/> (interceptor-supplied)</item>
    ///   <item><see cref="DurableToolInterceptorResult.Message"/> (interceptor fallback)</item>
    ///   <item>Default: <c>"Approve invocation of tool '{toolName}'"</c> (require-approval floor with no interceptor)</item>
    /// </list>
    /// </summary>
    /// <param name="result">
    /// The interceptor result, or <see langword="null"/> when the require-approval floor fired
    /// without any interceptor running.
    /// </param>
    /// <param name="toolName">Name of the tool, used for the default fallback string.</param>
    internal static string GetApprovalDescription(
        DurableToolInterceptorResult? result,
        string toolName) =>
        result?.EnrichedDescription
        ?? result?.Message
        ?? $"Approve invocation of tool '{toolName}'";

    /// <summary>
    /// Returns the synthetic result text injected as a <c>FunctionResultContent</c> when
    /// the interceptor outcome is <see cref="DurableToolOutcome.Skip"/>.
    /// Returns <see cref="string.Empty"/> when <paramref name="interceptorMessage"/> is
    /// <see langword="null"/>.
    /// </summary>
    internal static string SkipMessage(string? interceptorMessage) =>
        interceptorMessage ?? string.Empty;

    /// <summary>
    /// Returns the error result text injected as a <c>FunctionResultContent</c> when
    /// the interceptor outcome is <see cref="DurableToolOutcome.Block"/>.
    /// Falls back to <c>"Tool execution was blocked."</c> when
    /// <paramref name="interceptorMessage"/> is <see langword="null"/>.
    /// </summary>
    internal static string BlockMessage(string? interceptorMessage) =>
        $"[Blocked] {interceptorMessage ?? "Tool execution was blocked."}";

    /// <summary>
    /// Returns the error result text injected when a human reviewer denies the approval request.
    /// </summary>
    internal static string DenialMessage(string? denialReason) =>
        $"[Denied] {denialReason}";
}
