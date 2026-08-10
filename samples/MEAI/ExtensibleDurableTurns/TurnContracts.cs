namespace ExtensibleDurableTurns;

public sealed record ProcessingRequest(
    string OperationId,
    string SubjectId,
    string ResourceId);

public sealed record ProcessingReceipt(
    string Step,
    string Value,
    string ActivityIdempotencyKey);

public sealed record ProcessingState(
    int Revision,
    bool ClaimedAuthorized,
    IReadOnlyList<ProcessingReceipt> Receipts);
