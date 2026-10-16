namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// The UI hub at <c>/hubs/ui</c> (spec 051 D7). A signed-in user's connection joins the group of each run
/// or chat it shows and receives that group's <see cref="IUiHubClient"/> events. These are its only methods.
/// </summary>
public interface IUiHub
{
    /// <summary>Joins the run's group. Refused unless the user can read the run's project.</summary>
    Task SubscribeToRun(string runId);

    Task UnsubscribeFromRun(string runId);

    /// <summary>Joins the group of the chat pod <paramref name="agentId"/>. Refused unless the user owns the chat.</summary>
    Task SubscribeToChat(string agentId);

    Task UnsubscribeFromChat(string agentId);
}
