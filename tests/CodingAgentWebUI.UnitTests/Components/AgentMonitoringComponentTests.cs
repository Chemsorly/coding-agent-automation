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
            mockStore.Object, mockFactory.Object, new IssueDescriptionParser(),
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
    public void ActiveRunsTable_HasColgroup()
    {
        SetActiveRun(CreateRun("Title"));

        var cut = Render<AgentMonitoring>();

        var colgroups = cut.FindAll("colgroup");
        Assert.NotEmpty(colgroups);

        var cols = colgroups[0].QuerySelectorAll("col");
        Assert.Equal(7, cols.Length);
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
