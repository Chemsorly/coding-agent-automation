namespace CodingAgentWebUI.Agent;

/// <summary>
/// Environment variable names used by agent worker containers.
/// </summary>
public static class AgentEnvironmentVariables
{
    /// <summary>URL of the orchestrator's SignalR hub.</summary>
    public const string OrchestratorUrl = "ORCHESTRATOR_URL";

    /// <summary>Shared secret for authenticating agent connections.</summary>
    public const string AgentApiKey = "AGENT_API_KEY";

    /// <summary>Unique identifier for this agent instance.</summary>
    public const string AgentId = "AGENT_ID";

    /// <summary>Agent type identifier (e.g., "kiro-dotnet10").</summary>
    public const string AgentType = "AGENT_TYPE";

    /// <summary>Comma-separated labels for agent routing.</summary>
    public const string AgentLabels = "AGENT_LABELS";

    /// <summary>Serilog log level override.</summary>
    public const string LogLevel = "LOG_LEVEL";

    /// <summary>Agent provider type (e.g., "OpenCode", "KiroCli").</summary>
    public const string AgentProviderType = "AGENT_PROVIDER_TYPE";

    /// <summary>Override path for the Kiro CLI executable.</summary>
    public const string KiroCliPath = "KIRO_CLI_PATH";

    /// <summary>Override base URL for the OpenCode agent API.</summary>
    public const string OpenCodeBaseUrl = "OPENCODE_BASE_URL";

    /// <summary>Password for OpenCode server authentication.</summary>
    public const string OpenCodeServerPassword = "OPENCODE_SERVER_PASSWORD";
}
