using AwesomeAssertions;
using Bunit;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Shared;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Spec 053 req 2.1–2.5, 3.2, 3.7: the routing editor of the Routing page (Default and five task rows) and of the
/// repository page (five task rows that inherit the global table).
/// </summary>
public class RouteTableComponentTests : BunitContext
{
    private static readonly UiRoutingProfile Kiro = new("p-kiro", "Kiro dotnet", JobTemplateProviderType.Kiro, "dotnet,kiro");
    private static readonly UiRoutingProfile Claude = new("p-claude", "Claude dotnet", JobTemplateProviderType.Claude, "claude,dotnet");
    private static readonly UiRoutingProfile OpenCode = new("p-opencode", "OpenCode python", JobTemplateProviderType.Opencode, "opencode,python");
    private static readonly UiRoutingProfile Retired = new("p-retired", "Retired", JobTemplateProviderType.Kiro, "dotnet,kiro");
    private static readonly UiRoutingProfile[] Profiles = [Kiro, Claude, OpenCode, Retired];

    private static readonly AgentRouting Global = new()
    {
        Default = new AgentRoute(Kiro.Id, "auto", "high"),
        ByTaskType = new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Decomposition] = new(Claude.Id, "claude-opus-5-5", "max") },
    };

    private const string LightModel = "claude-haiku-4-5";

    private static readonly AgentRoute ClaudeWithLight = new(Claude.Id, "claude-opus-5-5", "high", LightModel, "low");

    private IRenderedComponent<RouteTable> RenderTable(IReadOnlyList<RouteRow> rows, AgentRouting? inherited = null, IReadOnlyList<string>? kiroModels = null) =>
        Render<RouteTable>(p => p
            .Add(s => s.Rows, rows)
            .Add(s => s.Profiles, Profiles)
            .Add(s => s.Inherited, inherited)
            .Add(s => s.KiroModels, kiroModels ?? []));

    private static string Text(IRenderedComponent<RouteTable> cut, string testId) => cut.Find($"[data-testid='{testId}']").TextContent.Trim();

    [Fact]
    public void TheGlobalTable_HasTheDefaultRowAndOneRowPerTaskType()
    {
        var cut = RenderTable(RouteRow.ForRouting(Global));

        cut.FindAll("tbody tr td:first-child").Select(c => c.TextContent.Trim())
            .Should().Equal("Default", "Implementation", "PR review", "Decomposition", "Triage", "Consolidation");
        cut.FindAll("[data-testid='route-inherit-button-default']").Should().BeEmpty("the default row is required");
    }

    [Fact]
    public void ATaskRowWithoutARoute_SaysWhatTheDefaultRowGivesIt()
    {
        var cut = RenderTable(RouteRow.ForRouting(Global));

        Text(cut, "route-inherit-review").Should().Be("Use default: Kiro dotnet · auto (the tool chooses) · effort high · dotnet,kiro · default");
        cut.Find("[data-testid='route-profile-decomposition']").GetAttribute("value").Should().Be(Claude.Id);
    }

    [Fact]
    public void WithoutADefaultRoute_ATaskRowSaysThatNothingRoutesIt()
    {
        var cut = RenderTable(RouteRow.ForRouting(new AgentRouting()));

        Text(cut, "route-inherit-triage").Should().Contain("No agent route: set the default route");
    }

    [Fact]
    public void TheRepositoryTable_HasFiveRows_ThatInheritTheGlobalTable()
    {
        // Req 3.7: all five task types, whatever the template's switches say.
        var cut = RenderTable(RouteRow.ForRepository(null), Global);

        cut.FindAll("tbody tr").Should().HaveCount(5);
        Text(cut, "route-inherit-implementation").Should().Be("Inherit: Kiro dotnet · auto (the tool chooses) · effort high · dotnet,kiro · global default");
        Text(cut, "route-inherit-decomposition").Should().Be("Inherit: Claude dotnet · claude-opus-5-5 · effort max · claude,dotnet · global task row");
    }

    [Fact]
    public void SetRoute_StartsFromWhatTheRowInherits_AndInheritGivesItBack()
    {
        var rows = RouteRow.ForRepository(null);
        var cut = RenderTable(rows, Global);

        cut.Find("[data-testid='route-set-decomposition']").Click();

        rows[2].Should().BeEquivalentTo(new { UseRoute = true, ProfileId = Claude.Id, Model = "claude-opus-5-5", Effort = "max" });
        cut.Find("[data-testid='route-inherit-button-decomposition']").Click();
        rows[2].UseRoute.Should().BeFalse();
        RouteRow.ToRepositoryRoutes(rows).Should().BeEmpty();
    }

    [Fact]
    public void UseForAllTaskTypes_PutsTheRowsRouteOnAllFive()
    {
        var rows = RouteRow.ForRepository(new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Review] = new(Claude.Id, "claude-opus-5-5", "high") });
        var cut = RenderTable(rows, Global);

        cut.Find("[data-testid='route-use-for-all-review']").Click();

        RouteRow.ToRepositoryRoutes(rows).Should().HaveCount(5).And.AllSatisfy(r => r.Value.Should().Be(new AgentRoute(Claude.Id, "claude-opus-5-5", "high")));
    }

    [Fact]
    public void TheModelField_OffersAutoAndTheToolsModels()
    {
        var rows = RouteRow.ForRouting(Global);
        var cut = RenderTable(rows, kiroModels: ["claude-sonnet-4", "qwen3-coder"]);

        cut.Find("#route-models-default option[value='auto']").TextContent.Should().Be("auto (the tool chooses)");
        cut.FindAll("#route-models-default option").Select(o => o.GetAttribute("value")).Should().Equal("auto", "claude-sonnet-4", "qwen3-coder");
        cut.FindAll("#route-models-decomposition option").Select(o => o.GetAttribute("value")).Should().Contain("claude-opus-5-5");
    }

    [Fact]
    public void TheEffort_OffersWhatTheToolTakes_AndOpenCodeHasNone()
    {
        var rows = RouteRow.ForRouting(Global);
        var cut = RenderTable(rows);

        cut.FindAll("[data-testid='route-effort-default'] option").Select(o => o.GetAttribute("value"))
            .Should().Equal("auto", "low", "medium", "high", "max");
        cut.FindAll("[data-testid='route-effort-decomposition'] option").Select(o => o.GetAttribute("value"))
            .Should().Equal("auto", "low", "medium", "high", "xhigh", "max");

        cut.Find("[data-testid='route-profile-decomposition']").Change(OpenCode.Id);

        rows[3].Effort.Should().Be("auto", "OpenCode takes no effort (req 2.5)");
        cut.Find("[data-testid='route-effort-decomposition']").TagName.Should().Be("SPAN");
    }

    [Fact]
    public void EveryProfile_IsOfferedOnEveryRow_Spec056()
    {
        var cut = RenderTable(RouteRow.ForRouting(Global));

        cut.FindAll("[data-testid='route-profile-default'] option").Select(o => o.TextContent).Should().Contain("Retired (Kiro)");
        cut.Markup.Should().NotContain("disabled)");
    }

    [Fact]
    public void ARowWhoseCredentialIsPaused_ShowsThePauseText_Spec056()
    {
        const string text = "Kiro paused — quota used up, since 2026-11-02 05:00 UTC";
        var routing = Global with { ByTaskType = new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Triage] = new(Retired.Id, "auto", "auto") } };
        var cut = Render<RouteTable>(p => p
            .Add(c => c.Rows, RouteRow.ForRouting(routing))
            .Add(c => c.Profiles, [Kiro, Claude, OpenCode, Retired with { PausedText = text }]));

        cut.Find("[data-testid='route-paused-triage']").TextContent.Should().Be(text);
        cut.FindAll("[data-testid='route-paused-default']").Should().BeEmpty();
    }

    [Fact]
    public void TheRows_RoundTripTheTable_AndADefaultRowWithoutProfileIsNoDefault()
    {
        RouteRow.ToRouting(RouteRow.ForRouting(Global)).Should().BeEquivalentTo(Global);

        RouteRow.ToRouting(RouteRow.ForRouting(new AgentRouting())).Default.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void RouteRow_ToRoute_BlankLightFields_AreNull(string blank)
    {
        var route = new RouteRow { ProfileId = Claude.Id, LightModel = blank, LightEffort = blank }.ToRoute();

        route.LightModel.Should().BeNull("a blank light model is unset");
        route.LightEffort.Should().BeNull("a blank light effort is unset");
        route.Should().Be(new AgentRoute(Claude.Id, "auto", "auto"));
    }

    [Fact]
    public void RouteRow_ToRoute_TrimsTheLightModel_AndKeepsAnAutoLightEffort()
    {
        var route = new RouteRow { ProfileId = Claude.Id, LightModel = $"  {LightModel}  ", LightEffort = "auto" }.ToRoute();

        route.LightModel.Should().Be(LightModel);
        route.LightEffort.Should().Be("auto", "auto is a value of the light tier, not unset");
    }

    [Fact]
    public void RouteRow_RoundTrip_KeepsLightFields()
    {
        var routing = new AgentRouting
        {
            Default = new AgentRoute(Kiro.Id, "auto", "high", LightModel, "medium"),
            ByTaskType = new Dictionary<WorkItemTaskType, AgentRoute>
            {
                [WorkItemTaskType.Decomposition] = ClaudeWithLight,
                [WorkItemTaskType.Triage] = new(Claude.Id, "claude-opus-5-5", "max"),
            },
        };

        var table = RouteRow.ToRouting(RouteRow.ForRouting(routing));
        var repository = RouteRow.ToRepositoryRoutes(RouteRow.ForRepository(routing.ByTaskType));

        table.Should().BeEquivalentTo(routing);
        repository.Should().BeEquivalentTo(routing.ByTaskType);
        table.Default!.Should().Be(new AgentRoute(Kiro.Id, "auto", "high", LightModel, "medium"));
        repository[WorkItemTaskType.Triage].Should().Be(new AgentRoute(Claude.Id, "claude-opus-5-5", "max"), "an unset light tier stays null, not empty");
    }

    [Fact]
    public void LightEffort_NotShown_ForAToolWithoutEfforts()
    {
        var routing = Global with
        {
            ByTaskType = new Dictionary<WorkItemTaskType, AgentRoute>
            {
                [WorkItemTaskType.Decomposition] = ClaudeWithLight,
                [WorkItemTaskType.Triage] = new(OpenCode.Id, "auto", "auto"),
            },
        };
        var cut = RenderTable(RouteRow.ForRouting(routing));

        cut.FindAll("[data-testid='route-light-effort-triage']").Should().BeEmpty("OpenCode has no effort, so no light effort");
        cut.Find("[data-testid='route-light-model-triage']").GetAttribute("list").Should().Be("route-models-triage", "the light model reuses the row's model list");
        cut.FindAll("[data-testid='route-light-effort-default'] option").Select(o => o.GetAttribute("value"))
            .Should().Equal("", "auto", "low", "medium", "high", "max");
        cut.FindAll("[data-testid='route-light-effort-decomposition'] option").Select(o => o.GetAttribute("value"))
            .Should().Equal("", "auto", "low", "medium", "high", "xhigh", "max");
        cut.FindAll("[data-testid='route-light-hint']").Should().ContainSingle();
    }

    public static TheoryData<string, string, string> LightEffortsOnAProfileChange() => new()
    {
        { "xhigh", Kiro.Id, "" },
        { "low", Kiro.Id, "low" },
        { "auto", Kiro.Id, "auto" },
        { "", Kiro.Id, "" },
        { "low", OpenCode.Id, "" },
    };

    [Theory]
    [MemberData(nameof(LightEffortsOnAProfileChange))]
    public void ProfileChange_ClearsALightEffortTheToolDoesNotTake(string lightEffort, string newProfileId, string expected)
    {
        var rows = RouteRow.ForRouting(Global);
        rows[3].LightModel = LightModel;
        var cut = RenderTable(rows);

        cut.Find("[data-testid='route-light-effort-decomposition']").Change(lightEffort);
        rows[3].LightEffort.Should().Be(lightEffort);
        cut.Find("[data-testid='route-profile-decomposition']").Change(newProfileId);

        rows[3].LightEffort.Should().Be(expected);
        rows[3].LightModel.Should().Be(LightModel, "a model does not depend on the tool's efforts");
    }

    [Fact]
    public void UseForAll_CopiesLightFields()
    {
        var rows = RouteRow.ForRepository(new Dictionary<WorkItemTaskType, AgentRoute>
        {
            [WorkItemTaskType.Review] = new(Claude.Id, "claude-opus-5-5", "high"),
            [WorkItemTaskType.Triage] = new(Kiro.Id, "auto", "low", "claude-sonnet-4", "max"),
        });
        var cut = RenderTable(rows, Global);
        cut.Find("[data-testid='route-light-model-review']").Change($"  {LightModel} ");
        cut.Find("[data-testid='route-light-effort-review']").Change("low");

        cut.Find("[data-testid='route-use-for-all-review']").Click();

        RouteRow.ToRepositoryRoutes(rows).Should().HaveCount(5).And.AllSatisfy(r => r.Value.Should().Be(new AgentRoute(Claude.Id, "claude-opus-5-5", "high", LightModel, "low")));

        cut.Find("[data-testid='route-light-model-review']").Change("");
        cut.Find("[data-testid='route-light-effort-review']").Change("");
        cut.Find("[data-testid='route-use-for-all-review']").Click();

        RouteRow.ToRepositoryRoutes(rows).Should().HaveCount(5).And.AllSatisfy(r => r.Value.Should().Be(new AgentRoute(Claude.Id, "claude-opus-5-5", "high")));
    }

    [Fact]
    public void SetRoute_CopiesTheInheritedLightFields()
    {
        var inherited = Global with { ByTaskType = new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Decomposition] = ClaudeWithLight } };
        var rows = RouteRow.ForRepository(null);
        var cut = RenderTable(rows, inherited);

        cut.Find("[data-testid='route-set-decomposition']").Click();

        rows[2].Should().BeEquivalentTo(new { UseRoute = true, ProfileId = Claude.Id, LightModel, LightEffort = "low" });
        cut.Find("[data-testid='route-light-model-decomposition']").GetAttribute("value").Should().Be(LightModel);
        cut.Find("[data-testid='route-light-effort-decomposition']").GetAttribute("value").Should().Be("low");
    }

    [Fact]
    public void SetRoute_ReplacesTheLightFieldsARowHeldBefore_WhenTheInheritedRouteHasNone()
    {
        var rows = RouteRow.ForRepository(null);
        var cut = RenderTable(rows, Global);
        cut.Find("[data-testid='route-set-implementation']").Click();
        cut.Find("[data-testid='route-light-model-implementation']").Change(LightModel);
        cut.Find("[data-testid='route-light-effort-implementation']").Change("low");
        rows[0].Should().BeEquivalentTo(new { LightModel, LightEffort = "low" });
        cut.Find("[data-testid='route-inherit-button-implementation']").Click();

        cut.Find("[data-testid='route-set-implementation']").Click();

        rows[0].Should().BeEquivalentTo(new { UseRoute = true, ProfileId = Kiro.Id, LightModel = "", LightEffort = "" });
        cut.Find("[data-testid='route-light-model-implementation']").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("[data-testid='route-light-effort-implementation']").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void SetRoute_OnTheRoutingPage_CopiesTheDefaultRowsLightFields()
    {
        var rows = RouteRow.ForRouting(new AgentRouting { Default = new AgentRoute(Kiro.Id, "auto", "high", LightModel, "low") });
        var cut = RenderTable(rows);

        cut.Find("[data-testid='route-set-review']").Click();

        rows[2].Should().BeEquivalentTo(new { UseRoute = true, ProfileId = Kiro.Id, LightModel, LightEffort = "low" });
    }
}
