using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Spec 053 req 4.1 and D15: <see cref="AgentRouteResolver"/> — repository row, else global task row, else global
/// default; a row that does not resolve is refused and never falls through; nothing picks an unrouted profile.
/// </summary>
public class AgentRouteResolverTests
{
    private static readonly AgentProfile Kiro = AgentRoutingTestData.Profile("profile-kiro", "dotnet,kiro", "Kiro");
    private static readonly AgentProfile Claude = AgentRoutingTestData.Profile("profile-claude", "claude,dotnet", "Claude");
    private static readonly AgentProfile OpenCode = AgentRoutingTestData.Profile("profile-opencode", "dotnet,opencode", "OpenCode");
    private static readonly AgentProfile Disabled = AgentRoutingTestData.Profile("profile-off", "dotnet,kiro", "Retired", enabled: false);
    private static readonly IReadOnlyList<AgentProfile> Profiles = [Kiro, Claude, OpenCode, Disabled];

    private static readonly IReadOnlyDictionary<string, string> Tools = AgentRoutingTestData.ToolsByKey(
        ("dotnet,kiro", "kiro"), ("claude,dotnet", "claude"), ("dotnet,opencode", "OpenCode"));

    public static TheoryData<WorkItemTaskType> TaskTypes
    {
        get
        {
            var data = new TheoryData<WorkItemTaskType>();
            foreach (var taskType in Enum.GetValues<WorkItemTaskType>())
                data.Add(taskType);
            return data;
        }
    }

    private static PipelineJobTemplate Template(params (WorkItemTaskType TaskType, AgentRoute Route)[] rows) => new()
    {
        Id = "t-api", Name = "api", IssueProviderId = "ip", RepoProviderId = "rp",
        AgentRoutes = rows.Length == 0 ? null : rows.ToDictionary(r => r.TaskType, r => r.Route),
    };

    private static AgentRouting Routing(params (WorkItemTaskType TaskType, AgentRoute Route)[] rows) =>
        AgentRoutingTestData.Routing("profile-kiro", "sonnet", "high", rows);

    // ── 3 sources × 5 task types ─────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(TaskTypes))]
    public void Resolve_RepositoryRow_WinsOverTheGlobalRowAndTheDefault(WorkItemTaskType taskType)
    {
        var template = Template((taskType, AgentRoutingTestData.Route("profile-opencode", "openai/gpt-5", "auto")));
        var routing = Routing((taskType, AgentRoutingTestData.Route("profile-claude", "opus", "xhigh")));

        var result = AgentRouteResolver.Resolve(taskType, template, routing, Profiles, Tools);

        result.Snapshot.Should().Be(new AgentRouteSnapshot(
            "profile-opencode", "OpenCode", "opencode", "openai/gpt-5", "auto", AgentRouteSource.Repository));
        result.Profile.Should().BeSameAs(OpenCode);
    }

    [Theory]
    [MemberData(nameof(TaskTypes))]
    public void Resolve_GlobalTaskRow_WinsOverTheDefault(WorkItemTaskType taskType)
    {
        var routing = Routing((taskType, AgentRoutingTestData.Route("profile-claude", "opus", "xhigh")));

        var result = AgentRouteResolver.Resolve(taskType, Template(), routing, Profiles, Tools);

        result.Snapshot.Should().Be(new AgentRouteSnapshot(
            "profile-claude", "Claude", "claude", "opus", "xhigh", AgentRouteSource.GlobalTaskRow));
    }

    [Theory]
    [MemberData(nameof(TaskTypes))]
    public void Resolve_NoRows_UsesTheGlobalDefault(WorkItemTaskType taskType)
    {
        var result = AgentRouteResolver.Resolve(taskType, Template(), Routing(), Profiles, Tools);

        result.Snapshot.Should().Be(new AgentRouteSnapshot(
            "profile-kiro", "Kiro", "kiro", "sonnet", "high", AgentRouteSource.GlobalDefault));
    }

    [Fact]
    public void Resolve_RowsOfOtherTaskTypes_DoNotApply()
    {
        var template = Template((WorkItemTaskType.Decomposition, AgentRoutingTestData.Route("profile-claude", "opus", "high")));
        var routing = Routing((WorkItemTaskType.Triage, AgentRoutingTestData.Route("profile-opencode", "auto", "auto")));

        var result = AgentRouteResolver.Resolve(WorkItemTaskType.Implementation, template, routing, Profiles, Tools);

        result.Snapshot!.Source.Should().Be(AgentRouteSource.GlobalDefault);
        result.Snapshot.ProfileId.Should().Be("profile-kiro");
    }

    [Fact]
    public void Resolve_NullTemplate_UsesTheGlobalRowsOnly()
    {
        var routing = Routing((WorkItemTaskType.Consolidation, AgentRoutingTestData.Route("profile-claude", "opus", "high")));

        var result = AgentRouteResolver.Resolve(WorkItemTaskType.Consolidation, null, routing, Profiles, Tools);

        result.Snapshot!.Source.Should().Be(AgentRouteSource.GlobalTaskRow);
    }

