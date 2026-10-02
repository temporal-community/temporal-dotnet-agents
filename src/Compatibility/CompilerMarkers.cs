#if NETSTANDARD2_1
// Metadata contracts only: these types do not add framework runtime APIs.
// Embedded prevents imported copies (including friend assemblies) from
// competing with each assembly's own down-level compiler definitions.
using Microsoft.CodeAnalysis;

namespace Microsoft.CodeAnalysis
{
    [Embedded]
    [System.Runtime.CompilerServices.CompilerGenerated]
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class EmbeddedAttribute : Attribute;
}

namespace System.Runtime.CompilerServices
{
    [Embedded]
    internal static class IsExternalInit;

    [Embedded]
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct |
        AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute;

    [Embedded]
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
        public string FeatureName { get; } = featureName;
        public bool IsOptional { get; init; }
    }

    [Embedded]
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute
    {
        public string ParameterName { get; } = parameterName;
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [Embedded]
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute;

    [Embedded]
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Module |
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum |
        AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property |
        AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Interface |
        AttributeTargets.Delegate, Inherited = false)]
    internal sealed class ExperimentalAttribute(string diagnosticId) : Attribute
    {
        public string DiagnosticId { get; } = diagnosticId;
        public string? UrlFormat { get; set; }
        public string? Message { get; set; }
    }
}
#endif
