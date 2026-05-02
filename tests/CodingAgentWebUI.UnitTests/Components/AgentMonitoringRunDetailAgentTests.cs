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

/// <summary>
/// bUnit tests for the agent info section within the run detail modal on the Agent Monitoring page.
/// </summary>
public class AgentMonitoringRunDetailAgentTests : BunitContext
{
    private readonly PipelineOrchestrationService _pipelineService;
    private readonly AgentRegistryService _registry;
    private readonly Mock<IPipelineRunHistoryService> _mockHistory;

    public AgentMonitoringRunDetailAgentTests()
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
        _mockHistory = new Mock<IPipelineRunHistoryService>();
        _mockHistory.Setup(h => h.GetRunHistory()).Returns(Array.Empty<PipelineRunSummary>());
        _mockHistory.Setup(h => h.GetRunsByAgentId(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(Array.Empty<PipelineRunSummary>());

        _pipelineService = new PipelineOrchestrationService(
            mockStore.Object, mockFactory.Object, new IssueDescriptionParser(),
            mockValidator.Object, new CiLogWriter(mockLogger.Object), mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: _mockHistory.Object);

        _registry = new AgentRegistryService(mockLogger.Object);

        Services.AddSingleton(_pipelineService);
        Services.AddSingleton(_registry);
        Services.AddSingleton(new JobDispatcherService(_registry, mockLogger.Object));
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton(mockStore.Object);
        Services.AddSingleton(_mockHistory.Object);
        Services.AddSingleton(new Mock<IHubContext<AgentHub, IAgentHubClient>>().Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
    }

    [Fact]
    public void RunDetailModal_ShowsAgentIdentity_WhenAgentRegistered()
    {
        RegisterAgent("agent-1");
        SetActiveRun(CreateRun(agentId: "agent-1"));

        var cut = Render<AgentMonitoring>();
        OpenRunDetailModal(cut);

        Assert.Contains("agent-1", cut.Markup);
        Assert.Contains("worker-node-1", cut.Markup);
        Assert.Contains("KiroCli", cut.Markup);
    }

    [Fact]
    public void RunDetailModal_ShowsAgentLabels_WhenAgentRegistered()
    {
        RegisterAgent("agent-1");
        SetActiveRun(CreateRun(agentId: "agent-1"));

        var cut = Render<AgentMonitoring>();
        OpenRunDetailModal(cut);

        Assert.Contains("dotnet", cut.Markup);
        Assert.Contains("csharp", cut.Markup);
    }

    [Fact]
    public void RunDetailModal_ShowsAgentStatus_WhenAgentRegistered()
    {
        RegisterAgent("agent-1");
        SetActiveRun(CreateRun(agentId: "agent-1"));

        var cut = Render<AgentMonitoring>();
        OpenRunDetailModal(cut);

        Assert.Contains("Idle", cut.Markup);
        Assert.Contains("Last Heartbeat", cut.Markup);
    }

    [Fact]
    public void RunDetailModal_ShowsAgentUnavailable_WhenAgentNotRegistered()
    {
        SetActiveRun(CreateRun(agentId: "ghost-agent"));

        var cut = Render<AgentMonitoring>();
        OpenRunDetailModal(cut);

        Assert.Contains("Agent unavailable", cut.Markup);
    }

    [Fact]
    public void RunDetailModal_ShowsLocalRun_WhenNoAgentId()
    {
        SetActiveRun(CreateRun(agentId: null));

        var cut = Render<AgentMonitoring>();
        OpenRunDetailModal(cut);

        Assert.Contains("Local run", cut.Markup);
    }

    [Fact]
    public void RunDetailModal_ShowsJobHistory_WhenHistoryExists()
    {
        RegisterAgent("agent-1");
        var runs = new[]
        {
            new PipelineRunSummary
            {
                RunId = "run-1", IssueIdentifier = "42", IssueTitle = "Test",
                FinalStep = PipelineStep.Completed, StartedAt = DateTime.UtcNow.AddHours(-1),
                CompletedAt = DateTime.UtcNow.AddMinutes(-30), AgentId = "agent-1"
            }
        };
        _mockHistory.Setup(h => h.GetRunsByAgentId("agent-1", 10)).Returns(runs);
        SetActiveRun(CreateRun(agentId: "agent-1"));

        var cut = Render<AgentMonitoring>();
        OpenRunDetailModal(cut);

        Assert.Contains("Total: 1", cut.Markup);
        Assert.Contains("Success: 100%", cut.Markup);
        Assert.Contains("#42", cut.Markup);
    }

    [Fact]
    public void RunDetailModal_ShowsDisableButton_WhenAgentEnabled()
    {
        RegisterAgent("agent-1");
        SetActiveRun(CreateRun(agentId: "agent-1"));

        var cut = Render<AgentMonitoring>();
        OpenRunDetailModal(cut);

        Assert.Contains("Disable", cut.Markup);
        Assert.Contains("Force Disconnect", cut.Markup);
    }

    [Fact]
    public void RunDetailModal_ShowsEnableButton_WhenAgentDisabled()
    {
        var agent = RegisterAgent("agent-1");
        agent.Disabled = true;
        SetActiveRun(CreateRun(agentId: "agent-1"));

        var cut = Render<AgentMonitoring>();
        OpenRunDetailModal(cut);

        Assert.Contains("Enable", cut.Markup);
    }

    private AgentEntry RegisterAgent(string agentId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "worker-node-1",
            AgentType = "KiroCli",
            Labels = ["dotnet", "csharp"]
        }, "conn-abc123");
    }

    private static PipelineRun CreateRun(string? agentId) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N")[..8],
        IssueIdentifier = "100",
        IssueTitle = "Test Issue",
        CurrentStep = PipelineStep.GeneratingCode,
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        AgentId = agentId
    };

    private void SetActiveRun(PipelineRun run)
    {
        var prop = typeof(PipelineOrchestrationService).GetProperty("ActiveRun")!;
        prop.SetValue(_pipelineService, run);
    }

    private static void OpenRunDetailModal(IRenderedComponent<AgentMonitoring> cut)
    {
        var row = cut.Find("tr.monitoring-row-clickable");
        row.Click();
    }
}
