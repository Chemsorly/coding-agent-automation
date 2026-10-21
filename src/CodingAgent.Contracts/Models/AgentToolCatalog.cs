namespace CodingAgent.Pipeline.Models;

/// <summary>
/// One agent CLI an agent profile can run, given by its job template's <c>providerType</c> (spec 053 D16).
/// </summary>
/// <param name="Id">The job template's <c>providerType</c>: <c>kiro</c>, <c>opencode</c> or <c>claude</c>.</param>
/// <param name="DisplayName">The tool's name in the UI.</param>
/// <param name="ProviderConfigType">The <see cref="ProviderConfig.ProviderType"/> of the agent config the agent receives.</param>
/// <param name="Efforts">The efforts the tool accepts besides <c>auto</c>; empty when the tool has no effort.</param>
/// <param name="Settings">The tool settings the Agent Profiles form shows (<see cref="ProviderSettingKeys"/>).</param>
/// <param name="BuiltInModels">Models known without asking the tool; empty when there is no such list.</param>
/// <param name="DefaultMcpConfigPath">Where the agent writes the MCP servers when the profile sets no path.</param>
public sealed record AgentTool(
    string Id,
    string DisplayName,
    string ProviderConfigType,
    IReadOnlyList<string> Efforts,
    IReadOnlyList<string> Settings,
    IReadOnlyList<string> BuiltInModels,
    string DefaultMcpConfigPath);

/// <summary>
/// What each agent tool accepts and needs. Replaces the per-tool checks of the former agent provider page.
/// </summary>
public static class AgentToolCatalog
{
    /// <summary>The model or effort value that leaves the choice to the tool.</summary>
    public const string Auto = "auto";

    public static readonly AgentTool Kiro = new(
        JobTemplateProviderType.Kiro,
        "Kiro",
        ProviderTypes.KiroCli,
        // kiro-cli has no xhigh; it would run it at high (KiroCliAgentProvider.ToKiroEffort), so it is not offered.
        Efforts: ["low", "medium", "high", "max"],
        Settings: [ProviderSettingKeys.AgentName],
        BuiltInModels: [],
        DefaultMcpConfigPath: "/home/ubuntu/.kiro/settings/mcp.json");

    public static readonly AgentTool OpenCode = new(
        JobTemplateProviderType.Opencode,
        "OpenCode",
        ProviderTypes.OpenCode,
        Efforts: [],
        Settings: [ProviderSettingKeys.BaseUrl],
        BuiltInModels: [],
        DefaultMcpConfigPath: "/home/ubuntu/.opencode/opencode.json");

    public static readonly AgentTool ClaudeCode = new(
        JobTemplateProviderType.Claude,
        "Claude Code",
        ProviderTypes.ClaudeCode,
        Efforts: ["low", "medium", "high", "xhigh", "max"],
        Settings: [ProviderSettingKeys.AuthMode],
        BuiltInModels: ClaudeCodeModels.All.Select(m => m.ModelId).ToArray(),
        DefaultMcpConfigPath: AgentDefaults.ClaudeMcpConfigPath);

    public static IReadOnlyList<AgentTool> All { get; } = [Kiro, OpenCode, ClaudeCode];

    /// <summary>
    /// The tool a job template's <c>providerType</c> names, or null when it is empty or unknown. The ConfigMap renders
    /// an unset <c>providerType</c> as an empty string. The provider config type (<c>KiroCli</c>, …) is accepted too,
    /// as <see cref="JobTemplateProviderType.MatchesProviderConfigType"/> does. Case-insensitive.
    /// </summary>
    public static AgentTool? Find(string? providerType)
    {
        if (string.IsNullOrWhiteSpace(providerType))
            return null;
        var value = providerType.Trim();
        return All.FirstOrDefault(t =>
            t.Id.Equals(value, StringComparison.OrdinalIgnoreCase)
            || t.ProviderConfigType.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="effort"/> is <c>auto</c> or an effort <paramref name="tool"/> accepts.</summary>
    public static bool AcceptsEffort(AgentTool tool, string? effort)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return string.Equals(effort, Auto, StringComparison.Ordinal)
            || tool.Efforts.Contains(effort ?? "", StringComparer.Ordinal);
    }

    /// <summary>Every effort value any tool accepts, <c>auto</c> first. Used where the tool is not known.</summary>
    public static IReadOnlyList<string> AllEfforts { get; } = [Auto, "low", "medium", "high", "xhigh", "max"];
}
