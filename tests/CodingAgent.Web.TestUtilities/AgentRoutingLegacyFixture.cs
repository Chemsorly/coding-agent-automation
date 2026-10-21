using System.Text.Json;
using System.Text.Json.Nodes;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.TestUtilities;

/// <summary>
/// One label-routing configuration as it was stored before spec 053, for the configuration move's tests.
/// <see cref="ProfileJson"/> holds the profiles as stored JSON, so the fixture outlives the old record members.
/// </summary>
public sealed record LegacyRoutingScenario(
    string Name,
    string? DefaultRequiredAgentLabels,
    IReadOnlyList<string> ProfileJson,
    IReadOnlyList<ProviderConfig> ProviderConfigs,
    PipelineProject Project,
    IReadOnlyList<PipelineJobTemplate> Templates,
    IReadOnlyList<QualityGateConfiguration> QualityGates,
    IReadOnlyList<ReviewerConfiguration> Reviewers,
    IReadOnlyDictionary<string, string> ToolsByKey);

/// <summary>
/// Spec 053 req 7.14: the fixture of the move's equivalence test. Two scenarios, because some cases need the global
/// <c>DefaultRequiredAgentLabels</c> and one needs it unset. Together they hold:
/// a polyglot profile; a variant profile with an extra label (whose <c>JobTemplate</c> differs from its labels, so an
/// operator triage claims another profile); a template without labels using the default labels; a label-less
/// template without default labels, where implementation and consolidation differ; a template with no matching
/// profile; a disabled template; a profile without a provider config; a profile with no labels and no job template;
/// a disabled profile; two profiles sharing one provider config; duplicate profile names; an <c>auto</c> model; a
/// blank model; a tool label that a reviewer config names; a job template without <c>providerType</c>; a profile
/// whose job template does not exist (which the Scheduler's unfiltered consolidation picked).
/// </summary>
public static class AgentRoutingLegacyFixture
{
    // ── Ids (GUIDs: the stores key profiles, providers and templates by GUID) ──

    public const string ProjectId = "00000000-0000-0000-0000-00000000a001";

    public const string KiroProfile = "00000000-0000-0000-0000-000000000101";
    public const string PolyglotProfile = "00000000-0000-0000-0000-000000000102";
    public const string KiroOpusProfile = "00000000-0000-0000-0000-000000000103";
    public const string ClaudeProfile = "00000000-0000-0000-0000-000000000104";
    public const string ClaudeExtraProfile = "00000000-0000-0000-0000-000000000105";
    public const string OpenCodeProfile = "00000000-0000-0000-0000-000000000106";
    public const string NoConfigProfile = "00000000-0000-0000-0000-000000000107";
    public const string NoToolProfile = "00000000-0000-0000-0000-000000000108";
    public const string UnlabelledProfile = "00000000-0000-0000-0000-000000000109";
    public const string OrphanProfile = "00000000-0000-0000-0000-000000000110";
    public const string DisabledProfile = "00000000-0000-0000-0000-000000000111";

    public const string KiroConfig = "00000000-0000-0000-0000-000000000201";
    public const string KiroAutoConfig = "00000000-0000-0000-0000-000000000202";
    public const string KiroOpusConfig = "00000000-0000-0000-0000-000000000203";
    public const string ClaudeConfig = "00000000-0000-0000-0000-000000000204";
    public const string OpenCodeConfig = "00000000-0000-0000-0000-000000000205";
    public const string MissingConfig = "00000000-0000-0000-0000-0000000002ff";

    /// <summary>Profiles the move disables: no provider config, no job template key, or a template without a tool.</summary>
    public static IReadOnlySet<string> ProfilesMovedDisabled { get; } =
        new HashSet<string>(StringComparer.Ordinal) { NoConfigProfile, NoToolProfile, UnlabelledProfile };

    /// <summary>The template whose operator triage claims another profile than its dispatch picks (req 7.3).</summary>
    public const string VariantTemplateName = "opus";

    public static LegacyRoutingScenario WithDefaultLabels { get; } = Build("with-default-labels", "dotnet,kiro", includeUnlabelled: true);

    public static LegacyRoutingScenario WithoutDefaultLabels { get; } = Build("without-default-labels", null, includeUnlabelled: false);

    public static IReadOnlyList<LegacyRoutingScenario> Scenarios { get; } = [WithDefaultLabels, WithoutDefaultLabels];

    /// <summary>Writes <paramref name="scenario"/> to <paramref name="db"/> as the pre-053 stores stored it, and saves.</summary>
    public static async Task SeedAsync(PipelineDbContext db, LegacyRoutingScenario scenario, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scenario);