    [Fact]
    public void Resolve_NullRowsInTheTables_AreNoRows()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t", Name = "api", IssueProviderId = "ip", RepoProviderId = "rp",
            AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Implementation] = null! },
        };
        var routing = new AgentRouting
        {
            Default = AgentRoutingTestData.Route("profile-kiro"),
            ByTaskType = new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Implementation] = null! },
        };

        var result = AgentRouteResolver.Resolve(WorkItemTaskType.Implementation, template, routing, Profiles, Tools);

        result.Snapshot!.Source.Should().Be(AgentRouteSource.GlobalDefault);
    }

    // ── Refusals (D15) ───────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(TaskTypes))]
    public void Resolve_NoRowAtAnyLevel_IsRefused_Req2_9(WorkItemTaskType taskType)
    {
        var result = AgentRouteResolver.Resolve(taskType, Template(), AgentRouting.Empty, Profiles, Tools);

        result.IsRefused.Should().BeTrue("no code path picks a profile that no route names (D15)");
        result.RefusalReason.Should().Be(AgentRouteRefusalReason.NoRoute);
        result.RefusalText.Should().Be("No agent route: set the default route in Settings → Agents → Routing");
        result.Profile.Should().BeNull();
    }

    [Fact]
    public void Resolve_RepositoryRowNamingADisabledProfile_IsRefused_AndDoesNotFallThroughToTheDefault()
    {
        var template = Template((WorkItemTaskType.Implementation, AgentRoutingTestData.Route("profile-off", "auto", "auto")));

        var result = AgentRouteResolver.Resolve(WorkItemTaskType.Implementation, template, Routing(), Profiles, Tools);

        result.RefusalReason.Should().Be(AgentRouteRefusalReason.ProfileUnavailable);
        result.RefusalText.Should().Be(
            "The repository route for Implementation names agent profile 'Retired', which does not exist or is disabled");
    }

    [Fact]
    public void Resolve_GlobalRowNamingAMissingProfile_IsRefused_WithTheId()
    {
        var routing = Routing((WorkItemTaskType.Review, AgentRoutingTestData.Route("deleted-id", "auto", "auto")));

        var result = AgentRouteResolver.Resolve(WorkItemTaskType.Review, Template(), routing, Profiles, Tools);

        result.RefusalReason.Should().Be(AgentRouteRefusalReason.ProfileUnavailable);
        result.RefusalText.Should().Be(
            "The global route for PR review names agent profile 'deleted-id', which does not exist or is disabled");
    }

    [Fact]
    public void Resolve_DefaultNamingADisabledProfile_IsRefused()
    {
        var routing = AgentRoutingTestData.Routing("profile-off");

        var result = AgentRouteResolver.Resolve(WorkItemTaskType.Triage, null, routing, Profiles, Tools);

        result.RefusalText.Should().Be(
            "The global default route for Triage names agent profile 'Retired', which does not exist or is disabled");
    }

    [Theory]
    [InlineData("")]
    [InlineData("gemini")]
    public void Resolve_JobTemplateWithoutAKnownTool_IsRefused(string providerType)
    {
        var tools = AgentRoutingTestData.ToolsByKey(("dotnet,kiro", providerType));

        var result = AgentRouteResolver.Resolve(WorkItemTaskType.Implementation, null, Routing(), Profiles, tools);

        result.RefusalReason.Should().Be(AgentRouteRefusalReason.NoTool);
        result.RefusalText.Should().Be("Job template dotnet,kiro of agent profile 'Kiro' has no providerType");
    }

    // ── Job template checks ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_NoJobTemplatesKnown_SkipsTheTemplateChecks()
    {
        var empty = new Dictionary<string, string>();

        var result = AgentRouteResolver.Resolve(WorkItemTaskType.Implementation, null, Routing(), Profiles, empty);

        result.Snapshot!.ProfileId.Should().Be("profile-kiro");
        result.Snapshot.Tool.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_MissingJobTemplate_IsNotRefusedHere_TheDispatcherDefersIt()
    {
        var tools = AgentRoutingTestData.ToolsByKey(("claude,dotnet", "claude"));

        var result = AgentRouteResolver.Resolve(WorkItemTaskType.Implementation, null, Routing(), Profiles, tools);

        result.IsRefused.Should().BeFalse();
        result.Snapshot!.Tool.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ProviderConfigTypeAsProviderType_IsNormalizedToTheToolId()
    {
        var result = AgentRouteResolver.Resolve(
            WorkItemTaskType.Implementation, null,
            AgentRoutingTestData.Routing("profile-opencode"), Profiles, Tools);

        result.Snapshot!.Tool.Should().Be("opencode");
    }
}
