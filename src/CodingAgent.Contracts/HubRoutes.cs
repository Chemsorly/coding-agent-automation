namespace CodingAgent.Pipeline;

/// <summary>
/// Centralized SignalR hub route constants.
/// </summary>
public static class HubRoutes
{
    /// <summary>
    /// The SignalR hub endpoint path for agent connections.
    /// </summary>
    public const string Agent = "/hubs/agent";

    /// <summary>The SignalR hub of the signed-in UI (spec 051 D7): live run output and chat replies.</summary>
    public const string Ui = "/hubs/ui";
}
