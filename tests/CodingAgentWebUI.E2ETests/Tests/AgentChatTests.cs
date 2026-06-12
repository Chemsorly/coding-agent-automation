using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// E2E tests for the Agent Chat feature (/agent-chat page).
/// Exercises the full SignalR round-trip: select agent → send prompt → receive response → end session.
/// </summary>
[Trait("Category", "E2E")]
public sealed class AgentChatTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public AgentChatTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Chat_SendPrompt_ResponseDisplayed()
    {
        // Arrange: connect a fake agent
        await using var fakeAgent = new FakeAgentClient("chat-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: navigate, select agent, start chat, send prompt
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();
        await chatPage.SelectAgentAsync("chat-agent-1");
        await chatPage.StartChatAsync();
        await chatPage.SendPromptAsync("Hello agent");

        // Agent responds
        await fakeAgent.RespondToChatAsync("Hello from the agent!");

        // Assert: response text appears in the UI
        var responseText = await chatPage.GetResponseTextAsync();
        Assert.NotNull(responseText);
        Assert.Contains("Hello from the agent!", responseText);
    }

    [Fact]
    public async Task Chat_NoIdleAgents_ShowsWarning()
    {
        // Act: navigate with no agents connected
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        // Assert: warning shown and start button disabled
        var warningVisible = await chatPage.IsNoIdleAgentsWarningVisibleAsync();
        Assert.True(warningVisible, "Expected 'No idle agents' warning to be visible");

        var buttonDisabled = await chatPage.IsStartButtonDisabledAsync();
        Assert.True(buttonDisabled, "Expected Start Chat button to be disabled when no agents available");
    }

    [Fact]
    public async Task Chat_EndSession_AgentReturnsToIdle()
    {
        // Arrange: connect a fake agent and start a chat
        await using var fakeAgent = new FakeAgentClient("chat-agent-2", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();
        await chatPage.SelectAgentAsync("chat-agent-2");
        await chatPage.StartChatAsync();

        // Act: end the chat session
        await chatPage.EndChatAsync();

        // Assert: agent reappears in the dropdown (back to idle)
        await Task.Delay(500); // Allow state to propagate
        var agentInDropdown = await chatPage.IsAgentInDropdownAsync("chat-agent-2");
        Assert.True(agentInDropdown, "Expected agent to reappear in dropdown after ending chat session");
    }
}
