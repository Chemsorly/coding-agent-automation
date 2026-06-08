using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.PageObjects;

/// <summary>
/// Page object for the /agent-chat page.
/// Encapsulates navigation and interactions for interactive agent chat sessions.
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

    /// <summary>Navigates to the /agent-chat page and waits for it to render.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/agent-chat");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await _page.WaitForTimeoutAsync(3000);
    }

    /// <summary>Selects an agent from the dropdown by agent ID value.</summary>
    public async Task SelectAgentAsync(string agentId)
    {
        await _page.SelectOptionAsync("#agent-select", agentId);
    }

    /// <summary>Clicks the Start Chat button.</summary>
    public async Task StartChatAsync()
    {
        await _page.ClickAsync(".btn-start-chat");
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

    /// <summary>Checks if the "No idle agents" warning is visible.</summary>
    public async Task<bool> IsNoIdleAgentsWarningVisibleAsync()
    {
        var warning = await _page.QuerySelectorAsync(".agent-detail-warning");
        if (warning is null) return false;
        var text = await warning.TextContentAsync();
        return text?.Contains("No idle agents") == true;
    }

    /// <summary>Checks if the Start Chat button is disabled.</summary>
    public async Task<bool> IsStartButtonDisabledAsync()
    {
        return await _page.EvaluateAsync<bool>(
            "() => document.querySelector('.btn-start-chat')?.disabled === true");
    }

    /// <summary>Checks if a specific agent appears in the dropdown options.</summary>
    public async Task<bool> IsAgentInDropdownAsync(string agentId)
    {
        return await _page.EvaluateAsync<bool>(@"(id) => {
            const options = document.querySelectorAll('#agent-select option');
            for (const opt of options) {
                if (opt.value === id) return true;
            }
            return false;
        }", agentId);
    }
}
