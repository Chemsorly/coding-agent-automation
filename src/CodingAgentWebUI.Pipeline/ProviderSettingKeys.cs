namespace CodingAgentWebUI.Pipeline;

/// <summary>
/// Dictionary key names used in <c>ProviderConfig.Settings</c> dictionaries.
/// Centralizes these strings to prevent runtime KeyNotFoundException from typos.
/// </summary>
public static class ProviderSettingKeys
{
    // ── GitHub provider settings ─────────────────────────────────────────

    /// <summary>GitHub API base URL (e.g., "https://api.github.com").</summary>
    public const string ApiUrl = "apiUrl";

    /// <summary>GitHub App client ID.</summary>
    public const string ClientId = "clientId";

    /// <summary>GitHub App installation ID (numeric string).</summary>
    public const string InstallationId = "installationId";

    /// <summary>Base64-encoded private key PEM for GitHub App authentication.</summary>
    public const string PrivateKeyBase64 = "privateKeyBase64";

    /// <summary>Repository owner (user or organization).</summary>
    public const string Owner = "owner";

    /// <summary>Repository name.</summary>
    public const string Repo = "repo";

    /// <summary>Base branch name for the repository (e.g., "main").</summary>
    public const string BaseBranch = "baseBranch";

    /// <summary>Personal access token (used by consolidation executor).</summary>
    public const string Token = "token";

    // ── Agent provider settings ──────────────────────────────────────────

    /// <summary>Model name/identifier for the agent (e.g., "claude-sonnet-4").</summary>
    public const string Model = "model";

    /// <summary>Path to the agent CLI executable.</summary>
    public const string ExecutablePath = "executablePath";

    /// <summary>Base URL for HTTP-based agent providers (e.g., OpenCode).</summary>
    public const string BaseUrl = "baseUrl";

    /// <summary>Timeout in minutes for agent invocations.</summary>
    public const string Timeout = "timeout";

    /// <summary>Agent name identifier (e.g., "default").</summary>
    public const string AgentName = "agentName";

    /// <summary>Workspace-relative path for MCP server configuration.</summary>
    public const string McpConfigPath = "mcpConfigPath";

    // ── Label routing ────────────────────────────────────────────────────

    /// <summary>
    /// Optional key on Repository ProviderConfig. Comma-separated agent labels required
    /// to process issues from this repository (e.g., "kiro,dotnet").
    /// When absent, falls back to <see cref="Models.PipelineConfiguration.DefaultRequiredAgentLabels"/>,
    /// then to empty (any idle agent matches).
    /// </summary>
    public const string RequiredAgentLabels = "requiredAgentLabels";

    // ── Default values ───────────────────────────────────────────────────

    /// <summary>Default GitHub API URL.</summary>
    public const string DefaultApiUrl = "https://api.github.com";

    /// <summary>Default base branch name.</summary>
    public const string DefaultBaseBranch = "main";

    // ── GitLab provider settings ─────────────────────────────────────────

    /// <summary>GitLab access token (personal, project, or group).</summary>
    public const string AccessToken = "accessToken";

    /// <summary>GitLab numeric project identifier.</summary>
    public const string ProjectId = "projectId";

    /// <summary>Default GitLab API URL.</summary>
    public const string DefaultGitLabApiUrl = "https://gitlab.com";

    /// <summary>Username for GitLab HTTPS clone URL credentials.</summary>
    public const string GitLabTokenUsername = "oauth2";

    // ── Token vending (written by orchestrator) ──────────────────────────

    /// <summary>Token expiration timestamp (written to provider settings at runtime).</summary>
    public const string TokenExpiresAt = "tokenExpiresAt";
}
