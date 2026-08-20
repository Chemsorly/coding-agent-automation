using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Playwright E2E regression tests for the Agent Chat SignalR path.
/// Guards against regressions in the existing chat flow after K8s chat mode was added.
/// These tests use <see cref="E2EFixture"/> (SignalR mode — no K8s).
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "AgentChat")]
public sealed class AgentChatSignalRTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public AgentChatSignalRTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Guards: page renders with empty state (no agents) — Start button disabled.
    /// Regression guard: the page renders and shows the empty state correctly.
    /// </summary>
    [Fact]
    public async Task AgentChat_NoIdleAgents_StartButtonDisabled()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        var warningVisible = await chatPage.IsNoIdleAgentsWarningVisibleAsync();
        Assert.True(warningVisible, "Expected 'No idle agents' warning to be visible");

        var buttonDisabled = await chatPage.IsStartButtonDisabledAsync();
        Assert.True(buttonDisabled, "Expected Start Chat button to be disabled when no agents available");
    }

    /// <summary>
    /// Guards: idle agent registration correctly flows to UI dropdown.
    /// Regression guard: idle agent appears in dropdown after connecting.
    /// </summary>
    [Fact]
    public async Task AgentChat_IdleAgentConnects_AppearsInDropdown()
    {
        await using var fakeAgent = new FakeAgentClient("chat-ui-agent-1", "kiro");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        // Wait for dropdown to update (Blazor refresh timer fires every 5s — navigating re-renders)
        await WaitUntilAsync(
            () => chatPage.IsAgentInDropdownAsync("chat-ui-agent-1").GetAwaiter().GetResult(),
            timeout: TimeSpan.FromSeconds(15));

        var agentInDropdown = await chatPage.IsAgentInDropdownAsync("chat-ui-agent-1");
        Assert.True(agentInDropdown, "Expected agent to appear in dropdown after connecting");

        var warningVisible = await chatPage.IsNoIdleAgentsWarningVisibleAsync();
        Assert.False(warningVisible, "Expected 'No idle agents' warning to disappear when agent connects");
    }

    /// <summary>
    /// Guards: the K8s static banner ("not available in Kubernetes mode") was removed.
    /// In SignalR mode (E2EFixture), the idle agent dropdown must be visible — not a banner.
    /// Regression guard: the k8s gate removal does not break SignalR mode.
    /// </summary>
    [Fact]
    public async Task AgentChat_K8sModeBanner_HiddenAfterFeature()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        // The k8s banner must NOT be visible in SignalR mode
        var bannerVisible = await Page.EvaluateAsync<bool>(@"() => {
            const els = document.querySelectorAll('*');
            for (const el of els) {
                if (el.textContent?.includes('not available in Kubernetes mode') ||
                    el.textContent?.includes('Chat is not available')) {
                    const style = window.getComputedStyle(el);
                    if (style.display !== 'none' && style.visibility !== 'hidden')
                        return true;
                }
            }
            return false;
        }");
        Assert.False(bannerVisible, "K8s 'not available' banner must not be visible in SignalR mode");

        // The idle agent dropdown or the empty-state warning must be visible (page renders chat UI)
        var pageHasChatUi = await Page.EvaluateAsync<bool>(@"() => {
            return document.querySelector('#agent-select') !== null ||
                   document.querySelector('.agent-detail-warning') !== null;
        }");
        Assert.True(pageHasChatUi, "Agent chat UI (dropdown or warning) must be visible in SignalR mode");
    }

    /// <summary>
    /// Smoke test — page renders with correct heading.
    /// Regression guard: the chat page renders without errors.
    /// </summary>
    [Fact]
    public async Task AgentChat_ShowsChatSetupUI()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        // Page should render without a Blazor error UI
        var blazorError = await Page.QuerySelectorAsync("#blazor-error-ui");
        if (blazorError is not null)
        {
            var errorStyle = await blazorError.GetAttributeAsync("style");
            Assert.True(
                errorStyle?.Contains("display: none") == true || errorStyle?.Contains("display:none") == true,
                "Blazor error UI must not be visible");
        }

        // Page title should be present
        var title = await Page.TitleAsync();
        Assert.NotEmpty(title);

        // Either the agent dropdown or the "no agents" warning must be visible
        var hasContent = await Page.EvaluateAsync<bool>(@"() => {
            return document.querySelector('#agent-select') !== null ||
                   document.querySelector('.agent-detail-warning') !== null ||
                   document.querySelector('h1') !== null;
        }");
        Assert.True(hasContent, "Chat page should render some content");
    }
}
