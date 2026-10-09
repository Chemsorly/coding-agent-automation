namespace CodingAgent.Pipeline;

/// <summary>
/// Default values and constants for agent worker configuration.
/// </summary>
public static class AgentDefaults
{
    // ── Chat constants ──────────────────────────────────────────────────

    /// <summary>Default workspace path for chat sessions inside agent containers.</summary>
    public const string ChatWorkspacePath = "/app/workspaces/chat";

    /// <summary>
    /// Root directory for per-window chat session workspaces.
    /// Each chat window gets a subdirectory: <see cref="ChatWorkspacesRoot"/>/{chatWindowId}.
    /// Distinct from <see cref="ChatWorkspacePath"/> (the legacy single-path constant retained for backward compatibility).
    /// </summary>
    public const string ChatWorkspacesRoot = "/app/workspaces/chat-sessions";

    /// <summary>Warm-up prompt sent to establish a KiroCli chat session before the real prompt.</summary>
    public const string ChatWarmUpPrompt = "hello, how are you?";

    // ── CLI paths ────────────────────────────────────────────────────────

    /// <summary>Default filesystem path to the Kiro CLI executable inside agent containers.</summary>
    public const string KiroCliPath = "/home/ubuntu/.local/bin/kiro-cli";

    /// <summary>Default filesystem path to the Claude Code CLI executable inside agent containers.</summary>
    public const string ClaudeCliPath = "/home/ubuntu/.local/bin/claude";

    /// <summary>
    /// Default MCP config file for Claude Code, outside the workspace so secrets never reach a commit.
    /// Passed to the CLI with <c>--mcp-config</c>.
    /// </summary>
    public const string ClaudeMcpConfigPath = "/home/ubuntu/.claude/pipeline-mcp.json";

    /// <summary>Provider type value of a claude job template (<c>jobTemplates[].providerType</c>).</summary>
    public const string ClaudeTemplateProviderType = "claude";

    /// <summary>Default base URL for the OpenCode agent HTTP API.</summary>
    public const string OpenCodeBaseUrl = "http://127.0.0.1:4096"; // NOSONAR S1075 — the single definition of this default endpoint

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

    /// <summary>Agent provider type of a chat pod: the job template's providerType ("kiro", "opencode", "claude").</summary>
    public const string EnvAgentProviderType = "AGENT_PROVIDER_TYPE";

    /// <summary>Override path for the Kiro CLI executable.</summary>
    public const string EnvKiroCliPath = "KIRO_CLI_PATH";

    /// <summary>Override path for the Claude Code CLI executable.</summary>
    public const string EnvClaudeCliPath = "CLAUDE_CLI_PATH";

    /// <summary>
    /// Anthropic API key for the Claude Code CLI, injected into claude agent pods from the chart
    /// Secret's <c>claude-api-key</c>. Deliberately not named ANTHROPIC_API_KEY: the agent hands it
    /// only to the claude process, so quality gates and other child processes never see it.
    /// </summary>
    public const string EnvClaudeApiKey = "AGENT_CLAUDE_API_KEY";

    /// <summary>
    /// Subscription token (<c>claude setup-token</c>) for the Claude Code CLI, injected from the chart
    /// Secret's <c>claude-oauth-token</c>. Handed to the claude process as CLAUDE_CODE_OAUTH_TOKEN.
    /// </summary>
    public const string EnvClaudeOAuthToken = "AGENT_CLAUDE_OAUTH_TOKEN";

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

    /// <summary>
    /// When "true" (case-insensitive), the agent pod runs in chat-only mode:
    /// no job assignment, no heartbeat loop. Set by ChatJobDispatcher env var injection.
    /// </summary>
    public const string EnvChatMode = "AGENT_CHAT_MODE";

    /// <summary>
    /// The dispatch GUID for the current chat session. Set by ChatJobDispatcher env var injection.
    /// Used by AgentConnectionLifecycle to add the matching label so the dispatcher can find the agent.
    /// </summary>
    public const string EnvChatSessionId = "AGENT_CHAT_SESSION_ID";

    /// <summary>
    /// Model name to apply to the Kiro CLI chat session (e.g., "claude-opus-4.8").
    /// Injected by ChatJobDispatcher post-build. Absent/auto → no override.
    /// </summary>
    public const string EnvChatModel = "AGENT_CHAT_MODEL";

    /// <summary>
    /// Effort level to apply to the Kiro CLI chat session (e.g., "high", "low").
    /// Injected by ChatJobDispatcher post-build. Absent/auto → no override.
    /// </summary>
    public const string EnvChatEffort = "AGENT_CHAT_EFFORT";

    // ── CLI arguments ────────────────────────────────────────────────────

    /// <summary>CLI argument prefix for work item ID (K8s mode).</summary>
    public const string CliWorkItemIdPrefix = "--work-item-id=";

    /// <summary>
    /// CLI argument emitted on chat pod specs (Spec 044 Req C5.1a).
    /// Phase 2 (Spec 044 Task 15b.2) makes <c>--mode</c> mandatory;
    /// this constant ensures both emitters use the same literal.
    /// </summary>
    public const string CliModeChat = "--mode=chat";
}
