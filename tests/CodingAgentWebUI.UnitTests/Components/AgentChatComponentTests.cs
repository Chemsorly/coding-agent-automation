using Bunit;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the AgentChat page.
/// Covers initial render state, agent selection, and chat setup UI.
/// </summary>
public class AgentChatComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly PipelineOrchestrationService _pipelineService;
    private readonly AgentRegistryService _registry;

    public AgentChatComponentTests()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        _mockStore = new Mock<IConfigurationStore>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistory()).Returns(Array.Empty<PipelineRunSummary>());

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        _pipelineService = new PipelineOrchestrationService(
            _mockStore.Object, _mockStore.Object, _mockStore.Object, _mockStore.Object, mockFactory.Object, new IssueDescriptionParser(),
            mockValidator.Object, new CiLogWriter(mockLogger.Object), mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistory.Object);

        _registry = new AgentRegistryService(mockLogger.Object);

        Services.AddSingleton(_pipelineService);
        Services.AddSingleton(_registry);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(new Mock<IHubContext<AgentHub, IAgentHubClient>>().Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
    }

    [Fact]
    public void AgentChat_RendersPageHeader()
    {
        var cut = Render<AgentChat>();

        Assert.Contains("Agent Chat", cut.Markup);
        Assert.NotNull(cut.Find("h1"));
    }

    [Fact]
    public void AgentChat_ShowsChatSetupSection()
    {
        var cut = Render<AgentChat>();

        Assert.Contains("Interactive Chat", cut.Markup);
        Assert.Contains("Select Agent", cut.Markup);
    }

    [Fact]
    public void AgentChat_ShowsNoIdleAgentsWarning_WhenNoAgents()
    {
        var cut = Render<AgentChat>();

        Assert.Contains("No idle agents available", cut.Markup);
    }

    [Fact]
    public void AgentChat_StartChatButton_DisabledWhenNoAgentSelected()
    {
        var cut = Render<AgentChat>();

        var startBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Start Chat"));
        Assert.True(startBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void AgentChat_ShowsAgentDropdown()
    {
        var cut = Render<AgentChat>();

        var select = cut.Find("select#agent-select");
        Assert.NotNull(select);
        Assert.Contains("Select an idle agent", cut.Markup);
    }

    [Fact]
    public void AgentChat_ShowsDescription()
    {
        var cut = Render<AgentChat>();

        Assert.Contains("Send prompts to an idle agent for MCP validation and debugging", cut.Markup);
    }

    [Fact]
    public void AgentChat_DoesNotShowChatWindow_Initially()
    {
        var cut = Render<AgentChat>();

        Assert.DoesNotContain("chat-window", cut.Markup);
        Assert.DoesNotContain("End Chat", cut.Markup);
    }

    [Fact]
    public void AgentChat_DisposesWithoutError()
    {
        var cut = Render<AgentChat>();
        cut.Dispose();
    }
}
