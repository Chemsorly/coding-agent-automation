namespace CodingAgent.Api;

/// <summary>
/// Aggregator that maps all Work Item HTTP API endpoints by delegating to the three
/// per-concern endpoint files:
/// <list type="bullet">
///   <item><see cref="WorkItemAgentEndpoints"/> — agent-facing routes (assignment, status)</item>
///   <item><see cref="WorkItemDispatchEndpoints"/> — control-plane lifecycle routes (create, claim, dispatch, requeue, etc.)</item>
///   <item><see cref="WorkItemQueryEndpoints"/> — read-only metrics/query routes (pending, active, staleness, counts, etc.)</item>
/// </list>
/// </summary>
public static class WorkItemEndpoints
{
    /// <summary>
    /// Maps all work item endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapWorkItemAgentEndpoints();
        app.MapWorkItemDispatchEndpoints();
        app.MapWorkItemQueryEndpoints();
    }
}
