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
