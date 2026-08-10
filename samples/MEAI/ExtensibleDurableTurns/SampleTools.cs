using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;

namespace ExtensibleDurableTurns;

internal static class SampleTools
{
    public static string ReadReference(string reference) => $"reference:{reference}";

    public static string Apply(
        ProcessingRequest request,
        ProcessingState? state,
        string value) =>
        $"applied:{request.ResourceId}:{state?.Revision ?? 0}:{value}";

    public static ProcessingState Complete(
        DurableToolInvocationContext<ProcessingRequest, ProcessingState> context,
        string step,
        string value)
    {
        var previous = context.TurnState ?? new ProcessingState(0, false, []);
        return previous with
        {
            Revision = previous.Revision + 1,
            Receipts =
            [
                .. previous.Receipts,
                new ProcessingReceipt(step, value, context.Metadata.IdempotencyKey),
            ],
        };
    }
}

internal interface IAuthoritativeAuthorizationService
{
    ValueTask<bool> IsAllowedAsync(
        string subjectId,
        string resourceId,
        CancellationToken cancellationToken);
}

internal sealed class AuthoritativeAuthorizationService : IAuthoritativeAuthorizationService
{
    public ValueTask<bool> IsAllowedAsync(
        string subjectId,
        string resourceId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            string.Equals(subjectId, "trusted-user", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(resourceId));
}

internal sealed class IdempotentExternalSink
{
    private readonly HashSet<string> _recordedKeys = new(StringComparer.Ordinal);

    public bool Record(string idempotencyKey)
    {
        lock (_recordedKeys)
        {
            return _recordedKeys.Add(idempotencyKey);
        }
    }
}

internal sealed class AuthorizingFunction(
    AIFunction innerFunction,
    IAuthoritativeAuthorizationService authorization,
    string subjectId,
    string resourceId) : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Request/state locate the subject and resource; current permission comes from the
        // authoritative service immediately before the effect. A forged state flag is ignored.
        if (!await authorization.IsAllowedAsync(subjectId, resourceId, cancellationToken))
        {
            throw new UnauthorizedAccessException("The authoritative service denied this operation.");
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken);
    }
}
