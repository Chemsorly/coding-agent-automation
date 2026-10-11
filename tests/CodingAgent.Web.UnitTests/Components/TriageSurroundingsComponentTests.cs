using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using CodingAgent.Web.UnitTests.Auth;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using static CodingAgent.Web.UnitTests.Components.TriageTestData;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>Triage outside its own pages: Attention, the run page link and the run's step list.</summary>
public class TriageSurroundingsComponentTests : BunitContext
{
    [Fact]
    public void Attention_ListsTriagesThatNeedSomeone()
    {
        var triages = new Mock<IPipelineApiTriageClient>();
        triages.Setup(t => t.ListAsync(It.Is<TriageListQuery>(q => q.Tab == TriageListTab.NeedYou), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriageListPage
            {
                Items = [ListItem(TriageStatus.NeedsReview, "Orders stuck", drafts: 2), ListItem(TriageStatus.NeedsInput, "Slow login")],
                Total = 2,
            });
        var history = new Mock<IPipelineApiRunHistoryClient>();
        history.Setup(h => h.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = [], Page = 1, PageSize = 100, HasMore = false });
        var state = new CockpitState();
        Services.AddTestAccess();
        Services.AddSingleton(triages.Object);
        Services.AddSingleton(history.Object);
        Services.AddSingleton(state);
        Services.AddSingleton(new BlockedIssuesService(Mock.Of<IPipelineApiConfigClient>(), Mock.Of<IProviderFactory>(), Mock.Of<IDependencyChecker>()));
        Services.AddSingleton(Mock.Of<IJSRuntime>());

        var cut = Render<Attention>();

        cut.Find("[data-testid='attention-section-count-triage']").TextContent.Should().Be("2");
        var rows = cut.FindAll("[data-testid='attention-triage-row']");
        rows[0].TextContent.Should().Contain("Orders stuck").And.Contain("review 2 issue drafts");
        rows[1].TextContent.Should().Contain("The agent has questions");
        state.AttentionCount.Should().Be(2);
    }

    [Fact]
    public void RunPage_TriageRun_LinksItsTriage()
    {
        var runId = Guid.NewGuid();
        var history = new Mock<IPipelineApiRunHistoryClient>();
        history.Setup(h => h.GetRunAsync(runId, It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineRunSummary
        {
            RunId = runId.ToString(),
            IssueIdentifier = TriageConstants.IssueIdentifierFor(TriageId),
            IssueTitle = "Orders stuck in Pending",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Triage,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            ProjectId = ShopProjectId,
        });
        var triages = new Mock<IPipelineApiTriageClient>();
        triages.Setup(t => t.FindByRunAsync(runId.ToString(), It.IsAny<CancellationToken>())).ReturnsAsync(TriageId);
        var hub = new Mock<IAgentHubConnection>();
        hub.Setup(h => h.State).Returns(HubConnectionState.Disconnected);
        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineConfiguration());
        Services.AddTestAccess();
        Services.AddSingleton(history.Object);
        Services.AddSingleton(triages.Object);
        Services.AddSingleton(hub.Object);
        Services.AddSingleton(Mock.Of<IPipelineApiWorkItemClient>());
        Services.AddSingleton(config.Object);
        Services.AddSingleton(Mock.Of<IConfigurationStore>());
        Services.AddSingleton(new CockpitState());
        Services.AddSingleton(Mock.Of<IJSRuntime>());

        var cut = Render<RunPage>(p => p.Add(c => c.RunId, runId.ToString()));

        cut.Find("[data-testid='run-triage-link']").GetAttribute("href").Should().Be($"triage/{TriageId}");
        cut.Find("h1").TextContent.Should().Be("Orders stuck in Pending", "an operator triage has no issue number to show");
    }

    private static PipelineRun TriageRun(PipelineStep current, PipelineStep highWaterMark) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "431",
        IssueTitle = "Orders stuck",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        StartedAt = DateTime.UtcNow.AddMinutes(-3),
        RunType = PipelineRunType.Triage,
        CurrentStep = current,
        HighWaterMark = highWaterMark,
    };

    [Fact]
    public void Sidebar_TriageRun_ShowsTheTriageSteps()
    {
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, TriageRun(PipelineStep.Investigating, PipelineStep.Investigating)).Add(s => s.IsRunning, true));

        cut.FindAll("[data-testid='pipeline-step-DownloadingOpenIssues']").Should().HaveCount(1, "it appears once, in Preparation");
        cut.Find("[data-testid='phase-preparation']").TextContent.Should().Contain("Downloading Issues");
        cut.Find("[data-testid='phase-investigation']").GetAttribute("class").Should().Contain("phase-group-active");
        cut.Find("[data-testid='phase-rca-review']");
        cut.Find("[data-testid='phase-finalization']").TextContent.Should().Contain("Reporting RCA");
        cut.FindAll("[data-testid='pipeline-step-CreatingBranch']").Should().BeEmpty();
        cut.FindAll("[data-testid='pipeline-step-GeneratingCode']").Should().BeEmpty();
        cut.FindAll("[data-testid='phase-decomposition-analysis']").Should().BeEmpty();
    }

    [Fact]
    public void Sidebar_TriageRunWithoutReview_HidesTheReviewStep()
    {
        var run = TriageRun(PipelineStep.ReportingRca, PipelineStep.ReportingRca);
        run.Metrics.PhaseBreakdown["triage"] = new PhaseUsage(1000, null);

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        cut.FindAll("[data-testid='phase-rca-review']").Should().BeEmpty();
    }

    [Fact]
    public void Sidebar_OtherRuns_DoNotShowTheTriageSteps()
    {
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "12",
            IssueTitle = "Epic",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            RunType = PipelineRunType.DecompositionAnalysis,
            CurrentStep = PipelineStep.DownloadingOpenIssues,
            HighWaterMark = PipelineStep.DownloadingOpenIssues,
        };

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        cut.FindAll("[data-testid='pipeline-step-DownloadingOpenIssues']").Should().HaveCount(1, "epics keep it in Decomposition Analysis only");
        cut.Find("[data-testid='phase-decomposition-analysis']").TextContent.Should().Contain("Downloading Issues");
        cut.FindAll("[data-testid='phase-investigation']").Should().BeEmpty();
        cut.FindAll("[data-testid='pipeline-step-ReportingRca']").Should().BeEmpty();
    }
}
