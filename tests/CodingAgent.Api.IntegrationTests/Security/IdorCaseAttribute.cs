namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Marks a test as the T3 (IDOR) case for a UI endpoint: it proves that a user with access to one
/// project cannot read or change another project's data through that endpoint. The endpoint is
/// written as in the T1 snapshot, <c>METHOD route</c>, e.g. <c>GET /api/ui/runs/{runId:guid}</c>.
/// <see cref="IdorCoverageTests"/> fails for an ID-taking endpoint without such a case or an exemption.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class IdorCaseAttribute(string endpoint) : Attribute
{
    public string Endpoint { get; } = endpoint;
}
