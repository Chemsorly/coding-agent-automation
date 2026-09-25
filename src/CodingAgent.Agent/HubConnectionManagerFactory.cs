using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent;

/// <summary>
/// Factory for creating new <see cref="HubConnectionManager"/> instances.
/// Used by <see cref="AgentWorkerService"/> to rebuild the SignalR connection from scratch
/// after the connection enters the terminal Closed state (e.g., orchestrator restart).
/// </summary>
public sealed class HubConnectionManagerFactory : IHubConnectionManagerFactory
{
    private readonly string _orchestratorUrl;
    private readonly AgentId _agentId;
    private readonly string _apiKey;
    private readonly Serilog.ILogger _logger;

    /// <summary>
    /// Initialises the factory.
    /// </summary>
    /// <param name="orchestratorUrl">SignalR hub base URL.</param>
    /// <param name="agentId">The agent identity (= K8s job name).</param>
    /// <param name="apiKey">
    /// The authentication key to present as the bearer token.
    /// <list type="bullet">
    ///   <item><term>Work-item pods</term><description>
    ///     Pass the pre-vended per-job key (<c>HMAC-SHA256(master, jobName)</c>) received in
    ///     <c>AGENT_API_KEY</c>. Set <paramref name="isWorkItemMode"/> to <see langword="true"/>
    ///     so the factory passes it verbatim — no local derivation.
    ///   </description></item>
    ///   <item><term>Non-work-item pods (chat, model-fetch, consolidation)</term><description>
    ///     Pass the raw master key (read from <c>AGENT_API_KEY_FILE</c> or <c>AGENT_API_KEY</c>).
    ///     Set <paramref name="isWorkItemMode"/> to <see langword="false"/> so the factory derives
    ///     <c>HMAC-SHA256(masterKey, agentId)</c> before handing the connection to SignalR — matching
    ///     what <c>AgentApiKeyAuthHandler</c> expects on the server side.
    ///   </description></item>
    /// </list>
    /// </param>
    /// <param name="isWorkItemMode">
    /// <see langword="true"/> when the pod was started in work-item mode (the key is already derived);
    /// <see langword="false"/> for chat/non-work-item mode (the key must be derived here).
    /// </param>
    /// <param name="logger">Serilog logger.</param>
    public HubConnectionManagerFactory(string orchestratorUrl, AgentId agentId, string apiKey, bool isWorkItemMode, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(orchestratorUrl);
        ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId));
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestratorUrl = orchestratorUrl;
        _agentId = agentId;
        // Work-item pods receive a pre-vended key (HMAC already computed server-side by
        // DispatchLifecycleService) — present it verbatim. Non-work-item pods (chat, model-fetch,
        // consolidation) receive the raw master key via AGENT_API_KEY_FILE and must derive the
        // per-agent key locally so the token matches what AgentApiKeyAuthHandler re-derives
        // server-side from HMAC(master, ?agentId=).
        _apiKey = isWorkItemMode ? apiKey : HubConnectionManager.DeriveKey(apiKey, agentId.Value);
        _logger = logger;
    }

    /// <summary>
    /// Creates a new <see cref="HubConnectionManager"/> instance with the same configuration.
    /// </summary>
    public IHubConnectionManager Create() => new HubConnectionManager(_orchestratorUrl, _agentId, _apiKey, _logger);
}
