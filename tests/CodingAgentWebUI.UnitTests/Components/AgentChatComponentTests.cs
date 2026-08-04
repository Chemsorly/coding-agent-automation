using Bunit;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
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
    private readonly PipelineRunLifecycleService _lifecycle;
    private readonly AgentRegistryService _registry;

    public AgentChatComponentTests()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        _mockStore = new Mock<IConfigurationStore>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PipelineRunSummary>());

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());

        _lifecycle = new PipelineRunLifecycleService(mockHistory.Object, null, mockLogger.Object);

        _registry = new AgentRegistryService(mockLogger.Object);

        Services.AddSingleton(_lifecycle);
        Services.AddSingleton(_registry);
        Services.AddSingleton<IAgentRegistryService>(_registry);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(new Mock<IHubContext<AgentHub, IAgentHubClient>>().Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(new FeatureFlags());  // defaults: IsKubernetesMode = false
        Services.AddSingleton(JobTemplateStore.CreateEmpty());
        Services.AddSingleton<IChatJobDispatcher, NullChatJobDispatcher>();

        // IConfiguration required by AgentChat for ChatPodConnectTimeoutSeconds
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
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

    [Fact]
    public void AgentChat_ShowsChatUI_InSignalRMode()
    {
        // FeatureFlags defaults to IsKubernetesMode = false — chat UI should be present
        var cut = Render<AgentChat>();

        Assert.Contains("Interactive Chat", cut.Markup);
        Assert.DoesNotContain("not available in Kubernetes mode", cut.Markup);
    }

    [Fact]
    public void AgentChat_ShowsK8sLaunchUI_InKubernetesMode()
    {
        // Task 9.1 removed the static "not available in Kubernetes mode" banner.
        // K8s mode now shows the Job Template dropdown and Launch Chat Pod button.
        Services.AddSingleton(new FeatureFlags { IsKubernetesMode = true });

        var cut = Render<AgentChat>();

        Assert.DoesNotContain("not available in Kubernetes mode", cut.Markup);
        Assert.Contains("Interactive Chat", cut.Markup);
        Assert.Contains("Launch Chat Pod", cut.Markup);
        Assert.Contains("template-select", cut.Markup);
    }
}
