using Bunit;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// The page re-renders only when the loop status changes, so TemplateTableSection redraws the values
/// that move on their own — "polled Xs ago" while the loop runs, and the agents matched in an open
/// label preview — with its own timer, and runs that timer only while such values are on screen.
/// </summary>
public class TemplateTableSectionLiveRefreshTests : BunitContext
{
    private const string TemplateId = "t-live-1";
    private const string RepoProviderId = "rp-live-1";

    public TemplateTableSectionLiveRefreshTests()
    {
        Services.AddSingleton<IAgentRegistryService>(new AgentRegistryService(new Mock<ILogger>().Object));
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
    public void LoopActive_RunsTimer()
    {
        var cut = RenderSection(isLoopActive: true);

        Assert.Single(cut.FindComponents<AutoRefresh>());
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
    public void LoopActive_PollAgeAdvancesWithoutParentRender()
    {
        var cut = RenderSection(isLoopActive: true, lastPoll: DateTimeOffset.UtcNow.AddSeconds(-10));
        var initial = cut.Find(".monitoring-status-idle").TextContent;

        // Only the component's own timer can redraw it here; the test never re-renders it.
        cut.WaitForAssertion(
            () => Assert.NotEqual(initial, cut.Find(".monitoring-status-idle").TextContent),
            TimeSpan.FromSeconds(10));
    }
}
