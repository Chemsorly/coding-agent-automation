using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /agent-chat page.
/// Encapsulates navigation and interactions for interactive agent chat sessions.
///
/// Flow: SelectTemplateAsync → LaunchChatPodAsync → (test connects fake agent) →
/// WaitForChatWindowAsync → SendPromptAsync → GetResponseTextAsync → EndChatAsync.
/// </summary>
public sealed class AgentChatPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public AgentChatPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>
    /// Navigates to the /agent-chat page and waits for the Blazor circuit to become interactive.
    /// Uses <see cref="BlazorPageExtensions.WaitForInteractiveAsync"/> on <c>.btn-start-chat</c>
    /// to confirm the circuit is live and event handlers are attached, rather than a fixed sleep.
    /// </summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/agent-chat");
        // Pre-circuit guard: wait for the h1 (present in pre-rendered HTML).
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        // Wait for Blazor circuit to activate and @onclick to be attached to the launch button.
        // .btn-start-chat is always in the pre-rendered DOM and has @onclick, so _blazorEvents_*
        // appearing on it confirms the circuit is interactive.
        await _page.WaitForInteractiveAsync(".btn-start-chat");
    }

    /// <summary>
    /// Selects an agent type from the <c>#template-select</c> dropdown by its option value
    /// (e.g. <c>"kiro,dotnet"</c>). Must be called before <see cref="LaunchChatPodAsync"/>.
    /// </summary>
    public async Task SelectTemplateAsync(string labelValue)
    {
        await _page.SelectOptionAsync("#template-select", new SelectOptionValue { Value = labelValue });
    }

    /// <summary>
    /// Clicks the "Launch Chat Pod" button (<c>.btn-start-chat</c>).
    /// The button must not be disabled — call <see cref="SelectTemplateAsync"/> first.
    /// Does NOT wait for the chat window; the caller is responsible for connecting the fake agent
    /// and then calling <see cref="WaitForChatWindowAsync"/> once the dispatch completes.
    /// </summary>
    public async Task LaunchChatPodAsync()
    {
        await _page.ClickAsync(".btn-start-chat");
    }

    /// <summary>
    /// Waits for the chat window header bar to appear, indicating <c>_isChatActive = true</c>
    /// and <c>StartChat()</c> has completed. Call this after the fake agent has connected.
    /// </summary>
    public async Task WaitForChatWindowAsync(int timeoutMs = 35_000)
    {
        await _page.WaitForSelectorAsync(".chat-header-bar", new() { Timeout = timeoutMs });
    }

    /// <summary>
    /// Waits for the launch error element (<c>.agent-detail-warning</c>) to appear with an error
    /// message (not the transient "Launching chat pod..." progress text).
    ///
    /// Both the launching-progress div and the error div share the <c>.agent-detail-warning</c>
    /// CSS class. The progress div is visible immediately after clicking Launch (while
    /// <c>_launching = true</c>), but is replaced by the error div once the launch fails
    /// (<c>_launching = false</c>, <c>_launchError != null</c>). Waiting for the selector
    /// alone would return immediately on the progress text rather than the error text.
    /// This overload uses <c>WaitForFunctionAsync</c> to poll until the element is present
    /// AND its text content does not start with "Launching" (i.e., the error message has replaced
    /// the progress indicator).
    /// </summary>
    public async Task WaitForLaunchErrorAsync(int timeoutMs = 40_000)
    {
        await _page.WaitForFunctionAsync(
            @"() => {
                const el = document.querySelector('.agent-detail-warning');
                return el !== null && !el.textContent.includes('Launching chat pod');
            }",
            null,
            new() { Timeout = timeoutMs });
    }

    /// <summary>Types a prompt and clicks Send.</summary>
    public async Task SendPromptAsync(string text)
    {
        await _page.FillAsync(".chat-input", text);
        await _page.ClickAsync(".btn-send");
    }

    /// <summary>
    /// Waits for the streaming indicator to disappear, then reads the last agent message content.
    /// </summary>
    public async Task<string?> GetResponseTextAsync(int timeoutMs = 15_000)
    {
        // Wait for at least one agent message to appear
        await _page.WaitForSelectorAsync(
            ".chat-message-agent .chat-message-content pre",
            new() { Timeout = timeoutMs });

        // Wait for streaming to finish (no more .chat-streaming elements)
        await _page.WaitForFunctionAsync(
            "() => !document.querySelector('.chat-streaming')",
            null,
            new() { Timeout = timeoutMs });

        // Read the last agent message
        return await _page.EvaluateAsync<string?>(@"() => {
            const msgs = document.querySelectorAll('.chat-message-agent .chat-message-content pre');
            return msgs.length > 0 ? msgs[msgs.length - 1].textContent : null;
        }");
    }

    /// <summary>Clicks the End Chat button.</summary>
    public async Task EndChatAsync()
    {
        await _page.ClickAsync(".btn-end-chat");
    }

    /// <summary>
    /// Checks if the Launch Chat Pod button is disabled.
    /// Returns <c>true</c> before a template is selected (empty <c>_selectedTemplateLabels</c>)
    /// or while a launch is in progress (<c>_launching = true</c>).
    /// </summary>
    public async Task<bool> IsStartButtonDisabledAsync()
    {
        return await _page.EvaluateAsync<bool>(
            "() => document.querySelector('.btn-start-chat')?.disabled === true");
    }
}
