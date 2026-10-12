using CodingAgent.Web.Auth;
using AwesomeAssertions;
using CodingAgent.Web.UnitTests.Auth;
using Bunit;
using Moq;
using CodingAgent.Api.Client;
using CodingAgent.Contracts.Ui;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Web.Ui;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// The AgentChat page's services. Since spec 051 2.5b the page reaches the API only through
/// <see cref="IUiChatClient"/> (launch, prompt, keepalive, end) and <see cref="IUiConfigClient"/> (projects);
/// the API resolves profiles and builds prompts (ChatServiceTests). The hub mock starts disconnected and
/// connects on <c>StartAsync</c>. Each derived class sets the user's access.
/// </summary>
public abstract class AgentChatTestBase : BunitContext
{
    protected const string ProfileId = "chat-profile";
    protected const string FakeAgentId = "chat-agent-1";

    protected Mock<IUiChatClient> Chat { get; } = new();
    protected Mock<IUiConfigClient> Config { get; } = new();
    protected Mock<IAgentHubConnection> Hub { get; } = new();

    protected AgentChatTestBase()
    {
        Chat.Setup(c => c.GetOptionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiChatOptions([new UiChatProfile(ProfileId, "Chat profile", ["kiro", "dotnet"])], 120));
        Chat.Setup(c => c.StartAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiChatSession(FakeAgentId, "auto"));
        Config.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<UiProject>());

        var connected = false;
        Hub.Setup(h => h.State).Returns(() => connected ? HubConnectionState.Connected : HubConnectionState.Disconnected);
        Hub.Setup(h => h.StartAsync(It.IsAny<CancellationToken>())).Callback(() => connected = true).Returns(Task.CompletedTask);
        Hub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Hub.Setup(h => h.On(It.IsAny<string>(), It.IsAny<Action>())).Returns(Mock.Of<IDisposable>());
        Hub.Setup(h => h.On<string, IReadOnlyList<string>>(It.IsAny<string>(), It.IsAny<Action<string, IReadOnlyList<string>>>())).Returns(Mock.Of<IDisposable>());
        Hub.Setup(h => h.On<string, int, string?>(It.IsAny<string>(), It.IsAny<Action<string, int, string?>>())).Returns(Mock.Of<IDisposable>());

        Services.AddSingleton(Chat.Object);
        Services.AddSingleton(Config.Object);
        Services.AddSingleton(Hub.Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
    }

    protected async Task<IRenderedComponent<AgentChat>> RenderAndClickLaunchAsync()
    {
        var cut = Render<AgentChat>();
        await cut.InvokeAsync(() => cut.Find("select#profile-select").Change(ProfileId));
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod")).Click());
        return cut;
    }

    protected async Task<IRenderedComponent<AgentChat>> LaunchAsync()
    {
        var cut = await RenderAndClickLaunchAsync();
        cut.WaitForAssertion(() => Assert.Contains("chat-window", cut.Markup), timeout: TimeSpan.FromSeconds(5));
        return cut;
    }

    protected static async Task SendPromptAsync(IRenderedComponent<AgentChat> cut, string text = "hello")
    {
        await cut.InvokeAsync(() => cut.Find("textarea.chat-input").Input(text));
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Send").Click());
    }

    protected int SubscribeCount() => Hub.Invocations
        .Count(i => i.Method.Name == nameof(IAgentHubConnection.InvokeAsync)
                    && i.Arguments[0] is string m && m == HubMethodNames.SubscribeToChatSession);
}

/// <summary>bUnit component tests for the AgentChat page: setup UI, launch, prompt and end.</summary>
public class AgentChatComponentTests : AgentChatTestBase
{
    public AgentChatComponentTests()
    {
        Services.AddTestAccess(); // Spec 049: global admin, so every control renders as before
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
        Assert.Contains("Select an agent profile to launch a dedicated chat pod", cut.Markup);
        Assert.Contains("Launch Chat Pod", cut.Markup);
        Assert.NotNull(cut.Find("select#profile-select"));
    }

