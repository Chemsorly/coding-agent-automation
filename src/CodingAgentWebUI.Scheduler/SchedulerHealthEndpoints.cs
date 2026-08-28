using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodingAgentWebUI.Scheduler;

/// <summary>
/// Kubernetes-style health probe endpoints for the Scheduler.
/// <list type="bullet">
///   <item><c>/healthz</c> — Startup and liveness probe. Returns 200 if the process is running.</item>
///   <item><c>/readyz</c> — Readiness probe. All scheduler replicas are considered ready
///     regardless of leader state; the non-leader idles but is healthy and should not be evicted.</item>
///   <item><c>/health</c> — Retained for backward compatibility with the Dockerfile HEALTHCHECK
///     (<c>CMD curl -f http://localhost:8080/health || exit 1</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// This class exists so that the endpoint registrations can be exercised directly in tests
/// without re-declaring the lambdas inline, which would test the routing infrastructure
/// rather than the production code. Tests call <see cref="MapSchedulerHealthEndpoints"/> on
/// an in-memory host; <c>Program.cs</c> calls the same method so both paths are identical.
/// </remarks>
public static class SchedulerHealthEndpoints
{
    /// <summary>
    /// Maps <c>/healthz</c>, <c>/readyz</c>, and <c>/health</c> probe endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapSchedulerHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: Is the process alive? Always 200 — never check external dependencies here.
        // TODO [WARNING]: Missing .AllowAnonymous() — if UseAuthorization() is ever added to the
        // application pipeline, Kubernetes liveness probes will receive 401/403 and the pod will
        // restart. Fix: append .AllowAnonymous() here to match the /health pattern below and
        // satisfy the issue requirement "Both endpoints must be reachable without authentication."
        endpoints.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

        // Readiness: All scheduler replicas are considered ready regardless of leader state.
        // The non-leader idles but is healthy and should not be evicted.
        // TODO [WARNING]: Missing .AllowAnonymous() — same risk as /healthz above. If authorization
        // middleware is added, Kubernetes readiness probes will receive 401/403. Fix: append
        // .AllowAnonymous() here.
        endpoints.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));

        // Backward compatibility: retained for Dockerfile HEALTHCHECK
        // (CMD curl -f http://localhost:8080/health || exit 1)
        endpoints.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
                 .WithName("SchedulerHealth").AllowAnonymous();

        return endpoints;
    }
}
