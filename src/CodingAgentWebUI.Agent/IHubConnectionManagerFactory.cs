namespace CodingAgentWebUI.Agent;

/// <summary>
/// Abstraction over <see cref="HubConnectionManagerFactory"/> for testability.
/// </summary>
public interface IHubConnectionManagerFactory
{
    IHubConnectionManager Create();
}
