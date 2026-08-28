using Temporalio.Common;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Opt-in search attribute configuration for <see cref="DurableChatWorkflow"/>.
/// When set on <see cref="DurableChatWorkflowInput"/>, the workflow upserts
/// <c>TurnCount</c> and <c>SessionCreatedAt</c> as typed search attributes,
/// enabling session filtering in the Temporal UI and via workflow list APIs.
/// </summary>
/// <remarks>
/// Search attributes must be pre-registered with the Temporal server before use.
/// Use the Temporal CLI: <c>temporal operator search-attribute create</c>.
/// </remarks>
public sealed class DurableSessionAttributes
{
    /// <summary>
    /// Search attribute key for the number of completed turns in a session.
    /// Registered as a <c>Long</c> search attribute named <c>"TurnCount"</c>.
    /// </summary>
    public static readonly SearchAttributeKey<long> TurnCount =
        SearchAttributeKey.CreateLong("TurnCount");

    /// <summary>
    /// Search attribute key for the time at which the session was created.
    /// Registered as a <c>Datetime</c> search attribute named <c>"SessionCreatedAt"</c>.
    /// </summary>
    public static readonly SearchAttributeKey<DateTimeOffset> SessionCreatedAt =
        SearchAttributeKey.CreateDateTimeOffset("SessionCreatedAt");
}
