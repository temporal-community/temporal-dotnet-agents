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

internal sealed class ProcessingAttemptServices : IDisposable
{
    public ProcessingAttemptServices(
        IHttpClientFactory httpClientFactory,
        IAuthoritativeAuthorizationService authorization)
    {
        Client = httpClientFactory.CreateClient("processing-attempt");
        Authorization = authorization;
        InstanceId = Guid.NewGuid();
    }

    public HttpClient Client { get; }
    public IAuthoritativeAuthorizationService Authorization { get; }
    public Guid InstanceId { get; }

    public void Dispose() => Client.Dispose();
}

internal sealed record ExecutionAdapterObservation(
    string ToolName,
    int Attempt,
    Guid ScopeId,
    string Stage);

internal sealed class ExecutionAdapterAudit
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<ExecutionAdapterObservation> _entries = new();

    public IReadOnlyList<ExecutionAdapterObservation> Entries => _entries.ToArray();

    public void Record(string toolName, int attempt, Guid scopeId, string stage) =>
        _entries.Enqueue(new ExecutionAdapterObservation(toolName, attempt, scopeId, stage));
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
    string resourceId,
    string toolName,
    int attempt,
    Guid scopeId,
    ExecutionAdapterAudit audit) : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        audit.Record(toolName, attempt, scopeId, "before");
        try
        {
            // Request/state locate the subject and resource; current permission comes from the
            // authoritative service immediately before the effect. A forged state flag is ignored.
            if (!await authorization.IsAllowedAsync(subjectId, resourceId, cancellationToken))
            {
                audit.Record(toolName, attempt, scopeId, "denied");
                throw new UnauthorizedAccessException("The authoritative service denied this operation.");
            }

            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            audit.Record(toolName, attempt, scopeId, "success");
            return result;
        }
        catch
        {
            audit.Record(toolName, attempt, scopeId, "error");
            throw;
        }
        finally
        {
            audit.Record(toolName, attempt, scopeId, "finally");
        }
    }
}
