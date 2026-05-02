using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

public class AgentMonitoringComponentTests : BunitContext
{
    private readonly PipelineOrchestrationService _pipelineService;

    public AgentMonitoringComponentTests()
    {
        var mockLogger = new Mock<ILogger>();
        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());

        var mockFactory = new Mock<IProviderFactory>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistory()).Returns(Array.Empty<PipelineRunSummary>());

        _pipelineService = new PipelineOrchestrationService(
            mockStore.Object, mockStore.Object, mockStore.Object, mockStore.Object, mockFactory.Object, new IssueDescriptionParser(),
            mockValidator.Object, new CiLogWriter(mockLogger.Object), mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistory.Object);

        var registry = new AgentRegistryService(mockLogger.Object);

        Services.AddSingleton(_pipelineService);
        Services.AddSingleton(registry);
        Services.AddSingleton(new JobDispatcherService(registry, mockLogger.Object));
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton(mockStore.Object);
        Services.AddSingleton(mockHistory.Object);
        Services.AddSingleton(new Mock<IHubContext<AgentHub, IAgentHubClient>>().Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
    }

    [Fact]
    public void Renders_EmptyState_WhenNoActiveRuns()
    {
        var cut = Render<AgentMonitoring>();

        Assert.Contains("No active pipeline runs.", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyState_WhenNoAgents()
    {
        var cut = Render<AgentMonitoring>();

        Assert.Contains("No agents registered.", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyState_WhenNoQueuedJobs()
    {
        var cut = Render<AgentMonitoring>();

        Assert.Contains("No pending jobs in queue.", cut.Markup);
    }

    [Fact]
    public void Renders_AllThreeSections()
    {
        var cut = Render<AgentMonitoring>();

        Assert.Contains("Active Runs", cut.Markup);
        Assert.Contains("Registered Agents", cut.Markup);
        Assert.Contains("Job Queue", cut.Markup);
    }

    [Fact]
    public void ActiveRunsTable_DisplaysFullIssueTitle_WithoutTruncation()
    {
        var longTitle = "[ARC-07b] State machine property tests for pipeline step transitions";
        SetActiveRun(CreateRun(longTitle));

        var cut = Render<AgentMonitoring>();

        // The full title should appear in the markup (not server-side truncated)
        Assert.Contains(longTitle, cut.Markup);
    }

    [Fact]
    public void ActiveRunsTable_HasTitleAttributes_ForTooltips()
    {
        SetActiveRun(CreateRun("Test Title"));

        var cut = Render<AgentMonitoring>();

        var tdsWithTitle = cut.FindAll("td[title]");
        Assert.NotEmpty(tdsWithTitle);

        // Issue cell should have title with full text
        var issueTd = tdsWithTitle.FirstOrDefault(td => td.GetAttribute("title")?.Contains("Test Title") == true);
        Assert.NotNull(issueTd);
    }

    [Fact]
    public void ActiveRunsTable_HasSevenColumns()
    {
        SetActiveRun(CreateRun("Title"));

        var cut = Render<AgentMonitoring>();

        var headerCells = cut.FindAll(".monitoring-table thead th");
        Assert.Equal(7, headerCells.Count);
    }

    [Fact]
    public void ActiveRunsTable_RunIdCell_HasTitleWithFullId()
    {
        var run = CreateRun("Title");
        SetActiveRun(run);

        var cut = Render<AgentMonitoring>();

        var monoTds = cut.FindAll("td.monitoring-mono[title]");
        Assert.NotEmpty(monoTds);

        // The first mono td should have the full run ID as title
        Assert.Equal(run.RunId, monoTds[0].GetAttribute("title"));
    }

    [Fact]
    public void RemoveFromQueue_Button_RemovesJobAndUpdatesUI()
    {
        // Arrange: enqueue a job
        var dispatcher = Services.GetRequiredService<JobDispatcherService>();
        dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#42",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });

        var cut = Render<AgentMonitoring>();

        // Verify job appears in the queue
        Assert.Contains("org/repo#42", cut.Markup);
        Assert.Contains("Job Queue (1)", cut.Markup);

        // Act: click the Remove button
        var removeBtn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Remove"));
        removeBtn.Click();

        // Assert: job is removed from UI
        Assert.DoesNotContain("org/repo#42", cut.Markup);
        Assert.Contains("No pending jobs in queue.", cut.Markup);
    }

    [Fact]
    public void RemoveFromQueue_Button_RemovesCorrectJob_WhenMultipleQueued()
    {
        // Arrange: enqueue two jobs
        var dispatcher = Services.GetRequiredService<JobDispatcherService>();
        dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#10",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "loop"
        });
        dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#20",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "loop"
        });

        var cut = Render<AgentMonitoring>();
        Assert.Contains("Job Queue (2)", cut.Markup);

        // Act: click the Remove button for the first job
        var removeButtons = cut.FindAll("button.btn-cancel-small")
            .Where(b => b.TextContent.Trim() == "Remove")
            .ToList();
        removeButtons[0].Click();

        // Assert: first job removed, second remains
        Assert.DoesNotContain("org/repo#10", cut.Markup);
        Assert.Contains("org/repo#20", cut.Markup);
        Assert.Contains("Job Queue (1)", cut.Markup);
    }

    private static PipelineRun CreateRun(string issueTitle) => new()
    {
        RunId = "abcd1234-5678-9012-3456-789012345678",
        IssueIdentifier = "194",
        IssueTitle = issueTitle,
        CurrentStep = PipelineStep.GeneratingCode,
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1"
    };

    private void SetActiveRun(PipelineRun run)
    {
        var prop = typeof(PipelineOrchestrationService).GetProperty("ActiveRun")!;
        prop.SetValue(_pipelineService, run);
    }
}
