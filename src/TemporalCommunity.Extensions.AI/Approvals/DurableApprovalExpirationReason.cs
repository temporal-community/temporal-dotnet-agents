namespace TemporalCommunity.Extensions.AI.Approvals;

/// <summary>Why an approval wait ended without an explicit reviewer decision.</summary>
public enum DurableApprovalExpirationReason
{
    /// <summary>The configured approval deadline elapsed.</summary>
    Timeout,

    /// <summary>The workflow-history reserve was reached and the wait was expired to permit progress.</summary>
    HistoryBudget,
}
