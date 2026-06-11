namespace CodingAgentWebUI.Agent;

/// <summary>
/// Factory for creating new <see cref="HubConnectionManager"/> instances.
/// Used by <see cref="AgentWorkerService"/> to rebuild the SignalR connection from scratch
/// after the connection enters the terminal Closed state (e.g., orchestrator restart).
/// </summary>
public sealed class HubConnectionManagerFactory
{
    private readonly string _orchestratorUrl;
    private readonly string _agentId;
    private readonly string _apiKey;
    private readonly Serilog.ILogger _logger;

    public HubConnectionManagerFactory(string orchestratorUrl, string agentId, string apiKey, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(orchestratorUrl);
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestratorUrl = orchestratorUrl;
        _agentId = agentId;
        _apiKey = apiKey;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new <see cref="HubConnectionManager"/> instance with the same configuration.
    /// </summary>
    public HubConnectionManager Create() => new(_orchestratorUrl, _agentId, _apiKey, _logger);
}
