namespace CodingAgentWebUI.Agent;

/// <summary>
/// Resolved startup configuration for the agent worker.
/// Extracted from Program.cs to reduce top-level statement complexity.
/// </summary>
internal sealed record AgentStartupConfig
{
    public required string AgentApiKey { get; init; }
    public required string OrchestratorUrl { get; init; }
    public required string AgentId { get; init; }
    public required string? WorkItemId { get; init; }
    public required bool IsK8sMode { get; init; }

    internal static async Task<AgentStartupConfig> ResolveAsync(string[] args)
    {
        var workItemId = args
            .FirstOrDefault(a => a.StartsWith(AgentDefaults.CliWorkItemIdPrefix, StringComparison.OrdinalIgnoreCase))
            ?.Substring(AgentDefaults.CliWorkItemIdPrefix.Length);
        var isK8sMode = workItemId is not null;

        // Read API key: from file (K8s mode) or env var (SignalR mode)
        string agentApiKey;
        var apiKeyFilePath = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKeyFile);
        if (!string.IsNullOrEmpty(apiKeyFilePath))
        {
            agentApiKey = (await File.ReadAllTextAsync(apiKeyFilePath)).Trim();
        }
        else
        {
            agentApiKey = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey)
                ?? throw new InvalidOperationException(
                    $"Neither {AgentDefaults.EnvAgentApiKeyFile} nor {AgentDefaults.EnvAgentApiKey} is set. " +
                    "Provide --work-item-id={{id}} with AGENT_API_KEY_FILE, or AGENT_API_KEY for SignalR mode.");
        }

        var orchestratorUrl = Environment.GetEnvironmentVariable(AgentDefaults.EnvOrchestratorUrl)
            ?? throw new InvalidOperationException("ORCHESTRATOR_URL environment variable is required");
        var agentId = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentId)
            ?? Environment.MachineName;

        // TODO: Dead code — the `string.IsNullOrEmpty(orchestratorUrl)` check can never be true here
        // because the null-coalescing throw on line above already throws when orchestratorUrl is null,
        // and an empty string would also have been caught there. This was pre-existing in Program.cs.
        if (!isK8sMode && string.IsNullOrEmpty(orchestratorUrl))
        {
            throw new InvalidOperationException(
                "Agent startup mode cannot be determined. Provide --work-item-id={id} for K8s mode, " +
                "or set ORCHESTRATOR_URL + AGENT_API_KEY for SignalR mode.");
        }

        return new AgentStartupConfig
        {
            AgentApiKey = agentApiKey,
            OrchestratorUrl = orchestratorUrl,
            AgentId = agentId,
            WorkItemId = workItemId,
            IsK8sMode = isK8sMode
        };
    }
}
