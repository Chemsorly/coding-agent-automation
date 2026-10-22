using AwesomeAssertions;
using Bunit;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Spec 053 req 3.4, 5.1, 5.2: the Pipelines page shows each template's resolved routes in one line, and the label
/// preview lists the quality gates and reviewers the stack labels pick, and no agent profile.
/// </summary>
public class TemplateTableSectionLabelPreviewTests : BunitContext
{
    private const string RepoProviderId = "rp-preview-1";
    private const string TemplateId = "t-preview-1";

    public TemplateTableSectionLabelPreviewTests()
    {
        Services.AddSingleton(CodingAgent.Web.UnitTests.TestUtilities.UiAgentClientStub.With().Object);
    }

    private static UiResolvedRoute Route(WorkItemTaskType taskType, string profile, string model) =>
        new(taskType, profile, JobTemplateProviderType.Kiro, model, "auto", AgentRouteSource.GlobalDefault, "dotnet,kiro", null);

    private IRenderedComponent<TemplateTableSection> RenderSection(
        IReadOnlyList<string>? stackLabels = null,
        IReadOnlyList<QualityGateConfiguration>? qualityGateConfigs = null,
        IReadOnlyList<UiResolvedRoute>? routes = null) =>
        Render<TemplateTableSection>(p => p
            .Add(s => s.Templates, new List<PipelineJobTemplate>
            {
                new() { Id = TemplateId, Name = "Preview Template", IssueProviderId = "ip-prev-1", RepoProviderId = RepoProviderId, Enabled = true, RequiredLabels = stackLabels }
            })
            .Add(s => s.Projects, new List<PipelineProject>
            {
                new() { Id = "proj-prev-1", Name = "Project", TemplateIds = [TemplateId] }
            })
            .Add(s => s.IssueProviders, [new ProviderConfig { Id = "ip-prev-1", DisplayName = "Issues", Kind = ProviderKind.Issue, ProviderType = "GitHub" }])
            .Add(s => s.RepoProviders, [new ProviderConfig { Id = RepoProviderId, DisplayName = "Repo", Kind = ProviderKind.Repository, ProviderType = "GitHub" }])
            .Add(s => s.BrainProviders, [])
            .Add(s => s.PipelineProviders, [])
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.RecentlyToggled, new HashSet<string>())
            .Add(s => s.TemplateStatuses, new Dictionary<string, ConfigStatusSnapshot>())
            .Add(s => s.QualityGateConfigs, qualityGateConfigs ?? [])
            .Add(s => s.ReviewerConfigs, [])
            .Add(s => s.Routes, new Dictionary<string, IReadOnlyList<UiResolvedRoute>> { [TemplateId] = routes ?? [] }));

    [Fact]
    public void GetLabelPreview_ListsTheQualityGatesTheStackLabelsPick_AndThoseWithoutMatchLabels()
    {
        var dotnet = new QualityGateConfiguration { DisplayName = ".NET", MatchLabels = ["dotnet"], Enabled = true };
        var python = new QualityGateConfiguration { DisplayName = "Python", MatchLabels = ["python"], Enabled = true };
        var always = new QualityGateConfiguration { DisplayName = "Secrets scan", MatchLabels = [], Enabled = true };
        var cut = RenderSection(["dotnet", "dotnet10"], [dotnet, python, always]);

        var result = cut.Instance.GetLabelPreview(["dotnet", "dotnet10"]);

        result.QualityGates.Should().BeEquivalentTo(".NET", "Secrets scan");
    }

    [Fact]
    public void TheOpenPreview_NamesNoAgentProfileAndNoAgents()
    {
        var cut = RenderSection(["dotnet"], [new QualityGateConfiguration { DisplayName = ".NET", MatchLabels = ["dotnet"], Enabled = true }]);

        cut.Find(".btn-label-preview").Click();

        var preview = cut.Find(".label-preview-inline").TextContent;
        preview.Should().Contain(".NET").And.NotContain("Agent Profiles").And.NotContain("Agents:");
    }

    [Fact]
    public void ATemplateWithoutStackLabels_OffersNoPreview()
    {
        var cut = RenderSection(stackLabels: null);

        cut.FindAll(".btn-label-preview").Should().BeEmpty();
    }

    [Fact]
    public void TheRow_ShowsTheResolvedRoutesInOneLine()
    {
        var cut = RenderSection(routes:
        [
            Route(WorkItemTaskType.Implementation, "Kiro", "sonnet"),
            Route(WorkItemTaskType.Review, "Kiro", "sonnet"),
            Route(WorkItemTaskType.Decomposition, "Claude", "opus"),
            Route(WorkItemTaskType.Triage, "Claude", "opus"),
            Route(WorkItemTaskType.Consolidation, "Kiro", "sonnet"),
        ]);

        cut.Find("[data-testid='template-routes']").TextContent.Trim().Should().Be("Kiro · sonnet; Decomposition, Triage: Claude · opus");
        cut.FindAll("[data-testid='template-route-problem']").Should().BeEmpty();
    }

    [Fact]
    public void ARouteThatDoesNotResolve_IsMarked()
    {
        UiResolvedRoute NoRoute(WorkItemTaskType t) => new(t, null, null, null, null, null, null, "No agent route: set the default route in Settings → Agents → Routing");
        var cut = RenderSection(routes: AgentRouteLabels.TaskTypes.Select(NoRoute).ToList());

        cut.Find("[data-testid='template-routes']").TextContent.Should().Contain("no route");
        cut.Find("[data-testid='template-route-problem']").GetAttribute("title").Should().Contain("Implementation: No agent route");
    }
}
