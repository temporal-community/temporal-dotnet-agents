// AuditInterceptor — demonstrates IDurableToolInterceptor<DurableToolContext>.
//
// This interceptor uses the base-library interface (Temporalio.Extensions.AI) rather
// than IAgentToolInterceptor (Temporalio.Extensions.Agents) because it only needs
// the core context fields (ToolName, Arguments) and is not MAF-specific. Any
// IDurableToolInterceptor<DurableToolContext> implementation also works transparently
// inside MAF agent sessions due to the interface's 'in' variance annotation.
//
// Decision paths demonstrated:
//   Block          — delete_file for a protected file (system.lock, kernel.sys)
//   PauseForApproval — delete_file for any other file (irreversible; human must review)
//   Proceed        — everything else (read_file and unknown tools)
//                    Proceed carries an "audit" metadata tag to show the metadata parameter.

using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Tools;

/// <summary>
/// Pre-tool interceptor that enforces a policy on file operations:
/// protected files are blocked outright; any other deletion requires human approval;
/// read operations proceed immediately with an audit tag in metadata.
/// </summary>
internal sealed class AuditInterceptor : IDurableToolInterceptor<DurableToolContext>
{
    // Files that may never be deleted, regardless of approval.
    private static readonly HashSet<string> ProtectedFiles =
        new(StringComparer.OrdinalIgnoreCase) { "system.lock", "kernel.sys" };

    private const string DeleteTool = "delete_file";

    /// <inheritdoc />
    public Task<DurableToolDecision> BeforeToolCallAsync(
        DurableToolContext context,
        CancellationToken cancellationToken)
    {
        // Non-delete tools proceed immediately with an audit tag in metadata.
        if (!context.ToolName.Equals(DeleteTool, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(DurableToolDecision.Proceed(
                metadata: new Dictionary<string, string>
                {
                    ["audit"] = "read-allowed",
                    ["tool"]  = context.ToolName,
                }));
        }

        // Extract the file name from the tool arguments supplied by the LLM.
        var name = context.Arguments?.TryGetValue("name", out var n) == true
            ? n?.ToString()
            : null;

        if (string.IsNullOrEmpty(name))
            return Task.FromResult(DurableToolDecision.Block("No file name provided to delete_file."));

        // Protected files: hard block — no approval path possible.
        if (ProtectedFiles.Contains(name))
        {
            return Task.FromResult(DurableToolDecision.Block(
                $"'{name}' is a protected system file and cannot be deleted."));
        }

        // All other delete requests: pause for human approval with an enriched description.
        // The description is forwarded to DurableApprovalRequest.Description so the reviewer
        // sees meaningful context when querying GetPendingApprovalAsync.
        return Task.FromResult(DurableToolDecision.PauseForApproval(
            $"Delete file '{name}' — this operation is irreversible.",
            metadata: new Dictionary<string, string>
            {
                ["audit"] = "delete-pending-approval",
                ["file"]  = name,
            }));
    }
}
