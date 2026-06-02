namespace Temporalio.Extensions.Agents.Internal;

/// <summary>
/// Compile-time constants shared across the library that cannot use <c>typeof()</c> because the
/// referenced types are <see langword="internal"/> or <see langword="internal sealed"/> in
/// upstream libraries.
/// </summary>
internal static class AgentInternalConstants
{
    /// <summary>
    /// Fully-qualified type name of MAF's internal function-invocation decorator.
    /// Hard-coded because the type is <see langword="internal sealed"/> in
    /// <c>Microsoft.Agents.AI</c> and not accessible via <c>typeof()</c>. If MAF ever renames
    /// this type, update this constant — both the C-check validator and the runtime B-check
    /// reference it via this single declaration.
    /// </summary>
    public const string FunctionInvocationDelegatingAgentFullName =
        "Microsoft.Agents.AI.FunctionInvocationDelegatingAgent";
}
