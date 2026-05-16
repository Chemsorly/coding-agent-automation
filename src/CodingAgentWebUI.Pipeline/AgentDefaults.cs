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

    // ── Named HttpClient ─────────────────────────────────────────────────

    /// <summary>Named HttpClient identifier for the OpenCode agent API.</summary>
    public const string OpenCodeHttpClientName = "OpenCode";

    // ── TimeSpan defaults ────────────────────────────────────────────────

    /// <summary>Default timeout for OpenCode HTTP requests (30 minutes).</summary>
    public static readonly TimeSpan OpenCodeRequestTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Default interval between agent heartbeats (30 seconds).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
}
