// TODO: Move this file to the CodingAgentWebUI.Agent project to match namespace convention (review finding #3)
namespace CodingAgentWebUI.Agent;

/// <summary>
/// Default values and constants for agent worker configuration.
/// </summary>
public static class AgentDefaults
{
    // ── CLI paths ────────────────────────────────────────────────────────

    /// <summary>Default filesystem path to the Kiro CLI executable inside agent containers.</summary>
    public const string KiroCliPath = "/home/ubuntu/.local/bin/kiro-cli";

    /// <summary>Default base URL for the OpenCode agent HTTP API.</summary>
    public const string OpenCodeBaseUrl = "http://127.0.0.1:4096";

    // TODO: Add timeout constants (OpenCodeRequestTimeout, HeartbeatInterval) per acceptance criteria (review finding - AcceptanceCriteria #4)

    // ── Named HttpClient ─────────────────────────────────────────────────

    /// <summary>Named HttpClient identifier for the OpenCode agent API.</summary>
    public const string OpenCodeHttpClientName = "OpenCode";

    // ── Environment variable names (delegated to AgentEnvironmentVariables) ─

    /// <summary>URL of the orchestrator's SignalR hub.</summary>
    public const string EnvOrchestratorUrl = AgentEnvironmentVariables.OrchestratorUrl;

    /// <summary>Shared secret for authenticating agent connections.</summary>
    public const string EnvAgentApiKey = AgentEnvironmentVariables.AgentApiKey;

    /// <summary>Unique identifier for this agent instance.</summary>
    public const string EnvAgentId = AgentEnvironmentVariables.AgentId;

    /// <summary>Agent type identifier (e.g., "kiro-dotnet10").</summary>
    public const string EnvAgentType = AgentEnvironmentVariables.AgentType;

    /// <summary>Comma-separated labels for agent routing.</summary>
    public const string EnvAgentLabels = AgentEnvironmentVariables.AgentLabels;

    /// <summary>Serilog log level override.</summary>
    public const string EnvLogLevel = AgentEnvironmentVariables.LogLevel;

    /// <summary>Agent provider type (e.g., "OpenCode", "KiroCli").</summary>
    public const string EnvAgentProviderType = AgentEnvironmentVariables.AgentProviderType;

    /// <summary>Override path for the Kiro CLI executable.</summary>
    public const string EnvKiroCliPath = AgentEnvironmentVariables.KiroCliPath;

    /// <summary>Override base URL for the OpenCode agent API.</summary>
    public const string EnvOpenCodeBaseUrl = AgentEnvironmentVariables.OpenCodeBaseUrl;

    /// <summary>Password for OpenCode server authentication.</summary>
    public const string EnvOpenCodeServerPassword = AgentEnvironmentVariables.OpenCodeServerPassword;
}
