using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal enum DurableToolsetAuthorityKind
{
    None,
    CallerOwned,
    WorkerOwned,
}

internal static class DurableToolsetAuthority
{
    internal static DurableToolsetAuthorityKind Resolve(DurableChatWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var hasCallerDeclarations = input.ToolDeclarations is not null;
        var hasWorkerManifest = input.ToolsetManifest is not null;
        if (hasCallerDeclarations && hasWorkerManifest)
        {
            throw ConflictFailure();
        }

        if (hasWorkerManifest)
        {
            input.ToolsetManifest!.Validate();
            return DurableToolsetAuthorityKind.WorkerOwned;
        }

        if (hasCallerDeclarations)
        {
            ValidateCallerDeclarations(input.ToolDeclarations!);
            return DurableToolsetAuthorityKind.CallerOwned;
        }

        return DurableToolsetAuthorityKind.None;
    }

    internal static ApplicationFailureException ConflictFailure() =>
        DurableToolsetManifest.Failure(
            "Caller-owned tool declarations and a worker-owned toolset manifest cannot be combined.",
            DurableToolsetValidationReasons.AuthorityMismatch);

    private static void ValidateCallerDeclarations(
        IReadOnlyList<DurableFunctionDeclarationSnapshot> declarations)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            declaration.Validate();
            if (!names.Add(declaration.Name))
            {
                throw DurableToolsetManifest.Failure(
                    $"Caller-owned declarations contain more than one function named " +
                    $"'{declaration.Name}'.",
                    DurableToolsetValidationReasons.NameCollision);
            }
        }
    }
}
