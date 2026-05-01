using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Kubernetes-style health probe endpoints following best practices:
/// <list type="bullet">
///   <item><c>/healthz</c> — Liveness probe. Returns 200 if the process is running.
///     Never checks external dependencies. Failure triggers pod restart.</item>
///   <item><c>/readyz</c> — Readiness probe. Returns 200 if connected to orchestrator,
///     503 otherwise. Failure removes pod from Service endpoints (no traffic routed).</item>
///   <item><c>/startupz</c> — Startup probe. Returns 200 once the application has fully
///     started. Until this succeeds, liveness and readiness probes are disabled by Kubernetes.</item>
/// </list>
/// </summary>
public static class HealthEndpoints
{
    private static bool _started;

    /// <summary>
    /// Signals that the application has completed startup. Call after <c>app.RunAsync()</c>
    /// begins (i.e., after the host is fully built and listening).
    /// </summary>
    public static void MarkStarted() => _started = true;

    /// <summary>
    /// Maps <c>/healthz</c>, <c>/readyz</c>, and <c>/startupz</c> probe endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: Is the process alive? Never check dependencies here.
        // If this fails, Kubernetes restarts the container.
        endpoints.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

        // Readiness: Can this pod handle work? Check orchestrator connection.
        // If this fails, Kubernetes stops routing traffic but does NOT restart.
        endpoints.MapGet("/readyz", (AgentWorkerService workerService) =>
        {
            if (workerService.IsConnected)
                return Results.Ok(new { status = "ready", connected = true });

            return Results.Json(
                new { status = "not_ready", connected = false },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        // Startup: Has the app finished initializing? Used to gate liveness/readiness probes.
        // Prevents premature restarts during slow startup (e.g., waiting for first connection).
        endpoints.MapGet("/startupz", () =>
        {
            if (_started)
                return Results.Ok(new { status = "started" });

            return Results.Json(
                new { status = "starting" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return endpoints;
    }
}
