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
/// <remarks>
/// <para><b>Probe semantics and failure conditions:</b></para>
/// <list type="bullet">
///   <item><b>Liveness (<c>/healthz</c>)</b>: Always returns 200 OK. This probe intentionally
///     never checks external dependencies (database, orchestrator, etc.). If the process can
///     serve HTTP, it is alive. Returning unhealthy here causes Kubernetes to restart the pod,
///     which is only appropriate for unrecoverable states (deadlocks, corrupted memory).
///     Unhealthy condition: process crash (probe unreachable).</item>
///   <item><b>Readiness (<c>/readyz</c>)</b>: Returns 503 Service Unavailable when
///     <see cref="AgentWorkerService.IsConnected"/> is <c>false</c> — i.e., the SignalR
///     connection to the orchestrator hub is not established or has been lost. While unready,
///     Kubernetes removes the pod from Service endpoints so no new jobs are dispatched to it.
///     The pod is NOT restarted; it remains running and can recover when the connection is
///     re-established.</item>
///   <item><b>Startup (<c>/startupz</c>)</b>: Returns 503 until <see cref="MarkStarted"/>
///     is called (after the host is fully built and listening). Until this probe succeeds,
///     Kubernetes disables liveness and readiness checks, preventing premature restarts during
///     slow startup (e.g., waiting for initial orchestrator connection, loading configuration).
///     Unhealthy condition: application still initializing.</item>
/// </list>
/// <para><b>Recommended K8s probe configuration:</b></para>
/// <code>
/// livenessProbe:
///   httpGet: { path: /healthz, port: 8080 }
///   initialDelaySeconds: 5
///   periodSeconds: 10
///   failureThreshold: 3
/// readinessProbe:
///   httpGet: { path: /readyz, port: 8080 }
///   periodSeconds: 5
///   failureThreshold: 2
/// startupProbe:
///   httpGet: { path: /startupz, port: 8080 }
///   initialDelaySeconds: 2
///   periodSeconds: 3
///   failureThreshold: 10
/// </code>
/// </remarks>
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
        endpoints.MapGet("/readyz", (IAgentService agentService) =>
        {
            if (agentService.IsConnected)
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
