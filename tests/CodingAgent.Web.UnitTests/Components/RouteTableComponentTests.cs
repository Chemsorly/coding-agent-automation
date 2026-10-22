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
    private static readonly UiRoutingProfile Kiro = new("p-kiro", "Kiro dotnet", JobTemplateProviderType.Kiro, "dotnet,kiro", true);
    private static readonly UiRoutingProfile Claude = new("p-claude", "Claude dotnet", JobTemplateProviderType.Claude, "claude,dotnet", true);
    private static readonly UiRoutingProfile OpenCode = new("p-opencode", "OpenCode python", JobTemplateProviderType.Opencode, "opencode,python", true);
    private static readonly UiRoutingProfile Retired = new("p-retired", "Retired", JobTemplateProviderType.Kiro, "dotnet,kiro", false);
    private static readonly UiRoutingProfile[] Profiles = [Kiro, Claude, OpenCode, Retired];

    private static readonly AgentRouting Global = new()
    {
        Default = new AgentRoute(Kiro.Id, "auto", "high"),
        ByTaskType = new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Decomposition] = new(Claude.Id, "claude-opus-5-5", "max") },
    };

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
    public void ADisabledProfile_IsOfferedOnlyOnTheRowThatNamesIt()
    {
        var routing = Global with { ByTaskType = new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Triage] = new(Retired.Id, "auto", "auto") } };
        var cut = RenderTable(RouteRow.ForRouting(routing));

        cut.FindAll("[data-testid='route-profile-triage'] option").Select(o => o.TextContent).Should().Contain("Retired (Kiro, disabled)");
        cut.FindAll("[data-testid='route-profile-default'] option").Select(o => o.GetAttribute("value")).Should().NotContain(Retired.Id);
    }

    [Fact]
    public void TheRows_RoundTripTheTable_AndADefaultRowWithoutProfileIsNoDefault()
    {
        RouteRow.ToRouting(RouteRow.ForRouting(Global)).Should().BeEquivalentTo(Global);

        RouteRow.ToRouting(RouteRow.ForRouting(new AgentRouting())).Default.Should().BeNull();
    }
}
