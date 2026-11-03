using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableToolCallResolver
{
    internal static DurableToolCallResolution Resolve(
        IEnumerable<ChatMessage> priorMessages,
        IReadOnlyList<ChatMessage> responseMessages)
    {
        var responseContents = responseMessages
            .SelectMany(message => message.Contents)
            .ToList();
        var answeredCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in priorMessages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Concat(responseContents.OfType<FunctionResultContent>()))
        {
            answeredCallIds.Add(result.CallId);
        }

        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
        var actionableCalls = new List<FunctionCallContent>();
        foreach (var call in responseMessages
            .Where(message => message.Role == ChatRole.Assistant)
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>())
        {
            if (call.InformationalOnly || answeredCallIds.Contains(call.CallId))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(call.CallId)
                || string.IsNullOrWhiteSpace(call.Name)
                || call.Exception is not null)
            {
                return DurableToolCallResolution.Invalid;
            }

            if (!seenCallIds.Add(call.CallId))
            {
                return DurableToolCallResolution.DuplicateCallId;
            }

            actionableCalls.Add(call);
        }

        return new DurableToolCallResolution(actionableCalls, null);
    }
}

internal sealed record DurableToolCallResolution(
    IReadOnlyList<FunctionCallContent> ActionableCalls,
    string? Failure)
{
    internal static DurableToolCallResolution Invalid { get; } = new([], "invalid_call");

    internal static DurableToolCallResolution DuplicateCallId { get; } =
        new([], "duplicate_call_id");
}
