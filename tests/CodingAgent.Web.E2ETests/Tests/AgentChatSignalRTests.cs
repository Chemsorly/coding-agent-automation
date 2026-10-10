using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Smoke coverage for the Agent Chat page.
///
/// This file used to hold four tests guarding the pre-Kubernetes chat UI, where an operator
/// picked a connected idle agent out of an <c>#agent-select</c> dropdown and a "No idle agents"
/// warning appeared when the pool was empty. Chat is now on-demand: you choose an agent profile and
/// <c>ChatJobDispatcher</c> launches a pod from the job template its labels name. Neither the dropdown nor the warning exists in
/// <c>AgentChat.razor</c> any more — the string "No idle agents" appears nowhere in <c>src/</c> —
/// so the three tests asserting them were removed rather than ported. The same applies to
/// <c>AgentChatTests</c>, deleted entirely for the same reason.
///
/// Rewriting UI coverage for the template-based flow needs the chat dispatcher and the agent hub
/// to share agent state across the process boundary, which they do not yet.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "AgentChat")]
[Collection(E2ECollection.Name)]
public sealed class AgentChatSignalRTests : E2ETestBase
{
    public AgentChatSignalRTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Guards that the chat page renders at all.
    ///
    /// Worth keeping despite being a smoke test: the page takes <c>IChatJobDispatcher</c> by
    /// injection, and when that registration went missing during the 041–045 arc the page threw
    /// on first render. This is the check that catches that class of break.
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

        // The profile selector is the entry point of the current chat flow. The old version of
        // this assertion also accepted any <h1>, which made it true on every page that renders.
        var profileSelect = await Page.QuerySelectorAsync("#profile-select");
        Assert.True(profileSelect is not null, "Chat page should render the agent profile selector");
    }
}