    [Fact]
    public void AgentChat_LaunchButton_DisabledWhenNoProfileSelected()
    {
        var cut = Render<AgentChat>();

        var launchBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Launch Chat Pod"));
        Assert.True(launchBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void AgentChat_OffersTheProfilesTheApiReturns_WithTheirLabels()
    {
        Chat.Setup(c => c.GetOptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new UiChatOptions(
        [
            new UiChatProfile("claude", "Claude", ["claude", "dotnet"]),
            new UiChatProfile("kiro", "Kiro", ["kiro"]),
        ], 120));

        var cut = Render<AgentChat>();

        cut.FindAll("#profile-select option").Select(o => o.GetAttribute("value")).Should().Equal("", "claude", "kiro");
        cut.Find("#profile-select option[value='claude']").TextContent.Should().Be("Claude (claude, dotnet)");
        cut.Markup.Should().NotContain("No enabled agent profile has a matching job template");
    }

    [Fact]
    public void AgentChat_ShowsHint_WhenNoProfileCanBeLaunched()
    {
        Chat.Setup(c => c.GetOptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new UiChatOptions([], 120));

        var cut = Render<AgentChat>();

        cut.Markup.Should().Contain("No enabled agent profile has a matching job template");
        cut.FindAll("#profile-select option").Should().ContainSingle();
    }

    [Fact]
    public void AgentChat_WhenTheApiCannotBeReached_StillRendersAndSaysSo()
    {
        Chat.Setup(c => c.GetOptionsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("connection refused"));

        var cut = Render<AgentChat>();

        cut.Find(".agent-detail-warning").TextContent.Should().Contain("Could not load the agent profiles and projects. Reload the page.");
        cut.FindAll("#profile-select option").Should().ContainSingle();
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
        var markupBeforeDispose = cut.Markup;
        cut.Dispose();
        Assert.True(cut.IsDisposed);
        Assert.Contains("Agent Chat", markupBeforeDispose);
    }

    [Fact]
    public async Task Launch_StartsTheChatForTheProfile_AndShowsItsModel()
    {
        // Regression: the header showed "model: auto" when the profile's model was lost on the way.
        Chat.Setup(c => c.StartAsync(ProfileId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiChatSession(FakeAgentId, "claude-opus-5-5"));

        var cut = await LaunchAsync();

        cut.Find(".chat-agent-info").TextContent.Should().Contain(FakeAgentId).And.Contain("model: claude-opus-5-5");
    }

    [Fact]
    public async Task Prompt_IsSentToTheChatsAgent_UnderTheSessionItSubscribedTo()
    {
        var cut = await LaunchAsync();

        await SendPromptAsync(cut, "hello there");

        cut.WaitForAssertion(() => Chat.Verify(c => c.SendPromptAsync(FakeAgentId, It.IsAny<string>(), "hello there", It.IsAny<CancellationToken>()), Times.Once));
        var subscribed = Hub.Invocations.Single(i => i.Method.Name == nameof(IAgentHubConnection.InvokeAsync)
            && i.Arguments[0] is string m && m == HubMethodNames.SubscribeToChatSession).Arguments[1];
        Chat.Verify(c => c.SendPromptAsync(FakeAgentId, (string)subscribed!, "hello there", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prompt_ThatIsRefused_ShowsTheReason()
    {
        Chat.Setup(c => c.SendPromptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The chat pod is disconnected."));
        var cut = await LaunchAsync();

        await SendPromptAsync(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("❌ Failed to send prompt: The chat pod is disconnected."));
    }

    [Fact]
    public async Task EndChat_EndsTheChatInTheApi()
    {
        var cut = await LaunchAsync();

        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("End Chat")).Click());

        cut.WaitForAssertion(() => Chat.Verify(c => c.TerminateAsync(FakeAgentId, It.IsAny<CancellationToken>()), Times.Once));
        cut.Markup.Should().NotContain("chat-window");
    }
}

/// <summary>
/// The hub connection is NOT started during page initialization (prerender), only after the user
/// launches a chat pod. Starting SignalR in OnInitializedAsync fails with 404 because prerender runs
/// before the interactive circuit exists.
/// </summary>
public class AgentChatHubStartTimingTests : AgentChatTestBase
{
    public AgentChatHubStartTimingTests()
    {
        Services.AddTestAccess();
    }

    [Fact]
    public void AgentChat_DoesNotStartHubDuringPageInitialization()
    {
        Render<AgentChat>();

        // The positive counterparts below keep this negative check meaningful.
        Hub.Verify(h => h.StartAsync(It.IsAny<CancellationToken>()), Times.Never,
            "Hub StartAsync must not be called during page initialization (prerender runs before the interactive circuit exists, causing 404)");
    }

    [Fact]
    public void AgentChat_DoesNotRegisterHubHandlersDuringPageInitialization()
    {
        Render<AgentChat>();

        Hub.Verify(h => h.On<string, IReadOnlyList<string>>(It.IsAny<string>(), It.IsAny<Action<string, IReadOnlyList<string>>>()), Times.Never);
        Hub.Verify(h => h.On<string, int, string?>(It.IsAny<string>(), It.IsAny<Action<string, int, string?>>()), Times.Never);
    }

    [Fact]
    public async Task AgentChat_StartsHubAfterLaunchChatPod()
    {
        await LaunchAsync();

        Hub.Verify(h => h.StartAsync(It.IsAny<CancellationToken>()), Times.Once,
            "Hub StartAsync must be called exactly once after LaunchChatPod succeeds");
    }

    [Fact]
    public async Task AgentChat_RegistersHubHandlersAfterLaunchChatPod()
    {
        await LaunchAsync();

        Hub.Verify(h => h.On<string, IReadOnlyList<string>>(HubMethodNames.OnChatResponse, It.IsAny<Action<string, IReadOnlyList<string>>>()), Times.Once);
        Hub.Verify(h => h.On<string, int, string?>(HubMethodNames.OnChatCompleted, It.IsAny<Action<string, int, string?>>()), Times.Once);
    }
}

/// <summary>Spec 049 (D13): Agent Chat is project-scoped.</summary>
public class AgentChatAccessTests : AgentChatTestBase
{
    public AgentChatAccessTests()
    {
        Config.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<UiProject>
        {
            new("6f1c2a9e-0000-0000-0000-00000000000a", "payments", true),
            new("6f1c2a9e-0000-0000-0000-00000000000b", "billing", true),
        });
    }

    private static string[] ProjectOptions(IRenderedComponent<AgentChat> cut) =>
        cut.FindAll("#project-select-k8s option").Select(o => o.GetAttribute("value") ?? "").ToArray();

    [Fact]
    public void ScopedOperator_ChoosesOnlyFromOperableProjects_AndMustChooseOne()
    {
        Services.AddTestAccess(TestAccess.Scoped(("6f1c2a9e-0000-0000-0000-00000000000a", AccessRole.Operator), ("6f1c2a9e-0000-0000-0000-00000000000b", AccessRole.ReadOnly)));

        var cut = Render<AgentChat>();

        ProjectOptions(cut).Should().Equal("6f1c2a9e-0000-0000-0000-00000000000a");
    }

    [Fact]
    public async Task ScopedOperator_LaunchesInTheirProject()
    {
        Services.AddTestAccess(TestAccess.Scoped(("6f1c2a9e-0000-0000-0000-00000000000a", AccessRole.Operator)));

        await LaunchAsync();

        Chat.Verify(c => c.StartAsync(ProfileId, "6f1c2a9e-0000-0000-0000-00000000000a", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GlobalOperator_MayChatWithoutAProject()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.Operator));

        var cut = Render<AgentChat>();

        ProjectOptions(cut).Should().Equal("", "6f1c2a9e-0000-0000-0000-00000000000b", "6f1c2a9e-0000-0000-0000-00000000000a");
    }
}

/// <summary>A launch the API refuses, or a pod it cannot start, shows the API's reason (ChatServiceTests word them).</summary>
public class AgentChatLaunchErrorTests : AgentChatTestBase
{
    public AgentChatLaunchErrorTests()
    {
        Services.AddTestAccess();
    }

    [Fact]
    public async Task LaunchChatPod_Refused_ShowsTheApisReason()
    {
        Chat.Setup(c => c.StartAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Pod did not connect within 120s. Check cluster logs."));

        var cut = await RenderAndClickLaunchAsync();

        cut.WaitForAssertion(() =>
            cut.Find(".agent-detail-warning").TextContent.Trim().Should().Contain("Pod did not connect within 120s. Check cluster logs."));
        cut.Markup.Should().NotContain("chat-window");
    }

    [Fact]
    public async Task LaunchChatPod_UnexpectedError_ShowsLaunchFailed()
    {
        Chat.Setup(c => c.StartAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service Unavailable"));

        var cut = await RenderAndClickLaunchAsync();

        cut.WaitForAssertion(() =>
            cut.Find(".agent-detail-warning").TextContent.Trim().Should().Contain("Launch failed: Service Unavailable"));
    }
}

/// <summary>
/// AgentChat re-joins the active chat session group after a hub reconnect, and the reconnect handler
/// is removed when the chat ends or the component is disposed. Issue #3551: hub group membership is
/// lost on reconnect, so in-flight responses only keep arriving if SubscribeToChatSession runs again.
/// </summary>
public class AgentChatReconnectTests : AgentChatTestBase
{
    public AgentChatReconnectTests()
    {
        Services.AddTestAccess();
    }

    private async Task SendPromptAndWaitForSubscribeAsync(IRenderedComponent<AgentChat> cut)
    {
        await SendPromptAsync(cut);
        cut.WaitForAssertion(() => SubscribeCount().Should().BeGreaterThan(0), timeout: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AgentChat_ResubscribesToChatSession_AfterReconnectWithActiveSession()
    {
        var cut = await LaunchAsync();
        await SendPromptAndWaitForSubscribeAsync(cut);
        var countBefore = SubscribeCount();

        Hub.Raise(m => m.Reconnected += null, "conn-2");

        cut.WaitForAssertion(() => SubscribeCount().Should().BeGreaterThan(countBefore), timeout: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AgentChat_DoesNotSubscribeOnReconnect_WhenNoActiveSession()
    {
        await LaunchAsync();

        Hub.Raise(m => m.Reconnected += null, "conn-2");
        await Task.Delay(200);

        SubscribeCount().Should().Be(0, "SubscribeToChatSession must not be called when no session is active");
    }

    [Fact]
    public async Task AgentChat_DoesNotSubscribeOnReconnect_AfterChatEnds()
    {
        var cut = await LaunchAsync();
        await SendPromptAndWaitForSubscribeAsync(cut);
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("End Chat")).Click());
        cut.WaitForAssertion(() => Assert.DoesNotContain("chat-window", cut.Markup), timeout: TimeSpan.FromSeconds(5));
        var countAfterEnd = SubscribeCount();

        Hub.Raise(m => m.Reconnected += null, "conn-2");
        await Task.Delay(200);

        SubscribeCount().Should().Be(countAfterEnd);
    }

    [Fact]
    public async Task AgentChat_RemovesReconnectedHandlerOnDispose()
    {
        var cut = await LaunchAsync();
        await SendPromptAndWaitForSubscribeAsync(cut);
        var countBeforeDispose = SubscribeCount();

        await cut.Instance.DisposeAsync();
        Hub.Raise(m => m.Reconnected += null, "conn-2");
        await Task.Delay(200);

        SubscribeCount().Should().Be(countBeforeDispose);
    }
}
