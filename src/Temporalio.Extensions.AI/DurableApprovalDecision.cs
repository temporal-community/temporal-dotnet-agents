using System.Text.Json.Serialization;

namespace Temporalio.Extensions.AI;

/// <summary>
/// A human's decision on a pending tool approval request.
/// </summary>
public sealed class DurableApprovalDecision
{
    /// <summary>
    /// The request ID this decision applies to.
    /// Must match <see cref="DurableApprovalRequest.RequestId"/>.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Whether the tool call is approved.
    /// </summary>
    public bool Approved { get; init; }

    /// <summary>
    /// Optional reason for approval or rejection.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// When <see cref="Approved"/> is <see langword="true"/>, controls how far this decision
    /// carries forward. Defaults to <see cref="ApprovalScope.ThisCallOnly"/>, which reproduces
    /// today's per-invocation behavior.
    /// </summary>
    /// <remarks>
    /// <c>ApprovalScope</c> is defined in <c>Temporalio.Extensions.AI</c> because this DTO is
    /// the shared approval wire contract. The MEAI workflow path currently ignores scope fields;
    /// the Agents workflow path applies them.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApprovalScope Scope { get; init; } = ApprovalScope.ThisCallOnly;

    /// <summary>
    /// Optional pattern that scopes the decision to a subset of tool calls sharing the same
    /// name. When <see langword="null"/>, the decision applies to any call of the named tool
    /// regardless of arguments. When non-null, only tool calls whose arguments match the
    /// pattern are auto-approved; others still require human review.
    /// </summary>
    /// <remarks>
    /// Serialized as a JSON object: <c>{ "type": "Glob", "parameter": "path", "pattern": "/tmp/*" }</c>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApprovalScopePattern? ScopePattern { get; init; }
}
