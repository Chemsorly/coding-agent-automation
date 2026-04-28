using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Minimal health check endpoints for Kubernetes/Docker probes.
/// No Blazor, no MVC — just raw endpoint routing.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Maps <c>/health</c> (liveness) and <c>/ready</c> (readiness) endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Liveness probe: 200 OK if the process is running
        endpoints.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

        // Readiness probe: 200 OK if connected to orchestrator, 503 if disconnected
        endpoints.MapGet("/ready", (AgentWorkerService workerService) =>
        {
            if (workerService.IsConnected)
                return Results.Ok(new { status = "ready", connected = true });

            return Results.Json(
                new { status = "not_ready", connected = false },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return endpoints;
    }
}
