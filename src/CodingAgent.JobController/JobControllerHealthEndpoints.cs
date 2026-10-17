using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodingAgent.JobController;

/// <summary>
/// Kubernetes-style health probe endpoints for the Job Controller, the only endpoints it serves.
/// <list type="bullet">
///   <item><c>/healthz</c> — liveness: 200 while the process runs.</item>
///   <item><c>/readyz</c> — readiness: 200 for every replica regardless of leader state.</item>
/// </list>
/// </summary>
/// <remarks>
/// Leader election gates the reconciliation work inside <c>ReconciliationService</c>; the non-leader
/// idles and is still healthy. Returning 503 for non-leaders would keep the pod stuck at 0/1 Ready in
/// multi-replica deployments (strategy: RollingUpdate) and is incorrect: the non-leader is not
/// degraded, just standing by. <c>Program.cs</c> and the tests call the same method.
/// </remarks>
public static class JobControllerHealthEndpoints
{
    /// <summary>Maps <c>/healthz</c> and <c>/readyz</c>, both anonymous so the probes always reach them.</summary>
    public static IEndpointRouteBuilder MapJobControllerHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
        endpoints.MapGet("/readyz", () => Results.Ok(new { status = "ready" })).AllowAnonymous();
        return endpoints;
    }
}
