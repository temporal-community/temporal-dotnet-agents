namespace System.ClientModel;

/// <summary>
/// Test double for the OpenAI / Azure OpenAI <c>System.ClientModel.ClientResultException</c>.
/// <para>
/// <see cref="TemporalCommunity.Extensions.AI.Internal.LlmErrorClassifier"/> detects that provider
/// error path by exact type FullName (<c>"System.ClientModel.ClientResultException"</c>) plus a
/// reflected public <c>int Status</c> property — deliberately avoiding a hard package dependency on
/// <c>System.ClientModel</c> in the shipping library. The real package is NOT referenced by this
/// test project, so declaring a type with the same fully-qualified name here produces an exact
/// <c>GetType().FullName</c> match and exercises the classifier's reflection read path faithfully,
/// without needing to construct a real <c>ClientResultException</c> (whose constructor requires an
/// abstract <c>PipelineResponse</c>).
/// </para>
/// </summary>
internal sealed class ClientResultException(int status) : Exception("client result error")
{
    public int Status { get; } = status;
}
