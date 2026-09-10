using Bunit;
using Moq;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CodingAgent.Web.UnitTests.Components;

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
        Services.AddSingleton(JobTemplateStore.CreateEmpty());
        Services.AddSingleton<IChatJobDispatcher, NullChatJobDispatcher>();

        // IAgentHubConnection — no-op mock (chat component starts the hub and registers event handlers)
        var mockHub = new Mock<IAgentHubConnection>();
        mockHub.Setup(h => h.State).Returns(HubConnectionState.Disconnected);
        mockHub.Setup(h => h.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockHub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockHub.Setup(h => h.On(It.IsAny<string>(), It.IsAny<Action>())).Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<string, IReadOnlyList<string>>(It.IsAny<string>(), It.IsAny<Action<string, IReadOnlyList<string>>>())).Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<string, int, string?>(It.IsAny<string>(), It.IsAny<Action<string, int, string?>>())).Returns(Mock.Of<IDisposable>());
        Services.AddSingleton(mockHub.Object);

        // IPipelineApiAgentClient — no-op mock for bUnit rendering
        Services.AddSingleton(Mock.Of<IPipelineApiAgentClient>());

        // IChatPromptBuilder — required after ChatPromptBuilder extraction
        Services.AddSingleton<IChatPromptBuilder>(new ChatPromptBuilder());

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
        Assert.Contains("Select an agent type to launch", cut.Markup);
    }

    [Fact]
    public void AgentChat_ShowsLaunchButton_WhenNoTemplateSelected()
    {
        var cut = Render<AgentChat>();

        var launchBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod"));
        Assert.True(launchBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void AgentChat_StartChatButton_DisabledWhenNoTemplateSelected()
    {
        var cut = Render<AgentChat>();

        var launchBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod"));
        Assert.True(launchBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void AgentChat_ShowsTemplateDropdown()
    {
        var cut = Render<AgentChat>();

        var select = cut.Find("select#template-select");
        Assert.NotNull(select);
        Assert.Contains("Select agent type", cut.Markup);
    }

    [Fact]
    public void AgentChat_ShowsDescription()
    {
        var cut = Render<AgentChat>();

        Assert.Contains("Select an agent type to launch a dedicated chat pod", cut.Markup);
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
        // Capture some markup before disposal to prove the component was live
        var markupBeforeDispose = cut.Markup;
        cut.Dispose();
        // After disposal, the component is no longer renderable — markup is inaccessible
        Assert.True(cut.IsDisposed);
        Assert.Contains("Agent Chat", markupBeforeDispose);
    }

    [Fact]
    public void AgentChat_ShowsK8sLaunchUI()
    {
        // K8s mode is now the only mode — shows Job Template dropdown and Launch Chat Pod button.
        var cut = Render<AgentChat>();

        Assert.DoesNotContain("not available in Kubernetes mode", cut.Markup);
        Assert.Contains("Interactive Chat", cut.Markup);
        Assert.Contains("Launch Chat Pod", cut.Markup);
        Assert.Contains("template-select", cut.Markup);
    }
}

/// <summary>
/// bUnit tests for MCP config path resolution in <see cref="AgentChat"/>.
/// These tests drive ResolvePodLaunchProfileAsync indirectly through LaunchChatPod
/// and capture the resolved McpConfigPath via AssignChatPromptAsync.
///
/// Each test registers its own services so provider config and profile mocks can be
/// configured independently without sharing state with <see cref="AgentChatComponentTests"/>.
/// </summary>
public class AgentChatMcpConfigPathTests : BunitContext
{
    private const string FakeAgentId = "chat-agent-1";
    private const string TemplateLabels = "opencode,dotnet";
    private const string AgentProviderConfigId = "agent-cfg-1";

    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IPipelineApiAgentClient> _mockAgentClient;
    private readonly Mock<IChatJobDispatcher> _mockDispatcher;
    private readonly Mock<IAgentHubConnection> _mockHub;

    public AgentChatMcpConfigPathTests()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        _mockStore = new Mock<IConfigurationStore>();
        _mockAgentClient = new Mock<IPipelineApiAgentClient>();
        _mockDispatcher = new Mock<IChatJobDispatcher>();
        _mockHub = new Mock<IAgentHubConnection>();

        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PipelineRunSummary>());

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());

        // Dispatcher returns FakeAgentId so LaunchChatPod succeeds and chat becomes active.
        _mockDispatcher
            .Setup(d => d.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FakeAgentId);

        // Hub: disconnected initially, no-op start + invoke + On registrations.
        _mockHub.Setup(h => h.State).Returns(HubConnectionState.Disconnected);
        _mockHub.Setup(h => h.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockHub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockHub.Setup(h => h.On(It.IsAny<string>(), It.IsAny<Action>())).Returns(Mock.Of<IDisposable>());
        _mockHub.Setup(h => h.On<string, IReadOnlyList<string>>(It.IsAny<string>(), It.IsAny<Action<string, IReadOnlyList<string>>>())).Returns(Mock.Of<IDisposable>());
        _mockHub.Setup(h => h.On<string, int, string?>(It.IsAny<string>(), It.IsAny<Action<string, int, string?>>())).Returns(Mock.Of<IDisposable>());

        // AssignChatPromptAsync no-op — callers capture it via Setup/Verify.
        _mockAgentClient
            .Setup(c => c.AssignChatPromptAsync(It.IsAny<string>(), It.IsAny<ChatPromptMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var lifecycle = new PipelineRunLifecycleService(mockHistory.Object, null, mockLogger.Object);
        var registry = new AgentRegistryService(mockLogger.Object);

        Services.AddSingleton(lifecycle);
        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(new Mock<IHubContext<AgentHub, IAgentHubClient>>().Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(JobTemplateStore.CreateEmpty());
        Services.AddSingleton(_mockDispatcher.Object);
        Services.AddSingleton(_mockHub.Object);
        Services.AddSingleton(_mockAgentClient.Object);
        Services.AddSingleton<IChatPromptBuilder>(new ChatPromptBuilder());
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
    }

    /// <summary>
    /// Helper: build an AgentProfile that matches TemplateLabels and points to a ProviderConfig
    /// with the given ProviderType and optional explicit mcpConfigPath setting.
    /// </summary>
    private static AgentProfile MakeProfile() =>
        new AgentProfile
        {
            DisplayName = "Test Profile",
            AgentProviderConfigId = AgentProviderConfigId,
            MatchLabels = TemplateLabels.Split(',').ToList(),
            Enabled = true
        };

    private static ProviderConfig MakeProviderConfig(string providerType, string? explicitMcpPath = null)
    {
        var settings = new Dictionary<string, string>();
        if (explicitMcpPath is not null)
            settings[CodingAgent.Pipeline.ProviderSettingKeys.McpConfigPath] = explicitMcpPath;

        return new ProviderConfig
        {
            DisplayName = "Test Agent Config",
            Kind = ProviderKind.Agent,
            ProviderType = providerType,
            Settings = settings
        };
    }

    /// <summary>
    /// Launches the chat pod with the given agent config, then sends one prompt,
    /// and captures the ChatPromptMessage passed to AssignChatPromptAsync.
    /// </summary>
    private async Task<ChatPromptMessage> LaunchAndCapturePromptMessageAsync(ProviderConfig agentConfig)
    {
        var profile = MakeProfile();

        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { profile });
        _mockStore.Setup(s => s.GetProviderConfigByIdAsync(AgentProviderConfigId, ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(agentConfig);

        var cut = Render<AgentChat>();

        // Select template to trigger ResolvePodLaunchProfileAsync
        var select = cut.Find("select#template-select");
        await cut.InvokeAsync(() => select.Change(TemplateLabels));

        // Click Launch Chat Pod
        var launchBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod"));
        await cut.InvokeAsync(() => launchBtn.Click());

        // Wait for chat window to appear (launch succeeded)
        cut.WaitForAssertion(() => Assert.Contains("chat-window", cut.Markup), timeout: TimeSpan.FromSeconds(5));

        // TODO: The AssignChatPromptAsync callback is re-registered *after* the launch button click
        // and after chat-window appears. This is safe today because no automatic prompt is sent
        // during StartChat / connection, but is fragile: if any future code path calls
        // AssignChatPromptAsync before this re-registration (e.g., a reconnect or init prompt),
        // the no-op Setup from the constructor fires instead, captured stays null, and the
        // WaitForAssertion below times out with no meaningful failure. Move this Setup to before
        // Render<AgentChat>() or at least before the launch button click to remove the race.

        // Send one prompt — this triggers AssignChatPromptAsync with the resolved McpConfigPath
        ChatPromptMessage? captured = null;
        _mockAgentClient
            .Setup(c => c.AssignChatPromptAsync(It.IsAny<string>(), It.IsAny<ChatPromptMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, ChatPromptMessage, CancellationToken>((_, msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var textarea = cut.Find("textarea.chat-input");
        await cut.InvokeAsync(() => textarea.Input("hello"));

        var sendBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Send");
        await cut.InvokeAsync(() => sendBtn.Click());

        // Wait for AssignChatPromptAsync to be called
        cut.WaitForAssertion(() => Assert.NotNull(captured), timeout: TimeSpan.FromSeconds(5));

        return captured!;
    }

    [Fact]
    public async Task ResolvePodLaunchProfileAsync_OpenCodeProvider_SetsOpenCodeMcpPath()
    {
        // Given: provider with ProviderType = "OpenCode" and no explicit mcpConfigPath
        var agentConfig = MakeProviderConfig("OpenCode");

        var msg = await LaunchAndCapturePromptMessageAsync(agentConfig);

        Assert.Equal("/home/ubuntu/.opencode/mcp.json", msg.McpConfigPath);
    }

    [Fact]
    public async Task ResolvePodLaunchProfileAsync_KiroCliProvider_SetsKiroMcpPath()
    {
        // Given: provider with ProviderType = "KiroCli" and no explicit mcpConfigPath
        var agentConfig = MakeProviderConfig("KiroCli");

        var msg = await LaunchAndCapturePromptMessageAsync(agentConfig);

        Assert.Equal("/home/ubuntu/.kiro/settings/mcp.json", msg.McpConfigPath);
    }

    [Fact]
    public async Task ResolvePodLaunchProfileAsync_ExplicitMcpConfigPathOverridesProviderType()
    {
        // Given: ProviderType = "OpenCode" but explicit mcpConfigPath in Settings
        const string customPath = "/custom/mcp.json";
        var agentConfig = MakeProviderConfig("OpenCode", explicitMcpPath: customPath);

        var msg = await LaunchAndCapturePromptMessageAsync(agentConfig);

        Assert.Equal(customPath, msg.McpConfigPath);
    }

    [Fact]
    public async Task ResolvePodLaunchProfileAsync_UnknownProviderType_FallsBackToKiroPath()
    {
        // Given: ProviderType is some unknown value and no explicit mcpConfigPath
        var agentConfig = MakeProviderConfig("SomeNewProvider");

        var msg = await LaunchAndCapturePromptMessageAsync(agentConfig);

        Assert.Equal("/home/ubuntu/.kiro/settings/mcp.json", msg.McpConfigPath);
    }

    [Fact]
    public async Task ResolvePodLaunchProfileAsync_NullAgentConfig_FallsBackToKiroPath()
    {
        // Given: profile resolves but GetProviderConfigByIdAsync returns null
        var profile = MakeProfile();

        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { profile });
        _mockStore.Setup(s => s.GetProviderConfigByIdAsync(AgentProviderConfigId, ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var cut = Render<AgentChat>();

        var select = cut.Find("select#template-select");
        await cut.InvokeAsync(() => select.Change(TemplateLabels));

        var launchBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod"));
        await cut.InvokeAsync(() => launchBtn.Click());

        cut.WaitForAssertion(() => Assert.Contains("chat-window", cut.Markup), timeout: TimeSpan.FromSeconds(5));

        ChatPromptMessage? captured = null;
        _mockAgentClient
            .Setup(c => c.AssignChatPromptAsync(It.IsAny<string>(), It.IsAny<ChatPromptMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, ChatPromptMessage, CancellationToken>((_, msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var textarea = cut.Find("textarea.chat-input");
        await cut.InvokeAsync(() => textarea.Input("hello"));

        var sendBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Send");
        await cut.InvokeAsync(() => sendBtn.Click());

        cut.WaitForAssertion(() => Assert.NotNull(captured), timeout: TimeSpan.FromSeconds(5));

        Assert.Equal("/home/ubuntu/.kiro/settings/mcp.json", captured!.McpConfigPath);
    }

    [Fact]
    public async Task ResolvePodLaunchProfileAsync_NullProfile_FallsBackToKiroPath()
    {
        // Given: no AgentProfile matches the selected template labels (_resolvedProfile is null)
        // The unconditional reset at the top of ResolvePodLaunchProfileAsync must fire.
        // TODO: This test does not actually verify the unconditional-reset behaviour it describes.
        // With a fresh component instance, _resolvedMcpConfigPath is already initialised to the
        // Kiro default, so this test passes even if the unconditional-reset line is deleted. The
        // stale-state-reset regression is covered by SequentialSessions_DoesNotLeakPathAcrossSessions.
        // Consider replacing this test with one that starts from a prior OpenCode session and
        // then launches with a null profile, asserting the path reverts to the Kiro default.
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());

        var cut = Render<AgentChat>();

        var select = cut.Find("select#template-select");
        await cut.InvokeAsync(() => select.Change(TemplateLabels));

        var launchBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod"));
        await cut.InvokeAsync(() => launchBtn.Click());

        cut.WaitForAssertion(() => Assert.Contains("chat-window", cut.Markup), timeout: TimeSpan.FromSeconds(5));

        ChatPromptMessage? captured = null;
        _mockAgentClient
            .Setup(c => c.AssignChatPromptAsync(It.IsAny<string>(), It.IsAny<ChatPromptMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, ChatPromptMessage, CancellationToken>((_, msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var textarea = cut.Find("textarea.chat-input");
        await cut.InvokeAsync(() => textarea.Input("hello"));

        var sendBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Send");
        await cut.InvokeAsync(() => sendBtn.Click());

        cut.WaitForAssertion(() => Assert.NotNull(captured), timeout: TimeSpan.FromSeconds(5));

        Assert.Equal("/home/ubuntu/.kiro/settings/mcp.json", captured!.McpConfigPath);
    }

    [Fact]
    public async Task ResolvePodLaunchProfileAsync_SequentialSessions_DoesNotLeakPathAcrossSessions()
    {
        // Session 1: OpenCode agent (sets OpenCode path)
        var openCodeConfig = MakeProviderConfig("OpenCode");
        var profile = MakeProfile();

        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { profile });
        _mockStore.Setup(s => s.GetProviderConfigByIdAsync(AgentProviderConfigId, ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openCodeConfig);

        var cut = Render<AgentChat>();

        // Launch session 1 (OpenCode)
        var select = cut.Find("select#template-select");
        await cut.InvokeAsync(() => select.Change(TemplateLabels));

        var launchBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod"));
        await cut.InvokeAsync(() => launchBtn.Click());

        cut.WaitForAssertion(() => Assert.Contains("chat-window", cut.Markup), timeout: TimeSpan.FromSeconds(5));

        // End session 1
        var endBtn = cut.FindAll("button").First(b => b.TextContent.Contains("End Chat"));
        await cut.InvokeAsync(() => endBtn.Click());

        cut.WaitForAssertion(() => Assert.DoesNotContain("chat-window", cut.Markup), timeout: TimeSpan.FromSeconds(5));

        // Switch to KiroCli provider for session 2
        var kiroConfig = MakeProviderConfig("KiroCli");
        _mockStore.Setup(s => s.GetProviderConfigByIdAsync(AgentProviderConfigId, ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(kiroConfig);

        // Re-select the template to trigger profile resolution again
        var select2 = cut.Find("select#template-select");
        await cut.InvokeAsync(() => select2.Change(TemplateLabels));

        var launchBtn2 = cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod"));
        await cut.InvokeAsync(() => launchBtn2.Click());

        cut.WaitForAssertion(() => Assert.Contains("chat-window", cut.Markup), timeout: TimeSpan.FromSeconds(5));

        // Send a prompt in session 2 and capture the McpConfigPath
        ChatPromptMessage? captured = null;
        _mockAgentClient
            .Setup(c => c.AssignChatPromptAsync(It.IsAny<string>(), It.IsAny<ChatPromptMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, ChatPromptMessage, CancellationToken>((_, msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var textarea = cut.Find("textarea.chat-input");
        await cut.InvokeAsync(() => textarea.Input("hello session 2"));

        var sendBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Send");
        await cut.InvokeAsync(() => sendBtn.Click());

        cut.WaitForAssertion(() => Assert.NotNull(captured), timeout: TimeSpan.FromSeconds(5));

        // The stale OpenCode path from session 1 must NOT leak into session 2
        Assert.Equal("/home/ubuntu/.kiro/settings/mcp.json", captured!.McpConfigPath);
    }
}


/// <summary>
/// Tests that the hub connection is NOT started eagerly during page initialization (prerender),
/// but IS started only after the user launches a chat pod (post-circuit, from a user action).
///
/// Background: starting SignalR during OnInitializedAsync fails with 404 because prerender
/// runs before the interactive circuit exists. The hub must be started only from StartChat(),
/// which is called after LaunchChatPod succeeds.
/// </summary>
public class AgentChatHubStartTimingTests : BunitContext
{
    private readonly Mock<IAgentHubConnection> _mockHub;
    private readonly Mock<IChatJobDispatcher> _mockDispatcher;

    private const string FakeAgentId = "chat-agent-timing-1";
    private const string TemplateLabels = "kiro,dotnet";

    public AgentChatHubStartTimingTests()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockStore = new Mock<IConfigurationStore>();
        _mockDispatcher = new Mock<IChatJobDispatcher>();
        _mockHub = new Mock<IAgentHubConnection>();

        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());

        _mockDispatcher
            .Setup(d => d.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FakeAgentId);

        // Hub starts disconnected, StartAsync transitions to connected for subsequent InvokeAsync calls.
        var connected = false;
        _mockHub.Setup(h => h.State)
            .Returns(() => connected ? HubConnectionState.Connected : HubConnectionState.Disconnected);
        _mockHub.Setup(h => h.StartAsync(It.IsAny<CancellationToken>()))
            .Callback(() => connected = true)
            .Returns(Task.CompletedTask);
        _mockHub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockHub.Setup(h => h.On(It.IsAny<string>(), It.IsAny<Action>()))
            .Returns(Mock.Of<IDisposable>());
        _mockHub.Setup(h => h.On<string, IReadOnlyList<string>>(It.IsAny<string>(), It.IsAny<Action<string, IReadOnlyList<string>>>()))
            .Returns(Mock.Of<IDisposable>());
        _mockHub.Setup(h => h.On<string, int, string?>(It.IsAny<string>(), It.IsAny<Action<string, int, string?>>()))
            .Returns(Mock.Of<IDisposable>());

        var lifecycle = new PipelineRunLifecycleService(mockHistory.Object, null, mockLogger.Object);
        var registry = new AgentRegistryService(mockLogger.Object);

        Services.AddSingleton(lifecycle);
        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(mockStore.Object);
        Services.AddSingleton(new Mock<IHubContext<AgentHub, IAgentHubClient>>().Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(JobTemplateStore.CreateEmpty());
        Services.AddSingleton(_mockDispatcher.Object);
        Services.AddSingleton(_mockHub.Object);
        Services.AddSingleton(Mock.Of<IPipelineApiAgentClient>());
        Services.AddSingleton<IChatPromptBuilder>(new ChatPromptBuilder());
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
    }

    [Fact]
    public void AgentChat_DoesNotStartHubDuringPageInitialization()
    {
        // Render the page (triggers OnInitialized + OnInitializedAsync)
        var cut = Render<AgentChat>();

        // StartAsync must NOT be called during initialization — only after pod launch
        // TODO [WARNING]: These negative-only tests cannot distinguish "bug is fixed" from "launch
        // path was never exercised at all". They only check Times.Never, which is trivially satisfied
        // if any conditional prevents the code path from being reached. The paired positive tests
        // AgentChat_StartsHubAfterLaunchChatPod / AgentChat_RegistersHubHandlersAfterLaunchChatPod
        // serve as the counterpart; ensure they are kept in sync — deleting or breaking them would
        // leave these negative tests unable to catch a regression.
        _mockHub.Verify(h => h.StartAsync(It.IsAny<CancellationToken>()), Times.Never,
            "Hub StartAsync must not be called during page initialization (prerender runs before the interactive circuit exists, causing 404)");
    }

    [Fact]
    public void AgentChat_DoesNotRegisterHubHandlersDuringPageInitialization()
    {
        // Render the page (triggers OnInitialized + OnInitializedAsync)
        var cut = Render<AgentChat>();

        // On<> handlers must NOT be registered during initialization
        _mockHub.Verify(
            h => h.On<string, IReadOnlyList<string>>(It.IsAny<string>(), It.IsAny<Action<string, IReadOnlyList<string>>>()),
            Times.Never,
            "Hub On<> handlers must not be registered during page initialization");
        _mockHub.Verify(
            h => h.On<string, int, string?>(It.IsAny<string>(), It.IsAny<Action<string, int, string?>>()),
            Times.Never,
            "Hub On<> handlers must not be registered during page initialization");
    }

    [Fact]
    public async Task AgentChat_StartsHubAfterLaunchChatPod()
    {
        // TODO [WARNING]: This test uses JobTemplateStore.CreateEmpty(), so the select dropdown has no
        // real <option> elements. The Change("kiro,dotnet") call works via Blazor @bind regardless of
        // DOM options, but the setup would silently test a blocked launch path if the component ever
        // validates that the selected value corresponds to an actual template entry. Consider registering
        // a JobTemplateStore with a template whose labels match TemplateLabels so DOM state mirrors the
        // real UI.
        //
        // TODO [WARNING]: Does not test the re-launch scenario (EndChat followed by a second LaunchChatPod).
        // On re-launch, StartAsync should NOT be called again if the hub is still Connected, but handlers
        // must still be re-registered. Add a test covering this case to verify the
        // `if (State == Disconnected)` guard and the handler re-registration behave correctly.
        var cut = Render<AgentChat>();

        // Before launch: StartAsync must not have been called
        _mockHub.Verify(h => h.StartAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Select template and click Launch Chat Pod
        var select = cut.Find("select#template-select");
        await cut.InvokeAsync(() => select.Change(TemplateLabels));

        var launchBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod"));
        await cut.InvokeAsync(() => launchBtn.Click());

        // Wait for chat window to appear (launch succeeded)
        cut.WaitForAssertion(() => Assert.Contains("chat-window", cut.Markup), timeout: TimeSpan.FromSeconds(5));

        // After launch: StartAsync must have been called exactly once
        _mockHub.Verify(h => h.StartAsync(It.IsAny<CancellationToken>()), Times.Once,
            "Hub StartAsync must be called exactly once after LaunchChatPod succeeds");
    }

    [Fact]
    public async Task AgentChat_RegistersHubHandlersAfterLaunchChatPod()
    {
        var cut = Render<AgentChat>();

        // Select template and click Launch Chat Pod
        var select = cut.Find("select#template-select");
        await cut.InvokeAsync(() => select.Change(TemplateLabels));

        var launchBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod"));
        await cut.InvokeAsync(() => launchBtn.Click());

        cut.WaitForAssertion(() => Assert.Contains("chat-window", cut.Markup), timeout: TimeSpan.FromSeconds(5));

        // After launch: On<> handlers must be registered
        _mockHub.Verify(
            h => h.On<string, IReadOnlyList<string>>(HubMethodNames.OnChatResponse, It.IsAny<Action<string, IReadOnlyList<string>>>()),
            Times.Once,
            "OnChatResponse handler must be registered after pod launch");
        _mockHub.Verify(
            h => h.On<string, int, string?>(HubMethodNames.OnChatCompleted, It.IsAny<Action<string, int, string?>>()),
            Times.Once,
            "OnChatCompleted handler must be registered after pod launch");
    }
}
