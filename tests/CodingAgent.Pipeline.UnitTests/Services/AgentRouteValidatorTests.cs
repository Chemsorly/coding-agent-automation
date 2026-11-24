using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Spec 053 req 2.2–2.6, 3.5, 7.12, 10.5: <see cref="AgentRouteValidator"/> refuses every malformed route with one line
/// per problem, so the endpoints answer 4xx instead of 5xx.
/// </summary>
public class AgentRouteValidatorTests
{
    private static readonly AgentProfile Kiro = AgentRoutingTestData.Profile("profile-kiro", "dotnet,kiro", "Kiro");
    private static readonly AgentProfile Claude = AgentRoutingTestData.Profile("profile-claude", "claude,dotnet", "Claude");
    private static readonly AgentProfile OpenCode = AgentRoutingTestData.Profile("profile-opencode", "dotnet,opencode", "OpenCode");
    private static readonly AgentProfile Homeless = AgentRoutingTestData.Profile("profile-homeless", "dotnet,gone", "Homeless");
    private static readonly AgentProfile NoTool = AgentRoutingTestData.Profile("profile-notool", "dotnet", "No tool");
    private static readonly IReadOnlyList<AgentProfile> Profiles = [Kiro, Claude, OpenCode, Homeless, NoTool];

    private static readonly IReadOnlyDictionary<string, string> Tools = AgentRoutingTestData.ToolsByKey(
        ("dotnet,kiro", "kiro"), ("claude,dotnet", "claude"), ("dotnet,opencode", "opencode"), ("dotnet", ""));

    private static AgentRouting Table(params (WorkItemTaskType TaskType, AgentRoute Route)[] rows) =>
        AgentRoutingTestData.Routing("profile-kiro", "sonnet", "high", rows);

    // ── Routing table (req 2.6) ──────────────────────────────────────────────

    [Fact]
    public void ValidateRouting_ValidTable_HasNoErrors()
    {
        var table = Table(
            (WorkItemTaskType.Decomposition, AgentRoutingTestData.Route("profile-claude", "opus", "xhigh")),
            (WorkItemTaskType.Review, AgentRoutingTestData.Route("profile-opencode", "openai/gpt-5", "auto")));

        AgentRouteValidator.ValidateRouting(table, Profiles, Tools).Should().BeEmpty();
    }

    [Fact]
    public void ValidateRouting_NullTable_IsRefused_Req10_5()
        => AgentRouteValidator.ValidateRouting(null, Profiles, Tools).Should().Equal("The routing table is missing");

    [Fact]
    public void ValidateRouting_MissingDefaultRow_IsRefused_OnceAProfileExists_Req2_1()
    {
        var table = new AgentRouting();

        AgentRouteValidator.ValidateRouting(table, Profiles, Tools).Should().Equal("Default: a default route is required");
        AgentRouteValidator.ValidateRouting(table, [], Tools).Should().BeEmpty("a fresh install has no profile to route to yet");
    }

    [Fact]
    public void ValidateRouting_ReportsOneLinePerInvalidRow()
    {
        var table = Table(
            (WorkItemTaskType.Decomposition, AgentRoutingTestData.Route("profile-claude", "", "high")),
            (WorkItemTaskType.Triage, AgentRoutingTestData.Route("profile-homeless", "opus", "high")));

        AgentRouteValidator.ValidateRouting(table, Profiles, Tools).Should().Equal(
            "Decomposition: model is required (auto lets the tool choose)",
            "Triage: job template dotnet,gone of agent profile 'Homeless' does not exist in the Helm values");
    }

    [Fact]
    public void ValidateRouting_UnknownTaskTypeKey_IsRefused_Req10_5()
    {
        var table = Table(((WorkItemTaskType)99, AgentRoutingTestData.Route()));

        AgentRouteValidator.ValidateRouting(table, Profiles, Tools).Should().Equal("Task type 99 is unknown");
    }

    [Fact]
    public void ValidateRouting_NullRow_IsRefused()
    {
        var table = Table((WorkItemTaskType.Triage, null!));

        AgentRouteValidator.ValidateRouting(table, Profiles, Tools).Should().Equal("Triage: the route is missing");
    }

    // ── Profiles and job templates (req 2.2) ─────────────────────────────────

