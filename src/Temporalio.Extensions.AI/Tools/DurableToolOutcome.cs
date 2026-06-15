namespace Temporalio.Extensions.AI.Tools;

/// <summary>
/// Outcome of a <c>RunToolInterceptor</c> activity, transported across the workflow/activity
/// boundary as the <c>Outcome</c> field on the internal result DTO.
/// </summary>
internal enum DurableToolOutcome
{
    /// <summary>Proceed with normal tool dispatch.</summary>
    Proceed = 0,

    /// <summary>Park the turn loop and wait for a human approval before dispatching.</summary>
    PauseForApproval = 1,

    /// <summary>Skip dispatch; inject a synthetic result.</summary>
    Skip = 2,

    /// <summary>Block dispatch; inject an error result.</summary>
    Block = 3,
}
