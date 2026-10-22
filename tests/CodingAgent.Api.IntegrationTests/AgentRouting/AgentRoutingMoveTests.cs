using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Serilog.Events;
using Fixture = CodingAgent.Web.TestUtilities.AgentRoutingLegacyFixture;
using RoutingTable = CodingAgent.Pipeline.Models.AgentRouting;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>
/// Spec 053 req 7.1–7.14: <see cref="AgentRoutingMove"/> writes today's routing down so every template × task type
/// runs as before, compared with the golden files the pre-053 code wrote (task 3.2), not with the move's own copy of
/// that code (review-2 M4).
/// </summary>
public sealed class AgentRoutingMoveTests
{
    private readonly List<LogEvent> _logs = [];

    public static TheoryData<string> ScenarioNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var scenario in Fixture.Scenarios)
                data.Add(scenario.Name);
            return data;
        }
    }

    // ── Equivalence with the golden files (req 7.14) ─────────────────────────

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Move_RoutesEveryTemplateAndTaskTypeAsBefore_Req7_14(string scenarioName)
    {
        var scenario = Scenario(scenarioName);
        var (db, summary) = await MoveAsync(scenario);
        var moved = await LoadAsync(db);

        foreach (var golden in GoldenFiles.Read<GoldenCell>(GoldenFiles.TodayRoutes).Where(c => c.Scenario == scenarioName))
        {
            var because = $"{golden.Template} × {golden.TaskType}";
            var taskType = Enum.Parse<WorkItemTaskType>(golden.TaskType);
            var template = moved.Templates.Single(t => t.Name == golden.Template);
            var result = AgentRouteResolver.Resolve(
                taskType, RepositorySettings.TemplateFor(template.RepoProviderId, moved.Templates), moved.Routing,
                moved.Profiles, scenario.ToolsByKey);

            if (golden.ProfileId is null)
            {
                // Req 7.6: no profile ran this work; it now uses the global routes, which the move logged.
                result.Snapshot!.Source.Should().Be(AgentRouteSource.GlobalDefault, because);
                summary.TemplatesWithoutProfile.Should().Contain(golden.Template, because);
                continue;
            }
            if (Fixture.ProfilesMovedDisabled.Contains(golden.ProfileId))
            {
                // Req 7.2: its profile is moved disabled, so the work is refused with the reason (D15).
                result.RefusalReason.Should().Be(AgentRouteRefusalReason.ProfileUnavailable, because);
                continue;
            }

            var snapshot = result.Snapshot!;
            var profile = moved.Profiles.Single(p => p.Id == snapshot.ProfileId);
            snapshot.ProfileId.Should().Be(golden.ProfileId, because);
            profile.JobTemplateKey().Should().Be(golden.JobTemplateKey, because);
            snapshot.Model.Should().Be(NormalizedModel(golden.Model), because);
            snapshot.Effort.Should().Be(NormalizedEffort(golden.Effort), because);
            McpNames(profile, scenario.Project).Should().Equal(golden.McpServers, because);

            if (taskType == WorkItemTaskType.Consolidation)
                continue; // consolidation runs have no quality gates or reviewers
            var labels = template.RequiredLabels ?? [];
            GateIds(scenario, labels).Should().Equal(golden.QualityGates, because);
            ReviewerIds(scenario, labels).Should().Equal(golden.Reviewers, because);
        }
    }

    [Fact]
    public async Task Move_OperatorTriageOfTheVariantTemplate_IsTheOneLoggedQualityGateException_Req7_3()
    {
        var golden = GoldenFiles.Read<GoldenCell>(GoldenFiles.TodayRoutes)
            .Where(c => c.OperatorTriageClaimProfileId is not null && c.OperatorTriageClaimProfileId != c.ProfileId)
            .ToList();
        golden.Select(c => c.Template).Distinct().Should().Equal([Fixture.VariantTemplateName],
            "only the variant profile's template claims another profile at operator triage");

        await MoveAsync(Fixture.WithDefaultLabels);

        Warnings().Should().Contain(m => m.Contains("operator triage on template \"opus\"", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Move_AgentConfigsAreWhatAgentsReceivedBefore_Req4_7(string scenarioName)
    {
        var scenario = Scenario(scenarioName);
        var (db, _) = await MoveAsync(scenario);
        var moved = await LoadAsync(db);

        foreach (var golden in GoldenFiles.Read<GoldenAgentConfig>(GoldenFiles.Assignment).Where(c => c.Scenario == scenarioName))
        {
            if (golden.ProviderType is null)
                continue; // no agent ran this work before
            var because = $"{golden.Template} × {golden.TaskType}";
            var template = moved.Templates.Single(t => t.Name == golden.Template);
            var result = AgentRouteResolver.Resolve(
                Enum.Parse<WorkItemTaskType>(golden.TaskType), template, moved.Routing, moved.Profiles, scenario.ToolsByKey);
            if (result.IsRefused)
                continue; // a profile moved disabled; covered by the equivalence test
            var route = result.Snapshot!;

            var config = AgentProviderConfigFactory.Build(result.Profile!, route.Tool, route.Model, route.Effort);

            config.ProviderType.Should().Be(golden.ProviderType, because);
            Normalized(config.Settings).Should().Equal(Normalized(golden.Settings!), because);
        }
    }

    // ── What the move writes ─────────────────────────────────────────────────

    [Fact]
    public async Task Move_WritesTheMostCommonImplementationRouteAsTheDefault_AndRowsForTheRest_Req7_4_7_5()
    {
        var (db, summary) = await MoveAsync(Fixture.WithDefaultLabels);
        var moved = await LoadAsync(db);

        moved.Routing.Default.Should().Be(new AgentRoute(Fixture.KiroProfile, "claude-sonnet-4-6", "high"));
        moved.Routing.ByTaskType.Should().BeEmpty("the move creates no global task rows");
        moved.Templates.Single(t => t.Name == "api").AgentRoutes.Should().BeNull("api runs on the default route");
        moved.Templates.Single(t => t.Name == "ml").AgentRoutes.Should().HaveCount(5)
            .And.AllSatisfy(r => r.Value.Should().Be(new AgentRoute(Fixture.PolyglotProfile, "auto", "auto")));
        summary.DefaultRoute.Should().Be("Kiro · claude-sonnet-4-6 · high");
        summary.GlobalTaskRows.Should().Be(0);
    }

    [Fact]
    public async Task Move_KiroXHigh_IsRoutedAtHigh_TheEffortKiroRanIt_Req7_3()
    {
        // The agent provider form offered xhigh for Kiro, which kiro-cli has no value for and runs at high. A route at
        // xhigh would make every save of the Routing page fail (req 2.5), so the move writes what the agent ran.
        var scenario = Fixture.WithDefaultLabels with
        {
            ProviderConfigs = Fixture.WithDefaultLabels.ProviderConfigs
                .Select(c => c.Id != Fixture.KiroConfig ? c : new ProviderConfig
                {
                    Id = c.Id, DisplayName = c.DisplayName, Kind = c.Kind, ProviderType = c.ProviderType,
                    Settings = new Dictionary<string, string>(c.Settings) { [ProviderSettingKeys.Effort] = "xhigh" },
                })
                .ToList(),
        };

        var (db, _) = await MoveAsync(scenario);
        var moved = await LoadAsync(db);

        moved.Routing.Default.Should().Be(new AgentRoute(Fixture.KiroProfile, "claude-sonnet-4-6", "high"));
        AgentRouteValidator.ValidateRouting(moved.Routing, moved.Profiles, scenario.ToolsByKey).Should().BeEmpty(
            "the Routing page can save the moved table");
    }

    [Fact]
    public async Task Move_LabellessTemplateWithoutDefaultLabels_GetsARowForConsolidationOnly()
    {
        var (db, _) = await MoveAsync(Fixture.WithoutDefaultLabels);
        var moved = await LoadAsync(db);

        moved.Routing.Default.Should().Be(new AgentRoute(Fixture.ClaudeProfile, "opus", "xhigh"));
        moved.Templates.Single(t => t.Name == "nolabels").AgentRoutes.Should().Equal(
            new Dictionary<WorkItemTaskType, AgentRoute>
            {
                [WorkItemTaskType.Consolidation] = new(Fixture.ClaudeExtraProfile, "opus", "xhigh"),
            });
        Warnings().Should().Contain(m => m.Contains("automatic consolidation of template \"nolabels\"", StringComparison.Ordinal),
            "the Scheduler's unfiltered consolidation took the profile without a job template");
    }

    [Fact]
    public async Task Move_TemplateWithoutAProfile_GetsNoRows_AndIsLoggedByName_Req7_6()
    {
        var (db, summary) = await MoveAsync(Fixture.WithDefaultLabels);
        var moved = await LoadAsync(db);

        moved.Templates.Single(t => t.Name == "legacy-java").AgentRoutes.Should().BeNull();
        summary.TemplatesWithoutProfile.Should().Equal("legacy-java");
        Warnings().Should().Contain("Template \"legacy-java\" had no agent profile; it now uses \"Kiro · claude-sonnet-4-6 · high\"");
    }

    [Fact]
    public async Task Move_ConvertsProfiles_KeepingIdsSettingsAndMcpServers_Req7_2()
    {
        var (db, summary) = await MoveAsync(Fixture.WithDefaultLabels);
        var moved = await LoadAsync(db);

        var claude = moved.Profiles.Single(p => p.Id == Fixture.ClaudeProfile);
        claude.JobTemplate.Should().Be("claude,dotnet");
        claude.Settings.Should().Equal(new Dictionary<string, string>
        {
            [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription,
            [ProviderSettingKeys.McpConfigPath] = "/home/ubuntu/.claude/custom-mcp.json",
            ["legacyFlag"] = "1",
        }, "every setting but model and effort is kept, unknown keys too");
        claude.McpServers.Select(s => s.Name).Should().Equal("grafana");
        moved.Profiles.Single(p => p.Id == Fixture.KiroOpusProfile).JobTemplate.Should().Be("dotnet,kiro");

        moved.Profiles.Where(p => !p.Enabled).Select(p => p.Id).Should().BeEquivalentTo(
            [.. Fixture.ProfilesMovedDisabled, Fixture.DisabledProfile]);
        summary.ProfilesMoved.Should().Be(Fixture.WithDefaultLabels.ProfileJson.Count);
        summary.ProfilesDisabled.Should().Be(Fixture.ProfilesMovedDisabled.Count);
        summary.DuplicateNames.Should().Equal("Claude");
        Infos().Should().Contain(m => m.Contains("legacyFlag", StringComparison.Ordinal), "unknown setting keys are logged, without values");
    }

    [Fact]
    public async Task Move_StoredProfilesHaveTheNewShape()
    {
        var (db, _) = await MoveAsync(Fixture.WithDefaultLabels);

        foreach (var entity in await db.AgentProfiles.AsNoTracking().ToListAsync())
        {
            var node = JsonNode.Parse(entity.Configuration!)!.AsObject();
            node.Select(p => p.Key).Should().NotContain(["agentProviderConfigId", "matchLabels", "priority"]);
            node.Select(p => p.Key).Should().Contain(["id", "displayName", "jobTemplate", "settings", "enabled", "mcpServers"]);
        }
    }

    [Fact]
    public async Task Move_DeletesTheAgentProviderConfigs_AndTheDefaultLabels_Req7_10()
    {
        var (db, summary) = await MoveAsync(Fixture.WithDefaultLabels);

        (await db.ProviderConfigs.CountAsync(p => p.Kind == ProviderKind.Agent)).Should().Be(0);
        (await db.ProviderConfigs.CountAsync(p => p.Kind == ProviderKind.Repository)).Should().Be(10);
        summary.ProviderConfigsDeleted.Should().Be(5);
        var pipeline = JsonNode.Parse((await db.PipelineConfig.SingleAsync()).Configuration!)!.AsObject();
        pipeline.Select(p => p.Key).Should().NotContain("defaultRequiredAgentLabels");
    }

    [Fact]
    public async Task Move_StackLabels_AreTheImplementationProfilesLabels_WithoutUnnamedToolLabels_Req7_8()
    {
        var (db, summary) = await MoveAsync(Fixture.WithDefaultLabels);
        var moved = await LoadAsync(db);
        IReadOnlyList<string>? Labels(string name) => moved.Templates.Single(t => t.Name == name).RequiredLabels;

        Labels("api").Should().Equal("dotnet", "kiro"); // kiro: a reviewer config names it
        Labels("ml").Should().Equal("dotnet", "kiro", "python"); // the polyglot profile's labels
        Labels("web").Should().Equal("dotnet"); // claude: no config names it
        Labels("tools").Should().Equal("dotnet"); // opencode: no config names it
        Labels("nolabels").Should().Equal("dotnet", "kiro"); // the default labels' profile
        Labels("legacy-java").Should().Equal("java"); // no profile: kept
        summary.TemplatesRelabelled.Should().BeEquivalentTo("ml", "web", "archive", "tools", "nolabels");
    }

    // ── Running again (DD9, review-3 M2) ─────────────────────────────────────

    [Fact]
    public async Task Move_RunTwice_SecondRunChangesNothing()
    {
        var (db, _) = await MoveAsync(Fixture.WithDefaultLabels);
        var before = await SnapshotAsync(db);

        var second = await AgentRoutingMove.RunAsync(db, Fixture.WithDefaultLabels.ToolsByKey, Logger(), CancellationToken.None);

        second.Should().BeNull();
        (await SnapshotAsync(db)).Should().Equal(before);
    }

    [Fact]
    public async Task Move_AfterTheAppRewroteEveryProfileAndThePipeline_StillChangesNothing()
    {
        var (db, _) = await MoveAsync(Fixture.WithDefaultLabels);
        // The record still writes the old members, empty, until commit 6 removes them: they are not legacy data.
        foreach (var entity in await db.AgentProfiles.ToListAsync())
        {
            var profile = JsonSerializer.Deserialize<AgentProfile>(entity.Configuration!, PipelineJsonOptions.Lenient)!;
            entity.Configuration = JsonSerializer.Serialize(profile, PipelineJsonOptions.Default);
        }
        var pipeline = await db.PipelineConfig.SingleAsync();
        pipeline.Configuration = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<PipelineConfiguration>(pipeline.Configuration!, PipelineJsonOptions.Lenient), PipelineJsonOptions.Default);
        await db.SaveChangesAsync();
        var enabledBefore = (await LoadAsync(db)).Profiles.Count(p => p.Enabled);

        var again = await AgentRoutingMove.RunAsync(db, Fixture.WithDefaultLabels.ToolsByKey, Logger(), CancellationToken.None);

        again.Should().BeNull();
        (await LoadAsync(db)).Profiles.Count(p => p.Enabled).Should().Be(enabledBefore, "no profile is disabled by a second run");
    }

    [Fact]
    public async Task Move_ALegacyProfileWrittenAfterTheMove_IsConvertedDisabled_AndTheRoutesStay_Req7_13()
    {
        var (db, _) = await MoveAsync(Fixture.WithDefaultLabels);
        var routingBefore = (await db.KeyValueStore.SingleAsync(kv => kv.Key == RoutingTable.StorageKey)).Value;
        var templatesBefore = await db.PipelineJobTemplates.AsNoTracking().Select(t => t.Configuration).ToListAsync();
        // A previous-release replica saves a profile with the agent provider config its stale tab had loaded.
        const string lateProfile = "00000000-0000-0000-0000-000000000199";
        db.AgentProfiles.Add(new AgentProfileEntity
        {
            Id = Guid.Parse(lateProfile), Name = "Late",
            Configuration = Fixture.LegacyProfileJson(lateProfile, "Late", ["dotnet", "kiro"], Fixture.KiroConfig),
        });
        await db.SaveChangesAsync();

        var summary = await AgentRoutingMove.RunAsync(db, Fixture.WithDefaultLabels.ToolsByKey, Logger(), CancellationToken.None);

        summary!.FullMove.Should().BeFalse();
        summary.ProfilesMoved.Should().Be(1);
        summary.ProfilesDisabled.Should().Be(1);
        (await LoadAsync(db)).Profiles.Single(p => p.Id == lateProfile).Enabled.Should().BeFalse();
        (await db.KeyValueStore.SingleAsync(kv => kv.Key == RoutingTable.StorageKey)).Value.Should().Be(routingBefore);
        (await db.PipelineJobTemplates.AsNoTracking().Select(t => t.Configuration).ToListAsync()).Should().Equal(templatesBefore);
        Warnings().Should().Contain(m => m.Contains("\"Late\"", StringComparison.Ordinal) && m.Contains("moved disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Move_NewShapeConfigurationWithoutRouting_OnlyGetsAnEmptyRoutingTable_DD9()
    {
        await using var db = NewDb();
        var profile = AgentRoutingTestData.Profile(Fixture.KiroProfile, "dotnet,kiro", "Kiro");
        var json = JsonSerializer.Serialize(profile, PipelineJsonOptions.Default);
        db.AgentProfiles.Add(new AgentProfileEntity { Id = Guid.Parse(profile.Id), Name = profile.DisplayName, Configuration = json });
        await db.SaveChangesAsync();

        var summary = await AgentRoutingMove.RunAsync(db, AgentRoutingTestData.ToolsByKey(), Logger(), CancellationToken.None);

        summary.Should().BeNull();
        (await AgentRoutingStore.LoadAsync(db, CancellationToken.None)).Should().BeEquivalentTo(RoutingTable.Empty);
        (await db.AgentProfiles.SingleAsync()).Configuration.Should().Be(json, "a profile of the new shape is not converted");
    }

    [Fact]
    public async Task Move_EmptyDatabase_GetsAnEmptyRoutingTable()
    {
        await using var db = NewDb();

        var summary = await AgentRoutingMove.RunAsync(db, new Dictionary<string, string>(), Logger(), CancellationToken.None);

        summary.Should().BeNull();
        (await AgentRoutingStore.LoadAsync(db, CancellationToken.None))!.Default.Should().BeNull();
    }

    [Fact]
    public async Task Move_LogsNoSecretValues_Req7_11()
    {
        await MoveAsync(Fixture.WithDefaultLabels);

        var rendered = string.Join("\n", _logs.Select(l => l.RenderMessage()));
        rendered.Should().NotContain("made-up-token").And.NotContain(ClaudeCodeAuthModes.Subscription)
            .And.NotContain("opencode.example.test");
        _logs.Should().Contain(l => l.MessageTemplate.Text.StartsWith("Agent routing move (", StringComparison.Ordinal)
            && l.Properties.ContainsKey("ProfilesMoved") && l.Properties.ContainsKey("TemplatesWithoutProfile"));
    }

    [Fact]
    public async Task RunWithLockAsync_MovesInOneTransaction_AndLogsTheSummary()
    {
        var name = Guid.NewGuid().ToString();
        await using (var seed = NewDb(name))
            await Fixture.SeedAsync(seed, Fixture.WithDefaultLabels);

        var summary = await AgentRoutingMove.RunWithLockAsync(
            new AgentRoutingTestSupport.InMemoryFactory(name), new AgentRoutingTestSupport.SemaphoreLockProvider(), Fixture.WithDefaultLabels.ToolsByKey, Logger(), CancellationToken.None);

        summary!.FullMove.Should().BeTrue();
        await using var check = NewDb(name);
        (await AgentRoutingStore.LoadAsync(check, CancellationToken.None))!.Default.Should().NotBeNull();
        _logs.Should().Contain(l => l.MessageTemplate.Text.StartsWith("Agent routing move (", StringComparison.Ordinal));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static LegacyRoutingScenario Scenario(string name) => Fixture.Scenarios.Single(s => s.Name == name);

    private async Task<(PipelineDbContext Db, AgentRoutingMoveSummary Summary)> MoveAsync(LegacyRoutingScenario scenario)
    {
        var db = NewDb();
        await Fixture.SeedAsync(db, scenario);
        var summary = await AgentRoutingMove.RunAsync(db, scenario.ToolsByKey, Logger(), CancellationToken.None);
        summary.Should().NotBeNull();
        summary!.FullMove.Should().BeTrue();
        AgentRoutingMove.LogSummary(Logger(), summary);
        return (db, summary);
    }

    private sealed record Moved(
        IReadOnlyList<AgentProfile> Profiles, IReadOnlyList<PipelineJobTemplate> Templates, RoutingTable Routing);

    private static async Task<Moved> LoadAsync(PipelineDbContext db)
    {
        var profiles = (await db.AgentProfiles.AsNoTracking().ToListAsync())
            .Select(e => JsonSerializer.Deserialize<AgentProfile>(e.Configuration!, PipelineJsonOptions.Lenient)!).ToList();
        var templates = (await db.PipelineJobTemplates.AsNoTracking().ToListAsync())
            .Select(e => JsonSerializer.Deserialize<PipelineJobTemplate>(e.Configuration!, PipelineJsonOptions.Lenient)!).ToList();
        var routing = await AgentRoutingStore.LoadAsync(db, CancellationToken.None) ?? RoutingTable.Empty;
        return new Moved(profiles, templates, routing);
    }

    private static async Task<List<string?>> SnapshotAsync(PipelineDbContext db) =>
    [
        .. await db.AgentProfiles.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Configuration).ToListAsync(),
        .. await db.PipelineJobTemplates.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Configuration).ToListAsync(),
        .. await db.ProviderConfigs.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Configuration).ToListAsync(),
        .. await db.KeyValueStore.AsNoTracking().OrderBy(e => e.Key).Select(e => e.Value).ToListAsync(),
        (await db.PipelineConfig.AsNoTracking().SingleAsync()).Configuration,
    ];

    private static string NormalizedModel(string? model) => string.IsNullOrWhiteSpace(model) ? "auto" : model;

    private static string NormalizedEffort(string? effort) =>
        AgentEffortLevelExtensions.ParseEffort(effort).ToCliValue() ?? "auto";

    /// <summary>Settings as an agent reads them: a blank model and a blank or unknown effort mean the tool's choice.</summary>
    private static SortedDictionary<string, string> Normalized(IReadOnlyDictionary<string, string> settings)
    {
        var normalized = new SortedDictionary<string, string>(settings.ToDictionary(), StringComparer.Ordinal)
        {
            [ProviderSettingKeys.Model] = NormalizedModel(settings.GetValueOrDefault(ProviderSettingKeys.Model)),
            [ProviderSettingKeys.Effort] = NormalizedEffort(settings.GetValueOrDefault(ProviderSettingKeys.Effort)),
        };
        return normalized;
    }

    private static List<string> GateIds(LegacyRoutingScenario scenario, IReadOnlyList<string> labels) =>
        new QualityGateResolver().Resolve(scenario.QualityGates, labels).Select(g => g.Id).Order(StringComparer.Ordinal).ToList();

    private static List<string> ReviewerIds(LegacyRoutingScenario scenario, IReadOnlyList<string> labels) =>
        new ReviewerResolver().Resolve(scenario.Reviewers, labels).Select(r => r.Id).Order(StringComparer.Ordinal).ToList();

    private static List<string> McpNames(AgentProfile profile, PipelineProject project) =>
        McpServerMerge.Merge(profile.McpServers, project.McpServers).Select(s => s.Name).Order(StringComparer.Ordinal).ToList();

    private IEnumerable<string> Warnings() =>
        _logs.Where(l => l.Level == LogEventLevel.Warning).Select(l => l.RenderMessage());

    private IEnumerable<string> Infos() =>
        _logs.Where(l => l.Level == LogEventLevel.Information).Select(l => l.RenderMessage());

    private Serilog.ILogger Logger() => AgentRoutingTestSupport.Logger(_logs);

    private static PipelineDbContext NewDb(string? name = null) => AgentRoutingTestSupport.NewDb(name);
}