    [Theory]
    [InlineData("deleted-id", "Default: agent profile 'deleted-id' does not exist")]
    [InlineData("ip-github", "Default: agent profile 'ip-github' does not exist")] // a provider config Id: another kind
    [InlineData("", "Default: an agent profile is required")]
    [InlineData("profile-homeless", "Default: job template dotnet,gone of agent profile 'Homeless' does not exist in the Helm values")]
    [InlineData("profile-notool", "Default: Job template dotnet of agent profile 'No tool' has no providerType")]
    public void ValidateRoute_ProfileProblems_AreRefused_Req10_5(string profileId, string error)
        => AgentRouteValidator.ValidateRoute("Default", AgentRoutingTestData.Route(profileId, "auto", "auto"), Profiles, Tools)
            .Should().Equal(error);

    [Fact]
    public void ValidateRoute_NoJobTemplatesKnown_SkipsTheTemplateChecks()
        => AgentRouteValidator.ValidateRoute("Default", AgentRoutingTestData.Route("profile-homeless"), Profiles, new Dictionary<string, string>())
            .Should().BeEmpty();

    // ── Model (req 2.3) ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("auto")]
    [InlineData("claude-opus-4-8")]
    [InlineData("anthropic/claude-sonnet-4.6")]
    [InlineData("gpt_5")]
    public void ValidateModel_ValidModels_HaveNoErrors(string model)
        => AgentRouteValidator.ValidateModel("Default", model).Should().BeEmpty();

    [Theory]
    [InlineData(null, "Default: model is required (auto lets the tool choose)")]
    [InlineData("", "Default: model is required (auto lets the tool choose)")]
    [InlineData("   ", "Default: model is required (auto lets the tool choose)")]
    [InlineData("opus; rm -rf /", "Default: model may contain only letters, digits, '.', '_', '-' and '/'")]
    [InlineData("$(whoami)", "Default: model may contain only letters, digits, '.', '_', '-' and '/'")]
    [InlineData("claude opus", "Default: model may contain only letters, digits, '.', '_', '-' and '/'")]
    public void ValidateModel_InvalidModels_AreRefused_Req10_5(string? model, string error)
        => AgentRouteValidator.ValidateModel("Default", model).Should().Equal(error);

    [Fact]
    public void ValidateModel_LongerThan200Characters_IsRefused_Req10_5()
    {
        AgentRouteValidator.ValidateModel("Default", new string('a', 200)).Should().BeEmpty();
        AgentRouteValidator.ValidateModel("Default", new string('a', 201))
            .Should().Equal("Default: model is longer than 200 characters");
    }

    // ── Effort (req 2.5) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("profile-claude", "xhigh")]
    [InlineData("profile-claude", "max")]
    [InlineData("profile-kiro", "max")]
    [InlineData("profile-kiro", "auto")]
    [InlineData("profile-opencode", "auto")]
    public void ValidateRoute_EffortTheToolAccepts_HasNoErrors(string profileId, string effort)
        => AgentRouteValidator.ValidateRoute("Default", AgentRoutingTestData.Route(profileId, "auto", effort), Profiles, Tools)
            .Should().BeEmpty();

    [Theory]
    [InlineData("profile-kiro", "xhigh", "Default: Kiro does not accept effort 'xhigh' (use auto, low, medium, high, max)")]
    [InlineData("profile-opencode", "high", "Default: OpenCode has no effort setting; use auto")]
    [InlineData("profile-claude", "", "Default: effort is required (auto uses the tool's default)")]
    [InlineData("profile-claude", "HIGH", "Default: Claude Code does not accept effort 'HIGH' (use auto, low, medium, high, xhigh, max)")]
    public void ValidateRoute_EffortTheToolRefuses_IsRefused_Req10_5(string profileId, string effort, string error)
        => AgentRouteValidator.ValidateRoute("Default", AgentRoutingTestData.Route(profileId, "auto", effort), Profiles, Tools)
            .Should().Equal(error);

    [Fact]
    public void ValidateEffort_UnknownTool_AcceptsAnyKnownEffort_ButNoInventedOne()
    {
        AgentRouteValidator.ValidateEffort("Default", "xhigh", null).Should().BeEmpty();
        AgentRouteValidator.ValidateEffort("Default", "turbo", null).Should().Equal("Default: effort 'turbo' is unknown");
    }

