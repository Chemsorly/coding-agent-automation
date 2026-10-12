using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.5a: the agent list of the Fleet and Overview pages. The registry spans every project, so
/// it is filtered per D3 rule 5: a global reader sees every agent, and a project-scoped user sees the
/// agents running work of their projects. An idle agent belongs to no project, so only global readers
/// see it. The credential pool is global infrastructure and needs global read.
/// </summary>
internal static class UiAgentEndpoints
{
    public static RouteGroupBuilder MapUiAgentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/agents", GetAgents);
        group.MapGet("/agents/credential-pool", AgentEndpoints.GetCredentialPool).RequireAuthorization(UiPolicies.GlobalRead);
        return group;
    }

    /// <summary>GET /api/ui/agents — the agents the user can see, enriched from their active runs.</summary>
    internal static IResult GetAgents(IUserAccess access, IAgentRegistryService registry, IOrchestratorRunService runService) =>
        TypedResults.Ok(AgentEndpoints.ListAgents(registry, runService)
            .Where(a => access.CanRead(a.ProjectId))
            .Select(a => a.Agent)
            .ToList());
}
