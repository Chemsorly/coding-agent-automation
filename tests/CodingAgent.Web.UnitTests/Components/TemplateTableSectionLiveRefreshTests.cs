using Bunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// The page re-renders on its loop-status tick (spec 051 D11), which redraws "polled Xs ago". The table runs no timer
/// of its own: since spec 053 the label preview lists quality gates and reviewers only, so it reads no agents.
/// </summary>
public class TemplateTableSectionLiveRefreshTests : BunitContext
{
    private const string TemplateId = "t-live-1";
    private const string RepoProviderId = "rp-live-1";

    private readonly Mock<CodingAgent.Web.Ui.IUiAgentClient> _agents = CodingAgent.Web.UnitTests.TestUtilities.UiAgentClientStub.With();

    public TemplateTableSectionLiveRefreshTests()
    {
        Services.AddSingleton(_agents.Object);
    }

    private IRenderedComponent<TemplateTableSection> RenderSection(bool isLoopActive, DateTimeOffset? lastPoll = null) =>
        Render<TemplateTableSection>(p => p
            .Add(s => s.Templates, new List<PipelineJobTemplate>
            {
                // Stack labels, so the row offers a label preview.
                new() { Id = TemplateId, Name = "Live Template", IssueProviderId = "ip-live-1", RepoProviderId = RepoProviderId, Enabled = true, RequiredLabels = ["dotnet"] }
            })
            .Add(s => s.Projects, new List<PipelineProject> { new() { Id = "proj-live-1", Name = "Project", TemplateIds = [TemplateId] } })
            .Add(s => s.IssueProviders, [new ProviderConfig { Id = "ip-live-1", DisplayName = "Issues", Kind = ProviderKind.Issue, ProviderType = "GitHub" }])
            .Add(s => s.RepoProviders, [new ProviderConfig { Id = RepoProviderId, DisplayName = "Repo", Kind = ProviderKind.Repository, ProviderType = "GitHub" }])
            .Add(s => s.BrainProviders, [])
            .Add(s => s.PipelineProviders, [])
            .Add(s => s.IsLoopActive, isLoopActive)
            .Add(s => s.RecentlyToggled, new HashSet<string>())
            .Add(s => s.TemplateStatuses, new Dictionary<string, ConfigStatusSnapshot>
            {
                [TemplateId] = new() { LastPollTime = lastPoll ?? DateTimeOffset.UtcNow, LastPollIssueCount = 2 }
            })
            .Add(s => s.QualityGateConfigs, [])
            .Add(s => s.ReviewerConfigs, []));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTable_RunsNoTimer(bool isLoopActive)
    {
        var cut = RenderSection(isLoopActive);

        Assert.Empty(cut.FindComponents<AutoRefresh>());
    }

    [Fact]
    public void AnOpenLabelPreview_RunsNoTimer_AndReadsNoAgents()
    {
        var cut = RenderSection(isLoopActive: false);

        cut.Find(".btn-label-preview").Click();

        Assert.Single(cut.FindAll("tr.label-preview-row"));
        Assert.Empty(cut.FindComponents<AutoRefresh>());
        _agents.Verify(a => a.GetAgentsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
