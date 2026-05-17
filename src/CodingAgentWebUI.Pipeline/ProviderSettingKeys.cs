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

    /// <summary>Workspace-relative path for MCP server configuration.</summary>
    public const string McpConfigPath = "mcpConfigPath";

    // TODO: Add constants for "timeout" and "agentName" keys used in AgentProviderSection.razor (review finding - AcceptanceCriteria #2)

    // ── Token vending (written by orchestrator) ──────────────────────────

    // TODO: TokenValue duplicates Token (both resolve to "token") — consolidate into a single constant (review finding - DotNet #1)
    /// <summary>Vended token value (written to provider settings at runtime).</summary>
    public const string TokenValue = "token";

    /// <summary>Token expiration timestamp (written to provider settings at runtime).</summary>
    public const string TokenExpiresAt = "tokenExpiresAt";
}