        // The move does not read projects; the row only gives the templates an owner.
        db.Projects.Add(new ProjectEntity { Id = Guid.Parse(scenario.Project.Id), Name = scenario.Project.Name, Enabled = true });
        foreach (var json in scenario.ProfileJson)
        {
            var node = JsonNode.Parse(json)!;
            db.AgentProfiles.Add(new AgentProfileEntity
            {
                Id = Guid.Parse(node["id"]!.GetValue<string>()), Name = node["displayName"]!.GetValue<string>(), Configuration = json,
            });
        }
        foreach (var config in scenario.ProviderConfigs)
        {
            db.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = Guid.Parse(config.Id), Kind = config.Kind, DisplayName = config.DisplayName,
                ProviderType = config.ProviderType, Enabled = true, Configuration = Json(config),
            });
        }
        foreach (var template in scenario.Templates)
        {
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = Guid.Parse(template.Id), ProjectId = Guid.Parse(ProjectId), Name = template.Name, Configuration = Json(template),
            });
        }
        foreach (var gate in scenario.QualityGates)
            db.QualityGateConfigs.Add(new QualityGateConfigEntity { Id = Guid.Parse(gate.Id), Name = gate.DisplayName, Configuration = Json(gate) });
        foreach (var reviewer in scenario.Reviewers)
            db.ReviewerConfigs.Add(new ReviewerConfigEntity { Id = Guid.Parse(reviewer.Id), Name = reviewer.DisplayName, Configuration = Json(reviewer) });

        var pipeline = JsonNode.Parse(Json(new PipelineConfiguration()))!.AsObject();
        pipeline["defaultRequiredAgentLabels"] = scenario.DefaultRequiredAgentLabels;
        db.PipelineConfig.Add(new PipelineConfigEntity { Id = Guid.NewGuid(), Configuration = pipeline.ToJsonString() });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>A profile as the pre-053 store wrote it.</summary>
    public static string LegacyProfileJson(
        string id,
        string displayName,
        IReadOnlyList<string> matchLabels,
        string agentProviderConfigId,
        int priority = 0,
        bool enabled = true,
        string? jobTemplate = null,
        IReadOnlyList<McpServerConfig>? mcpServers = null)
    {
        var node = new JsonObject
        {
            ["id"] = id,
            ["displayName"] = displayName,
            ["matchLabels"] = new JsonArray(matchLabels.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
            ["agentProviderConfigId"] = agentProviderConfigId,
            ["enabled"] = enabled,
            ["priority"] = priority,
            ["mcpServers"] = JsonNode.Parse(Json(mcpServers ?? [])),
            ["jobTemplate"] = jobTemplate,
        };
        return node.ToJsonString(PipelineJsonOptions.Default);
    }

    private static LegacyRoutingScenario Build(string name, string? defaultLabels, bool includeUnlabelled)
    {
        var grafana = new McpServerConfig
        {
            Name = "grafana", Type = "http", Url = "https://grafana.example.test/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer made-up-token" },
        };

        var profiles = new List<string>
        {
            LegacyProfileJson(KiroProfile, "Kiro", ["dotnet", "kiro"], KiroConfig),
            LegacyProfileJson(PolyglotProfile, "Polyglot", ["dotnet", "kiro", "python"], KiroAutoConfig),
            LegacyProfileJson(KiroOpusProfile, "Kiro Opus", ["dotnet", "kiro", "opus"], KiroOpusConfig, jobTemplate: "dotnet, kiro"),
            LegacyProfileJson(ClaudeProfile, "Claude", ["claude", "dotnet"], ClaudeConfig, priority: 10, mcpServers: [grafana]),
            // Duplicate name, and the same provider config as the profile above.
            LegacyProfileJson(ClaudeExtraProfile, "Claude", ["claude", "dotnet", "extra"], ClaudeConfig, priority: 20),
            LegacyProfileJson(OpenCodeProfile, "OpenCode", ["dotnet", "opencode"], OpenCodeConfig),
            LegacyProfileJson(NoConfigProfile, "Rust", ["kiro", "rust"], MissingConfig),
            LegacyProfileJson(NoToolProfile, "Go", ["go", "kiro"], KiroConfig),
            // Highest enabled priority, but its job template does not exist: the Scheduler's unfiltered consolidation took it.
            LegacyProfileJson(OrphanProfile, "Legacy", ["dotnet", "kiro", "legacy"], KiroConfig, priority: 30),
            LegacyProfileJson(DisabledProfile, "Old Kiro", ["dotnet", "kiro"], KiroConfig, priority: 50, enabled: false),
        };
        // No labels and no job template: it covers only a label-less request, so it is left out where one exists.
        if (includeUnlabelled)
            profiles.Add(LegacyProfileJson(UnlabelledProfile, "Unlabelled", [], KiroConfig));

        IReadOnlyList<ProviderConfig> providers =
        [
            Agent(KiroConfig, "Kiro (Sonnet)", ProviderTypes.KiroCli, new()
            {
                [ProviderSettingKeys.Model] = "claude-sonnet-4-6", [ProviderSettingKeys.Effort] = "high",
                [ProviderSettingKeys.AgentName] = "dev",
            }),
            Agent(KiroAutoConfig, "Kiro (auto)", ProviderTypes.KiroCli, new()
            {
                [ProviderSettingKeys.Model] = "auto", [ProviderSettingKeys.Effort] = "",
            }),
            Agent(KiroOpusConfig, "Kiro (Opus)", ProviderTypes.KiroCli, new()
            {
                [ProviderSettingKeys.Model] = "claude-opus-4-8", [ProviderSettingKeys.Effort] = "max",
            }),
            Agent(ClaudeConfig, "Claude Code", ProviderTypes.ClaudeCode, new()
            {
                [ProviderSettingKeys.Model] = "opus", [ProviderSettingKeys.Effort] = "xhigh",
                [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription,
                [ProviderSettingKeys.McpConfigPath] = "/home/ubuntu/.claude/custom-mcp.json",
                ["legacyFlag"] = "1",
            }),
            Agent(OpenCodeConfig, "OpenCode", ProviderTypes.OpenCode, new()
            {
                [ProviderSettingKeys.Model] = "", [ProviderSettingKeys.Effort] = "turbo",
                [ProviderSettingKeys.BaseUrl] = "http://opencode.example.test:4096",
            }),
            .. Enumerable.Range(1, 10).Select(Repository),
        ];

        IReadOnlyList<PipelineJobTemplate> templates =
        [
            Template(1, "api", ["dotnet", "kiro"]),
            Template(2, "ml", ["kiro", "python"]),
            Template(3, VariantTemplateName, ["dotnet", "kiro", "opus"]),
            Template(4, "web", ["claude", "dotnet"]),
            Template(5, "tools", ["dotnet", "opencode"]),
            Template(6, "legacy-java", ["java"]),
            Template(7, "nolabels", []),
            Template(8, "rust", ["kiro", "rust"]),
            Template(9, "go", ["go", "kiro"]),
            Template(10, "archive", ["claude", "dotnet"], enabled: false),
        ];

        IReadOnlyList<QualityGateConfiguration> gates =
        [
            Gate("00000000-0000-0000-0000-000000000401", "dotnet", ["dotnet"]),
            Gate("00000000-0000-0000-0000-000000000402", "python", ["python"]),
            Gate("00000000-0000-0000-0000-000000000403", "always", []),
            Gate("00000000-0000-0000-0000-000000000404", "opus checks", ["opus"]),
        ];

        IReadOnlyList<ReviewerConfiguration> reviewers =
        [
            // Names a tool label, so "kiro" stays a stack label after the move.
            new() { Id = "00000000-0000-0000-0000-000000000501", DisplayName = "Kiro reviewers", MatchLabels = ["kiro"], Agents = [] },
            new() { Id = "00000000-0000-0000-0000-000000000502", DisplayName = "Everyone", MatchLabels = [], Agents = [] },
        ];

        var tools = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dotnet,kiro"] = "kiro",
            ["dotnet,kiro,python"] = "kiro",
            ["claude,dotnet"] = "claude",
            ["claude,dotnet,extra"] = "claude",
            ["dotnet,opencode"] = "opencode",
            ["kiro,rust"] = "kiro",
            ["go,kiro"] = "",
        };

        var project = new PipelineProject
        {
            Id = ProjectId,
            Name = "Shop",
            McpServers = [new McpServerConfig { Name = "db", Type = "stdio", Command = "db-mcp" }],
        };

        return new LegacyRoutingScenario(name, defaultLabels, profiles, providers, project, templates, gates, reviewers, tools);
    }

    private static ProviderConfig Agent(string id, string displayName, string providerType, Dictionary<string, string> settings) => new()
    {
        Id = id, DisplayName = displayName, Kind = ProviderKind.Agent, ProviderType = providerType, Settings = settings,
    };

    private static ProviderConfig Repository(int n) => new()
    {
        Id = RepositoryId(n), DisplayName = $"repo-{n}", Kind = ProviderKind.Repository, ProviderType = ProviderTypes.GitHub,
        Settings = new Dictionary<string, string> { [ProviderSettingKeys.Owner] = "example", [ProviderSettingKeys.Repo] = $"repo-{n}" },
    };

    private static string RepositoryId(int n) => $"00000000-0000-0000-0000-000000000{300 + n}";

    private static PipelineJobTemplate Template(int n, string name, IReadOnlyList<string> labels, bool enabled = true) => new()
    {
        Id = $"00000000-0000-0000-0000-000000000{600 + n}",
        Name = name,
        IssueProviderId = $"00000000-0000-0000-0000-000000000{700 + n}",
        RepoProviderId = RepositoryId(n),
        RequiredLabels = labels.Count > 0 ? labels : null,
        Enabled = enabled,
    };

    private static QualityGateConfiguration Gate(string id, string name, IReadOnlyList<string> labels) => new()
    {
        Id = id, DisplayName = name, MatchLabels = labels,
    };

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, PipelineJsonOptions.Default);
}