    // ── Light tier ───────────────────────────────────────────────────────────

    [Fact]
    public void ValidateRoute_InvalidLightModel_NamesTheLightRow()
    {
        var route = AgentRoutingTestData.Route("profile-claude", "opus", "high") with { LightModel = "not a model!" };

        AgentRouteValidator.ValidateRoute("Implementation", route, Profiles, Tools)
            .Should().Equal("Implementation (light): model may contain only letters, digits, '.', '_', '-' and '/'");
    }

    [Fact]
    public void ValidateRoute_LightModelTooLong_NamesTheLightRow()
    {
        var route = AgentRoutingTestData.Route("profile-claude", "opus", "high") with { LightModel = new string('a', 201) };

        AgentRouteValidator.ValidateRoute("Default", route, Profiles, Tools)
            .Should().Equal("Default (light): model is longer than 200 characters");
    }

    [Theory]
    [InlineData("profile-kiro", "xhigh", "Default (light): Kiro does not accept effort 'xhigh' (use auto, low, medium, high, max)")]
    [InlineData("profile-opencode", "high", "Default (light): OpenCode has no effort setting; use auto")]
    [InlineData("profile-claude", "HIGH", "Default (light): Claude Code does not accept effort 'HIGH' (use auto, low, medium, high, xhigh, max)")]
    public void ValidateRoute_LightEffortTheToolDoesNotTake_IsRefused(string profileId, string lightEffort, string error)
        => AgentRouteValidator.ValidateRoute(
                "Default", AgentRoutingTestData.Route(profileId, "auto", "auto") with { LightEffort = lightEffort }, Profiles, Tools)
            .Should().Equal(error);

    [Theory]
    [InlineData("profile-claude", "xhigh")]
    [InlineData("profile-kiro", "low")]
    [InlineData("profile-kiro", "auto")]
    [InlineData("profile-opencode", "auto")]
    public void ValidateRoute_LightEffortTheToolAccepts_HasNoErrors(string profileId, string lightEffort)
        => AgentRouteValidator.ValidateRoute(
                "Default", AgentRoutingTestData.Route(profileId, "auto", "auto") with { LightEffort = lightEffort, LightModel = "haiku" }, Profiles, Tools)
            .Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRoute_BlankLightValues_AreValid(string? blank)
        => AgentRouteValidator.ValidateRoute(
                "Default", AgentRoutingTestData.Route("profile-opencode", "auto", "auto") with { LightModel = blank, LightEffort = blank }, Profiles, Tools)
            .Should().BeEmpty("a blank light value is unset");

    [Fact]
    public void ValidateRoute_LightEffortWithNoJobTemplatesKnown_AcceptsAnyKnownEffort_ButNoInventedOne()
    {
        var empty = new Dictionary<string, string>();
        var known = AgentRoutingTestData.Route("profile-kiro", "auto", "auto") with { LightEffort = "xhigh" };
        var invented = known with { LightEffort = "turbo" };

        AgentRouteValidator.ValidateRoute("Default", known, Profiles, empty).Should().BeEmpty();
        AgentRouteValidator.ValidateRoute("Default", invented, Profiles, empty).Should().Equal("Default (light): effort 'turbo' is unknown");
    }

    [Fact]
    public void ValidateRoute_InvalidLightValues_AddToTheRoutesOwnErrors()
    {
        var route = AgentRoutingTestData.Route("profile-kiro", "bad model", "xhigh") with { LightModel = "bad light", LightEffort = "xhigh" };

        AgentRouteValidator.ValidateRoute("Default", route, Profiles, Tools).Should().Equal(
            "Default: model may contain only letters, digits, '.', '_', '-' and '/'",
            "Default: Kiro does not accept effort 'xhigh' (use auto, low, medium, high, max)",
            "Default (light): model may contain only letters, digits, '.', '_', '-' and '/'",
            "Default (light): Kiro does not accept effort 'xhigh' (use auto, low, medium, high, max)");
    }

    [Fact]
    public void ValidateTemplateRoutes_InvalidLightEffort_IsRefusedByTaskType()
    {
        var rows = new Dictionary<WorkItemTaskType, AgentRoute>
        {
            [WorkItemTaskType.Review] = AgentRoutingTestData.Route("profile-opencode", "auto", "auto") with { LightEffort = "low" },
        };

        AgentRouteValidator.ValidateTemplateRoutes(rows, Profiles, Tools)
            .Should().Equal("PR review (light): OpenCode has no effort setting; use auto");
    }

