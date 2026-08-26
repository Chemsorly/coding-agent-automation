namespace CodingAgentWebUI.Api;

/// <summary>
/// Names of the authorization policies this host registers.
///
/// The two tiers are not interchangeable: <see cref="Operator"/> is the master key held by the
/// control plane (the Job Controller and the monolith), while <see cref="Agent"/> also admits the
/// per-pod derived keys. Picking the wrong one on an endpoint either locks out the control plane
/// or opens a control-plane route to every agent pod in the cluster, so the names are pinned here
/// rather than repeated as literals at each <c>RequireAuthorization</c> call.
/// </summary>
public static class ApiAuthPolicies
{
    /// <summary>Master key only — control-plane callers.</summary>
    public const string Operator = "OperatorApiKey";

    /// <summary>Master key or a per-agent derived key.</summary>
    public const string Agent = "AgentApiKey";
}
