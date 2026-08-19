namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableToolsetValidationReasons
{
    internal const string UnknownToolset = "unknown_toolset";
    internal const string DuplicateSelection = "duplicate_selection";
    internal const string NameCollision = "name_collision";
    internal const string InvalidManifestVersion = "invalid_manifest_version";
    internal const string ManifestMismatch = "manifest_mismatch";
    internal const string AuthorityMismatch = "authority_mismatch";
    internal const string InvalidDeclaration = "invalid_declaration";
    internal const string InvalidPolicy = "invalid_policy";
}

internal sealed class DurableToolsetValidationException(
    string reason,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    internal string Reason { get; } = reason;
}

internal static class DurableToolsetValidation
{
    internal static string GetReason(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DurableToolsetValidationException validation)
            {
                return validation.Reason;
            }
        }

        return DurableToolsetValidationReasons.InvalidPolicy;
    }
}