    [Fact]
    public void ValidateImport_LightEffortTheToolDoesNotTake_IsRefused()
    {
        var table = Table((WorkItemTaskType.Review,
            AgentRoutingTestData.Route("profile-opencode", "auto", "auto") with { LightEffort = "high" }));

        AgentRouteValidator.ValidateImport(table, Profiles, Tools)
            .Should().Equal("PR review (light): OpenCode has no effort setting; use auto");
    }

    [Fact]
    public void ValidateImport_InvalidLightModel_NamesTheLightRow()
    {
        var table = AgentRoutingTestData.Routing("profile-kiro", "auto", "high");
        table = table with { Default = table.Default! with { LightModel = "bad light" } };

        AgentRouteValidator.ValidateImport(table, Profiles, Tools)
            .Should().Equal("Default (light): model may contain only letters, digits, '.', '_', '-' and '/'");
    }

    [Fact]
    public void ValidateImport_BlankLightValues_AreValid()
    {
        var table = Table((WorkItemTaskType.Review,
            AgentRoutingTestData.Route("profile-opencode", "auto", "auto") with { LightModel = " ", LightEffort = "" }));

        AgentRouteValidator.ValidateImport(table, Profiles, Tools).Should().BeEmpty();
    }

    // ── Template rows (req 3.5) ──────────────────────────────────────────────

    [Fact]
    public void ValidateTemplateRoutes_Null_MeansKeepTheStoredRows_AndIsValid()
        => AgentRouteValidator.ValidateTemplateRoutes(null, Profiles, Tools).Should().BeEmpty();

    [Fact]
    public void ValidateTemplateRoutes_InvalidRows_AreRefusedByTaskType_Req10_5()
    {
        var rows = new Dictionary<WorkItemTaskType, AgentRoute>
        {
            [WorkItemTaskType.Implementation] = AgentRoutingTestData.Route("profile-claude", "opus", "high"),
            [WorkItemTaskType.Consolidation] = AgentRoutingTestData.Route("profile-opencode", "auto", "low"),
            [(WorkItemTaskType)42] = AgentRoutingTestData.Route(),
        };

        AgentRouteValidator.ValidateTemplateRoutes(rows, Profiles, Tools).Should().Equal(
            "Consolidation: OpenCode has no effort setting; use auto",
            "Task type 42 is unknown");
    }

    // ── Import (req 7.12) ────────────────────────────────────────────────────

    [Fact]
    public void ValidateImport_BundleWithoutRouting_IsValid_TheMoveWritesIt()
        => AgentRouteValidator.ValidateImport(null, Profiles, Tools).Should().BeEmpty();

    [Fact]
    public void ValidateImport_ChecksTheShapeOnly_NotWhetherJobTemplatesExistHere()
    {
        var table = AgentRoutingTestData.Routing("profile-homeless", "auto", "high");

        AgentRouteValidator.ValidateImport(table, Profiles, Tools).Should().BeEmpty(
            "a bundle from another cluster imports; the Agent Profiles page flags the missing template");
    }

    [Fact]
    public void ValidateImport_RouteToAProfileNotInTheBundle_IsRefused()
    {
        var table = Table((WorkItemTaskType.Triage, AgentRoutingTestData.Route("profile-elsewhere", "opus", "high")));

        AgentRouteValidator.ValidateImport(table, Profiles, Tools)
            .Should().Equal("Triage: agent profile 'profile-elsewhere' is not in the bundle");
    }

    [Fact]
    public void ValidateImport_MalformedRows_AreRefused_Req10_5()
    {
        var table = Table(
            (WorkItemTaskType.Review, AgentRoutingTestData.Route("profile-opencode", "auto", "max")),
            (WorkItemTaskType.Triage, AgentRoutingTestData.Route("profile-claude", "", "high")),
            ((WorkItemTaskType)7, AgentRoutingTestData.Route()));

        AgentRouteValidator.ValidateImport(table, Profiles, Tools).Should().Equal(
            "PR review: OpenCode has no effort setting; use auto",
            "Triage: model is required (auto lets the tool choose)",
            "Task type 7 is unknown");
    }
}
