using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodingAgentWebUI;

/// <summary>
/// Kubernetes-style health probe endpoints for the orchestrator.
/// <list type="bullet">
///   <item><c>/healthz</c> — Liveness probe. Returns 200 if the process is running.
///     Never checks external dependencies. Failure triggers pod restart.</item>
///   <item><c>/readyz</c> — Readiness probe. Returns 200 if ready to accept traffic,
///     503 during graceful shutdown drain or database connectivity loss.</item>
/// </list>
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Maps <c>/healthz</c> and <c>/readyz</c> probe endpoints for the orchestrator.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: Is the process alive? Never check dependencies here.
        endpoints.MapGet("/healthz", () =>
        {
            return Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
        }).AllowAnonymous();

        // Readiness: Can this pod serve traffic?
        // 503 during graceful shutdown drain or database unreachable.
        endpoints.MapGet("/readyz", (HttpContext httpContext) =>
        {
            var readiness = httpContext.RequestServices.GetRequiredService<ReadinessState>();
            var dbHealth = httpContext.RequestServices.GetService<DatabaseHealthState>();

            if (!readiness.IsReady)
                return Results.Json(new { status = "draining", timestamp = DateTime.UtcNow }, statusCode: 503);
            if (dbHealth is not null && !dbHealth.IsDatabaseHealthy)
                return Results.Json(new { status = "unhealthy", reason = "database_unreachable", timestamp = DateTime.UtcNow }, statusCode: 503);

            return Results.Ok(new { status = "ready", timestamp = DateTime.UtcNow });
        }).AllowAnonymous();

        return endpoints;
    }
}
