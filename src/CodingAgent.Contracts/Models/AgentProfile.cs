using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// How an agent runs (spec 053): the Helm job template it runs on, which sets the tool, the tool's settings and the MCP
/// servers. Which profile, model and effort run a task type is the routing table's choice (<see cref="AgentRouting"/>).
/// </summary>
[MessagePackObject]
public sealed record AgentProfile
{
    [Key(0)]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    [Key(1)]
    public required string DisplayName { get; init; }

    // Key(2) retired — MatchLabels removed (spec 053: routes pick profiles; the configuration move reads old rows as raw JSON). Do NOT reuse this Key index
    // Key(3) retired — AgentProviderConfigId removed (spec 053: the agent config is built from the profile and its route). Do NOT reuse this Key index

    [Key(4)]
    public bool Enabled { get; init; } = true;

    // Key(5) retired — Priority removed (spec 053: nothing picks among profiles any more). Do NOT reuse this Key index

    [Key(6)]
    public IReadOnlyList<McpServerConfig> McpServers { get; init; } = [];

    /// <summary>
    /// Key of the Kubernetes job template (the <c>labels</c> of a Helm <c>jobTemplates[]</c> entry) that this profile's
    /// jobs run on. Its <c>providerType</c> is the profile's tool. Required by validation, not by the <c>required</c>
    /// keyword, so the configuration move can read rows from before spec 053.
    /// </summary>
    [Key(7)]
    public string? JobTemplate { get; init; }

    /// <summary>
    /// Tool settings (spec 053 req 1.1): the <see cref="ProviderSettingKeys"/> of the job template's tool, such as
    /// <c>agentName</c> (Kiro), <c>baseUrl</c> (OpenCode), <c>authMode</c> (Claude Code) and <c>mcpConfigPath</c>.
    /// Never a model or an effort: the route names those. Null in a save body means the form is out of date; readers
    /// treat null as empty.
    /// </summary>
    [Key(8)]
    public IReadOnlyDictionary<string, string>? Settings { get; init; }

    /// <summary>
    /// The normalized key of <see cref="JobTemplate"/>: split on <c>,</c>, trimmed, empty entries removed and sorted
    /// ordinally (<see cref="AgentSelectorKey.From"/>), the same result as <c>JobTemplateStore.NormalizeLabels</c>, which
    /// Contracts cannot call. Empty when no job template is set. This is the profile's agent selector; dispatch code
    /// calls this method rather than normalizing <see cref="JobTemplate"/> itself.
    /// </summary>
    public string JobTemplateKey() =>
        string.IsNullOrWhiteSpace(JobTemplate)
            ? string.Empty
            : AgentSelectorKey.From(JobTemplate
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => !string.IsNullOrWhiteSpace(p)));
}
