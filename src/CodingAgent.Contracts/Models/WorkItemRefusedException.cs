namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The API refused to create a work item (spec 053 req 2.9, DD2): no agent route resolves, or the route cannot run.
/// The message is the reason to show, such as "No agent route: set the default route in Settings → Agents → Routing".
/// Refusals are configuration errors, so retrying gives the same answer until the routing changes.
/// </summary>
public sealed class WorkItemRefusedException(string reason) : Exception(reason);
