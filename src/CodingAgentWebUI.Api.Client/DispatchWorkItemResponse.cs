namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Response DTO returned by <see cref="IPipelineApiWorkItemClient.DispatchAsync"/>.
/// The endpoint creates the K8s Job and transitions the WorkItem to Dispatched atomically.
/// </summary>
/// <param name="WorkItemId">The ID of the dispatched WorkItem.</param>
public record DispatchWorkItemResponse(Guid WorkItemId);
