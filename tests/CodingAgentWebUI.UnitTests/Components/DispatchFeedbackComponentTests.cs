using Bunit;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit tests for dispatch visual feedback on the AgentCoding page.
/// Covers: button state during dispatch, success message, dispatched indicator on issue cards.
/// </summary>
public class DispatchFeedbackComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IJobDispatcher> _mockJobDispatcher;

    public DispatchFeedbackComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockJobDispatcher = new Mock<IJobDispatcher>();

        var mockLogger = new Mock<Serilog.ILogger>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistory()).Returns(Array.Empty<PipelineRunSummary>());

        var pipelineService = new PipelineOrchestrationService(
            _mockStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            mockValidator.Object,
            new CiLogWriter(mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistoryService.Object);

        SetupDefaults();

        Services.AddSingleton(pipelineService);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton(new PipelineLoopService(pipelineService, _mockFactory.Object, _mockStore.Object, mockLogger.Object));
        Services.AddSingleton(new Mock<IJSRuntime>().Object);

        var registry = new AgentRegistryService(mockLogger.Object);
        Services.AddSingleton(registry);
        Services.AddSingleton(new JobDispatcherService(registry, mockLogger.Object));
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton<IJobDispatcher>(_mockJobDispatcher.Object);
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ap-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Kiro Agent" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() });
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());

        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "42", Title = "Test Issue", Labels = Array.Empty<string>() },
                    new() { Identifier = "43", Title = "Bug Fix", Labels = new[] { "bug" } }
                },
                Page = 1, PageSize = 25, HasMore = false
            });

        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new IssueDetail
            {
                Identifier = id, Title = "Test Issue", Description = "", Labels = Array.Empty<string>()
            });

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockIssueProvider.Object);
        _mockRepoProvider.Setup(r => r.GetAgentPullRequestsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LinkedPullRequest>());
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockRepoProvider.Object);
    }

    [Fact]
    public async Task StartButton_ShowsDispatchingState_WhileInFlight()
    {
        var tcs = new TaskCompletionSource<bool>();
        _mockJobDispatcher.Setup(d => d.TryDispatchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var component = Render<AgentCoding>();
        component.WaitForAssertion(() => Assert.Contains("#42", component.Markup), timeout: TimeSpan.FromSeconds(5));

        // Select an issue
        var issueCard = component.Find(".issue-card");
        await component.InvokeAsync(() => issueCard.Click());
        component.WaitForAssertion(() => Assert.Contains("Selected Issue", component.Markup), timeout: TimeSpan.FromSeconds(5));

        // Click Start Pipeline
        var startBtn = component.Find(".pipeline-start-btn");
        await component.InvokeAsync(() => startBtn.Click());

        // Button should show dispatching state
        component.WaitForAssertion(() => Assert.Contains("Dispatching", component.Markup), timeout: TimeSpan.FromSeconds(5));
        var btn = component.Find(".pipeline-start-btn");
        Assert.True(btn.HasAttribute("disabled"));

        // Complete the dispatch
        tcs.SetResult(true);
    }

    [Fact]
    public async Task StartButton_ShowsDispatchedState_AfterSuccessfulDispatch()
    {
        _mockJobDispatcher.Setup(d => d.TryDispatchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var component = Render<AgentCoding>();
        component.WaitForAssertion(() => Assert.Contains("#42", component.Markup), timeout: TimeSpan.FromSeconds(5));

        var issueCard = component.Find(".issue-card");
        await component.InvokeAsync(() => issueCard.Click());
        component.WaitForAssertion(() => Assert.Contains("Selected Issue", component.Markup), timeout: TimeSpan.FromSeconds(5));

        var startBtn = component.Find(".pipeline-start-btn");
        await component.InvokeAsync(() => startBtn.Click());

        // Button should show dispatched state
        component.WaitForAssertion(() => Assert.Contains("Dispatched", component.Markup), timeout: TimeSpan.FromSeconds(5));
        var btn = component.Find(".pipeline-start-btn");
        Assert.True(btn.HasAttribute("disabled"));
    }

    [Fact]
    public async Task SuccessMessage_AppearsAfterDispatch()
    {
        _mockJobDispatcher.Setup(d => d.TryDispatchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var component = Render<AgentCoding>();
        component.WaitForAssertion(() => Assert.Contains("#42", component.Markup), timeout: TimeSpan.FromSeconds(5));

        var issueCard = component.Find(".issue-card");
        await component.InvokeAsync(() => issueCard.Click());
        component.WaitForAssertion(() => Assert.Contains("Selected Issue", component.Markup), timeout: TimeSpan.FromSeconds(5));

        var startBtn = component.Find(".pipeline-start-btn");
        await component.InvokeAsync(() => startBtn.Click());

        component.WaitForAssertion(() => Assert.Contains("status-success", component.Markup), timeout: TimeSpan.FromSeconds(5));
        Assert.Contains("Dispatched #42", component.Markup);
    }

    [Fact]
    public async Task FailedDispatch_ShowsErrorMessage_NotSuccessMessage()
    {
        _mockJobDispatcher.Setup(d => d.TryDispatchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var component = Render<AgentCoding>();
        component.WaitForAssertion(() => Assert.Contains("#42", component.Markup), timeout: TimeSpan.FromSeconds(5));

        var issueCard = component.Find(".issue-card");
        await component.InvokeAsync(() => issueCard.Click());
        component.WaitForAssertion(() => Assert.Contains("Selected Issue", component.Markup), timeout: TimeSpan.FromSeconds(5));

        var startBtn = component.Find(".pipeline-start-btn");
        await component.InvokeAsync(() => startBtn.Click());

        component.WaitForAssertion(() => Assert.Contains("Could not dispatch", component.Markup), timeout: TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("status-success", component.Markup);
    }

    [Fact]
    public void IssueCard_ShowsDispatchedBadge_WhenProcessingOrQueued()
    {
        _mockJobDispatcher.Setup(d => d.IsIssueBeingProcessedOrQueued("42")).Returns(true);
        _mockJobDispatcher.Setup(d => d.IsIssueBeingProcessedOrQueued("43")).Returns(false);

        var component = Render<AgentCoding>();
        component.WaitForAssertion(() => Assert.Contains("#42", component.Markup), timeout: TimeSpan.FromSeconds(5));

        // Issue 42 should have the dispatched class and badge
        var cards = component.FindAll(".issue-card");
        var card42 = cards.First(c => c.InnerHtml.Contains("#42"));
        Assert.Contains("issue-dispatched", card42.GetAttribute("class"));
        Assert.Contains("🔄", card42.InnerHtml);

        // Issue 43 should not
        var card43 = cards.First(c => c.InnerHtml.Contains("#43"));
        Assert.DoesNotContain("issue-dispatched", card43.GetAttribute("class") ?? "");
        Assert.DoesNotContain("🔄", card43.InnerHtml);
    }

    [Fact]
    public async Task DispatchedState_ResetsWhenSelectingDifferentIssue()
    {
        _mockJobDispatcher.Setup(d => d.TryDispatchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var component = Render<AgentCoding>();
        component.WaitForAssertion(() => Assert.Contains("#42", component.Markup), timeout: TimeSpan.FromSeconds(5));

        // Select issue 42 and dispatch
        var issueCard42 = component.FindAll(".issue-card").First(c => c.InnerHtml.Contains("#42"));
        await component.InvokeAsync(() => issueCard42.Click());
        component.WaitForAssertion(() => Assert.Contains("Selected Issue", component.Markup), timeout: TimeSpan.FromSeconds(5));

        var startBtn = component.Find(".pipeline-start-btn");
        await component.InvokeAsync(() => startBtn.Click());
        component.WaitForAssertion(() => Assert.Contains("✅ Dispatched", component.Markup), timeout: TimeSpan.FromSeconds(5));

        // Select a different issue
        var issueCard43 = component.FindAll(".issue-card").First(c => c.InnerHtml.Contains("#43"));
        await component.InvokeAsync(() => issueCard43.Click());

        // Button should revert to normal text
        component.WaitForAssertion(() => Assert.Contains("Start Pipeline on Issue", component.Markup), timeout: TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("✅ Dispatched", component.Markup);
    }
}
