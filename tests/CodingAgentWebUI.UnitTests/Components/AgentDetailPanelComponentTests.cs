using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the AgentDetailPanel slide-out component.
/// Covers rendering in open/closed states, agent identity display, and action buttons.
/// </summary>
public class AgentDetailPanelComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IPipelineRunHistoryService> _mockHistory;
    private readonly PipelineOrchestrationService _pipelineService;
    private readonly AgentRegistryService _registry;

    public AgentDetailPanelComponentTests()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        _mockStore = new Mock<IConfigurationStore>();
        _mockHistory = new Mock<IPipelineRunHistoryService>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockValidator = new Mock<IQualityGateValidator>();

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockHistory.Setup(h => h.GetRunHistory()).Returns(Array.Empty<PipelineRunSummary>());
        _mockHistory.Setup(h => h.GetRunsByAgentId(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(Array.Empty<PipelineRunSummary>());

        _pipelineService = new PipelineOrchestrationService(
            _mockStore.Object, mockFactory.Object, new IssueDescriptionParser(),
            mockValidator.Object, new CiLogWriter(mockLogger.Object), mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: _mockHistory.Object);

        _registry = new AgentRegistryService(mockLogger.Object);

        Services.AddSingleton(_pipelineService);
        Services.AddSingleton(_registry);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockHistory.Object);
        Services.AddSingleton(new Mock<IHubContext<AgentHub, IAgentHubClient>>().Object);
    }

    private static AgentEntry CreateAgent(string agentId = "agent-1", AgentStatus status = AgentStatus.Idle) => new()
    {
        AgentId = agentId,
        ConnectionId = "conn-abc123",
        Hostname = "worker-node-1",
        AgentType = "KiroCli",
        Labels = new[] { "dotnet", "csharp" },
        Status = status,
        RegisteredAt = DateTimeOffset.UtcNow.AddHours(-2),
        LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-10)
    };

    [Fact]
    public void Panel_WhenClosed_DoesNotHaveOpenClass()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, false)
            .Add(s => s.OnClose, EventCallback.Empty));

        // Panel is always rendered but hidden via CSS class — verify it lacks "open"
        var panel = cut.Find("aside.agent-detail-panel");
        Assert.DoesNotContain("open", panel.GetAttribute("class")!.Split(' '));
    }

    [Fact]
    public void Panel_WhenOpen_ShowsAgentDetailsHeader()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        Assert.Contains("Agent Details", cut.Markup);
    }

    [Fact]
    public void Panel_WhenOpen_ShowsIdentitySection()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        Assert.Contains("Identity", cut.Markup);
        Assert.Contains("agent-1", cut.Markup);
        Assert.Contains("worker-node-1", cut.Markup);
        Assert.Contains("KiroCli", cut.Markup);
    }

    [Fact]
    public void Panel_WhenOpen_ShowsLabels()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        Assert.Contains("dotnet", cut.Markup);
        Assert.Contains("csharp", cut.Markup);
    }

    [Fact]
    public void Panel_WhenOpen_ShowsStatusSection()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        Assert.Contains("Status", cut.Markup);
        Assert.Contains("Idle", cut.Markup);
    }

    [Fact]
    public void Panel_WhenOpen_ShowsActionsSection()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        Assert.Contains("Actions", cut.Markup);
        Assert.Contains("Disable Agent", cut.Markup);
        Assert.Contains("Force Disconnect", cut.Markup);
    }

    [Fact]
    public void Panel_WhenAgentDisabled_ShowsEnableButton()
    {
        var agent = CreateAgent();
        agent.Disabled = true;
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        Assert.Contains("Enable Agent", cut.Markup);
        Assert.Contains("DISABLED", cut.Markup);
    }

    [Fact]
    public void Panel_WhenAgentNull_DoesNotRenderContent()
    {
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, null)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        Assert.DoesNotContain("Identity", cut.Markup);
        Assert.DoesNotContain("Agent Details", cut.Markup);
    }

    [Fact]
    public void Panel_ShowsJobHistorySection()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        Assert.Contains("Job History", cut.Markup);
        Assert.Contains("No completed jobs yet", cut.Markup);
    }

    [Fact]
    public void Panel_ShowsMatchedProfileSection()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        Assert.Contains("Matched Profile", cut.Markup);
    }

    [Fact]
    public void Panel_WhenNoProfileMatched_ShowsWarning()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        Assert.Contains("No profile matched", cut.Markup);
    }

    [Fact]
    public async Task Panel_CloseButton_InvokesOnClose()
    {
        var closeCalled = false;
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Factory.Create(this, () => closeCalled = true)));

        var closeBtn = cut.Find("button.agent-detail-close");
        await closeBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(closeCalled);
    }

    [Fact]
    public void Panel_HasOpenCssClass_WhenOpen()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, true)
            .Add(s => s.OnClose, EventCallback.Empty));

        var panel = cut.Find("aside.agent-detail-panel");
        Assert.Contains("open", panel.GetAttribute("class"));
    }

    [Fact]
    public void Panel_DoesNotHaveOpenCssClass_WhenClosed()
    {
        var agent = CreateAgent();
        var cut = Render<AgentDetailPanel>(p => p
            .Add(s => s.Agent, agent)
            .Add(s => s.IsOpen, false)
            .Add(s => s.OnClose, EventCallback.Empty));

        var panel = cut.Find("aside.agent-detail-panel");
        var classes = panel.GetAttribute("class")!.Split(' ');
        Assert.DoesNotContain("open", classes);
    }
}
