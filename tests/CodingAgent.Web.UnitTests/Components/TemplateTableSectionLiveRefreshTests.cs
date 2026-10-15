using Bunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// The page re-renders on its loop-status tick (spec 051 D11), which redraws "polled Xs ago". The table
/// runs its own timer only while it shows agents — an open label preview or the add form — to read them
/// again from /api/ui/agents.
/// </summary>
public class TemplateTableSectionLiveRefreshTests : BunitContext
{
    private const string TemplateId = "t-live-1";
    private const string RepoProviderId = "rp-live-1";

    public TemplateTableSectionLiveRefreshTests()
    {
        Services.AddSingleton(CodingAgent.Web.UnitTests.TestUtilities.UiAgentClientStub.With().Object);
    }

    private IRenderedComponent<TemplateTableSection> RenderSection(bool isLoopActive, DateTimeOffset? lastPoll = null) =>
        Render<TemplateTableSection>(p => p
            .Add(s => s.Templates, new List<PipelineJobTemplate>
            {
                new() { Id = TemplateId, Name = "Live Template", IssueProviderId = "ip-live-1", RepoProviderId = RepoProviderId, Enabled = true }
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
            .Add(s => s.ReviewerConfigs, [])
            .Add(s => s.AgentProfiles, [])
            // Gives the repo provider required labels, so the row offers a label preview.
            .Add(s => s.PipelineConfig, new PipelineConfiguration { DefaultRequiredAgentLabels = "dotnet" }));

    [Fact]
    public void LoopIdle_NoPreviewOpen_RunsNoTimer()
    {
        var cut = RenderSection(isLoopActive: false);

        Assert.Empty(cut.FindComponents<AutoRefresh>());
    }

    [Fact]
    public void LoopActive_NoPreviewOpen_RunsNoTimer()
    {
        // The page re-renders on its loop-status tick, which redraws "polled Xs ago".
        var cut = RenderSection(isLoopActive: true);

        Assert.Empty(cut.FindComponents<AutoRefresh>());
    }

    [Fact]
    public void LabelPreviewOpen_RunsTimerUntilClosed()
    {
        var cut = RenderSection(isLoopActive: false);

        cut.Find(".btn-label-preview").Click();
        Assert.Single(cut.FindComponents<AutoRefresh>());

        cut.Find(".btn-label-preview").Click();
        Assert.Empty(cut.FindComponents<AutoRefresh>());
    }

    [Fact]
    public void LabelPreviewOpened_ReadsTheAgentsAgain()
    {
        var agents = CodingAgent.Web.UnitTests.TestUtilities.UiAgentClientStub.With();
        Services.AddSingleton(agents.Object);
        var cut = RenderSection(isLoopActive: false);

        cut.Find(".btn-label-preview").Click();

        // Once on first render, once when the preview opens.
        agents.Verify(a => a.GetAgentsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
