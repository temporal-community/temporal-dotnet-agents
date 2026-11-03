// HARNESS for docs/how-to/MEAI/tool-functions.md § "Logical operation identity".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI;

namespace DocSnippets.ToolFunctions;

internal static class LogicalOperationIdentity
{
    internal static void Register(DurableToolsetBuilder toolset)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/tool-functions.md#logical-operation-identity
        var declaration = AIFunctionFactory.Create(
            (string recordId) => string.Empty,
            name: "write_record",
            description: "Writes a record.").AsDeclarationOnly();

        toolset.AddDurableToolFactory<WriteRequest, WriteState>(
            declaration,
            (services, context) =>
            {
                var sink = services.GetRequiredService<IIdempotentRecordSink>();
                return new DurableToolActivation<WriteState>
                {
                    Function = AIFunctionFactory.Create(
                        (string recordId) => sink.Write(
                            recordId,
                            context.Metadata.IdempotencyKey),
                        name: "write_record"),
                };
            },
            options => options.Idempotent().WithMaxAttempts(3));
        // END SNIPPET docs/how-to/MEAI/tool-functions.md#logical-operation-identity
    }
}

internal sealed record WriteRequest(string UserId);
internal sealed record WriteState(int Count);
internal interface IIdempotentRecordSink
{
    string Write(string recordId, string idempotencyKey);
}
