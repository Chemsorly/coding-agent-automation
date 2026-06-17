namespace CodingAgentWebUI.Agent;

/// <summary>
/// Default values and constants for agent worker configuration.
/// </summary>
public static class AgentDefaults
{
    // ── Chat constants ──────────────────────────────────────────────────

    /// <summary>Default workspace path for chat sessions inside agent containers.</summary>
    public const string ChatWorkspacePath = "/app/workspaces/chat";

    /// <summary>Warm-up prompt sent to establish a KiroCli chat session before the real prompt.</summary>
    public const string ChatWarmUpPrompt = "hello, how are you?";

    // ── CLI paths ────────────────────────────────────────────────────────

    /// <summary>Default filesystem path to the Kiro CLI executable inside agent containers.</summary>
    public const string KiroCliPath = "/home/ubuntu/.local/bin/kiro-cli";

    /// <summary>Default base URL for the OpenCode agent HTTP API.</summary>
    public const string OpenCodeBaseUrl = "http://127.0.0.1:4096";

    // ── Named HttpClient ─────────────────────────────────────────────────

    /// <summary>Named HttpClient identifier for the OpenCode agent API.</summary>
    public const string OpenCodeHttpClientName = "OpenCode";

    // ── Environment variable names ───────────────────────────────────────

    /// <summary>URL of the orchestrator's SignalR hub.</summary>
    public const string EnvOrchestratorUrl = "ORCHESTRATOR_URL";

    /// <summary>Shared secret for authenticating agent connections.</summary>
    public const string EnvAgentApiKey = "AGENT_API_KEY";

    /// <summary>Unique identifier for this agent instance.</summary>
    public const string EnvAgentId = "AGENT_ID";

    /// <summary>Comma-separated labels for agent routing.</summary>
    public const string EnvAgentLabels = "AGENT_LABELS";

    /// <summary>Serilog log level override.</summary>
    public const string EnvLogLevel = "LOG_LEVEL";

    /// <summary>Agent provider type (e.g., "OpenCode", "KiroCli").</summary>
    public const string EnvAgentProviderType = "AGENT_PROVIDER_TYPE";

    /// <summary>Override path for the Kiro CLI executable.</summary>
    public const string EnvKiroCliPath = "KIRO_CLI_PATH";

    /// <summary>Override base URL for the OpenCode agent API.</summary>
    public const string EnvOpenCodeBaseUrl = "OPENCODE_BASE_URL";

    /// <summary>Password for OpenCode server authentication.</summary>
    public const string EnvOpenCodeServerPassword = "OPENCODE_SERVER_PASSWORD";

    /// <summary>Anthropic API key for LLM access.</summary>
    public const string EnvAnthropicApiKey = "ANTHROPIC_API_KEY";

    /// <summary>OpenAI API key for LLM access.</summary>
    public const string EnvOpenAiApiKey = "OPENAI_API_KEY";

    /// <summary>OpenRouter API key for LLM access.</summary>
    public const string EnvOpenRouterApiKey = "OPENROUTER_API_KEY";
}
