using System.ComponentModel.DataAnnotations;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Startup/runtime configuration for the agent pod, bound from environment variables
/// via <c>services.AddOptions&lt;AgentRuntimeOptions&gt;().BindFromEnvironment()</c>.
///
/// Replaces scattered <c>Environment.GetEnvironmentVariable</c> calls for:
/// <c>AGENT_CHAT_MODE</c>, <c>AGENT_CHAT_SESSION_ID</c>, <c>AGENT_LABELS</c>,
/// <c>AGENT_CHAT_MODEL</c>, <c>AGENT_CHAT_EFFORT</c>, <c>AGENT_PROVIDER_TYPE</c>,
/// <c>OPENCODE_BASE_URL</c>, <c>OPENCODE_SERVER_PASSWORD</c>, <c>KIRO_CLI_PATH</c>.
///
/// T11 (arch-audit 2026-08-22): previously these values were read by multiple call sites
/// independently (<c>AGENT_CHAT_MODE</c> ×3, <c>AGENT_LABELS</c> ×2, etc.).
/// </summary>
public sealed class AgentRuntimeOptions
{
    /// <summary>Section name for IConfiguration binding (maps to env var prefix AGENT__ or direct env vars).</summary>
    public const string SectionName = "AgentRuntime";

    // ── Identity / labels ───────────────────────────────────────────────

    /// <summary>Comma-separated routing labels. Env: <c>AGENT_LABELS</c>.</summary>
    public string AgentLabels { get; set; } = "";

    // ── Chat mode flags ─────────────────────────────────────────────────

    /// <summary>When true, pod runs in chat-only mode. Env: <c>AGENT_CHAT_MODE</c>.</summary>
    public bool IsChatMode { get; set; }

    /// <summary>Unique chat session ID injected per-dispatch. Env: <c>AGENT_CHAT_SESSION_ID</c>.</summary>
    public string ChatSessionId { get; set; } = "";

    /// <summary>Model name override for Kiro CLI chat sessions. Env: <c>AGENT_CHAT_MODEL</c>.</summary>
    public string? ChatModel { get; set; }

    /// <summary>Effort level override for Kiro CLI chat sessions. Env: <c>AGENT_CHAT_EFFORT</c>.</summary>
    public string? ChatEffort { get; set; }

    // ── Provider selection ──────────────────────────────────────────────

    /// <summary>Agent backend type ("KiroCli" or "OpenCode"). Env: <c>AGENT_PROVIDER_TYPE</c>.</summary>
    public string AgentProviderType { get; set; } = "";

    /// <summary>Override base URL for the OpenCode API. Env: <c>OPENCODE_BASE_URL</c>.</summary>
    public string? OpenCodeBaseUrl { get; set; }

    /// <summary>Password for OpenCode server authentication. Env: <c>OPENCODE_SERVER_PASSWORD</c>.</summary>
    public string? OpenCodeServerPassword { get; set; }

    /// <summary>Override path for the Kiro CLI executable. Env: <c>KIRO_CLI_PATH</c>.</summary>
    public string? KiroCliPath { get; set; }
}
