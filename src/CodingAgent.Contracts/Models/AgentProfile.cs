using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// A named configuration entity that maps a set of match labels to a specific agent provider config.
/// During job dispatch, the orchestrator resolves which profile matches the selected agent
/// and sends that profile's provider config.
/// </summary>
[MessagePackObject]
public sealed record AgentProfile
{
    [Key(0)]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    [Key(1)]
    public required string DisplayName { get; init; }

    [Key(2)]
    public IReadOnlyList<string> MatchLabels { get; init; } = [];

    /// <summary>
    /// The agent provider config of a profile from before spec 053. Not <c>required</c> any more, so a converted
    /// profile is written with an empty value; the configuration move treats a non-empty value as legacy data.
    /// </summary>
    [Key(3)]
    public string AgentProviderConfigId { get; init; } = "";

    [Key(4)]
    public bool Enabled { get; init; } = true;

    [Key(5)]
    public int Priority { get; init; } = 0;

    [Key(6)]
    public IReadOnlyList<McpServerConfig> McpServers { get; init; } = [];

    /// <summary>
    /// Key of the Kubernetes job template (the <c>labels</c> of a Helm <c>jobTemplates[]</c> entry)
    /// that this profile's jobs run on. Null or empty: the profile's <see cref="MatchLabels"/> are the key.
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
    /// Returns the canonical agent selector key for this profile. This is the single way to get a
    /// profile's agent selector — all dispatch code must call this method rather than computing the
    /// selector directly from <see cref="MatchLabels"/>.
    /// <para>
    /// When <see cref="JobTemplate"/> is not blank, the key is built from <see cref="JobTemplate"/>
    /// (split on <c>,</c>, trimmed, empty entries removed, then normalised by
    /// <see cref="AgentSelectorKey.From"/>). Otherwise the key is
    /// <c>AgentSelectorKey.From(<see cref="MatchLabels"/>)</c>.
    /// </para>
    /// </summary>
    public string JobTemplateKey()
    {
        if (!string.IsNullOrWhiteSpace(JobTemplate))
        {
            var parts = JobTemplate
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => !string.IsNullOrWhiteSpace(p));
            return AgentSelectorKey.From(parts);
        }

        return AgentSelectorKey.From(MatchLabels);
    }
}
