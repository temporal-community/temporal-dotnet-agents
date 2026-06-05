namespace Temporalio.Extensions.AI;

/// <summary>
/// Controls how far an approval decision carries forward when a tool call is approved.
/// </summary>
/// <remarks>
/// Serialized as an integer for compactness and replay safety.
/// Do not add <c>JsonStringEnumConverter</c> — integer serialization is the wire contract.
/// </remarks>
public enum ApprovalScope
{
    /// <summary>
    /// Approve this specific invocation only (equivalent to today's per-invocation behavior).
    /// No scope record is written.
    /// </summary>
    ThisCallOnly = 0,

    /// <summary>
    /// Approve this tool/pattern for the remainder of the current session.
    /// Survives continue-as-new via StateBag. Expires when the session workflow terminates.
    /// </summary>
    Session = 1,

    /// <summary>
    /// Approve this tool/pattern for all future sessions.
    /// Stored in the agent's configured approval-scope store under a well-known key.
    /// For scope-aware tools, when approval-scope store mode is not enabled the scope degrades to
    /// <see cref="Session"/> with a warning logged when the decision is processed. For tools that
    /// are not scope-aware, reusable scopes are ignored and the decision behaves as
    /// <see cref="ThisCallOnly"/>.
    /// </summary>
    Always = 2,
}
