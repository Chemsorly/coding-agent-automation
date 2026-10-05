using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /agent-chat page.
/// Encapsulates navigation and interactions for interactive agent chat sessions.
/// Selectors match the current AgentChat.razor markup (post-Kubernetes refactor).
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

    /// <summary>Navigates to the /agent-chat page and waits for the template selector to render.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/agent-chat");
        await _page.WaitForSelectorAsync("#template-select", new() { Timeout = 15_000 });
    }

    /// <summary>Selects an agent type by its labels value in the template dropdown.</summary>
    public async Task SelectTemplateAsync(string labelsValue)
    {
        await _page.SelectOptionAsync("#template-select", labelsValue);
    }

    /// <summary>Clicks the "Launch Chat Pod" button.</summary>
    public async Task LaunchChatPodAsync()
    {
        // Wait for the button to be enabled before clicking. The button is enabled only when
        // _selectedTemplateLabels is set in Blazor, which requires the @onchange from
        // SelectTemplateAsync to round-trip through the Blazor Server WebSocket circuit.
        // Without this wait, clicking immediately after SelectTemplateAsync can land on a
        // still-disabled button because the circuit hasn't processed the change event yet.
        //
        // 30 s instead of 10 s: OnTemplateSelected() is an async method that calls
        // ConfigStore.LoadAgentProfilesAsync + GetProviderConfigByIdAsync before it calls
        // StateHasChanged. Under CI load those async calls can take several seconds, pushing
        // the total roundtrip well beyond the original 10 s budget and producing a flaky
        // TimeoutException at this wait (as seen in shard 3/3 failures).
        var button = _page.Locator(".btn-start-chat");
        await button.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await _page.WaitForFunctionAsync(
            "() => { const b = document.querySelector('.btn-start-chat'); return b && !b.disabled; }",
            null,
            new() { Timeout = 30_000 });
        await _page.ClickAsync(".btn-start-chat");
    }

    /// <summary>
    /// Waits for the chat window header to appear, confirming the pod is connected.
    /// Returns the header text (e.g. "Chatting with agent-id · model: auto").
    /// </summary>
    public async Task<string?> WaitForChatHeaderAsync(int timeoutMs = 60_000)
    {
        var header = await _page.WaitForSelectorAsync(".chat-header-bar", new() { Timeout = timeoutMs });
        if (header is null) return null;
        return await header.InnerTextAsync();
    }

    /// <summary>
    /// Returns the text content of the chat header, or null if it is not visible.
    /// </summary>
    public async Task<string?> GetChatHeaderTextAsync()
    {
        var header = await _page.QuerySelectorAsync(".chat-header-bar");
        return header is null ? null : await header.InnerTextAsync();
    }

    /// <summary>Types a prompt into the chat textarea and clicks Send.</summary>
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

    /// <summary>Clicks the End Chat (✕) button to terminate the session.</summary>
    public async Task EndChatAsync()
    {
        await _page.ClickAsync(".btn-end-chat");
    }

    /// <summary>
    /// Waits until the launch ERROR message appears in the page (e.g. connect timeout or no PVC).
    /// Returns the error text.
    ///
    /// <para>
    /// Both the in-progress "Launching chat pod..." indicator and the final error message use the
    /// <c>.agent-detail-warning</c> CSS class (see <c>AgentChat.razor</c>). This method waits
    /// until the element is present AND does NOT contain the transient "Launching" text, ensuring
    /// callers only receive control once the actual error message has been rendered (i.e., after
    /// <c>_launching</c> is set to <c>false</c> in the component's <c>finally</c> block).
    /// </para>
    /// </summary>
    public async Task<string?> WaitForLaunchErrorAsync(int timeoutMs = 30_000)
    {
        // Wait until .agent-detail-warning is present AND does not contain "Launching"
        // (the in-progress counter). Once _launching = false and _launchError is set the
        // "Launching…" div is removed and the error div takes its place.
        // TODO [WARNING]: this heuristic is fragile — if the actual error message produced by
        // ChatJobDispatcher contains the word "Launching" (e.g. "Launching timed out after…"),
        // the wait condition never becomes true and this method blocks until timeoutMs expires.
        // A more robust approach would use a data attribute set by the component when _launching
        // is false and _launchError is non-null (e.g. data-launch-state="error"), avoiding the
        // substring exclusion entirely.
        await _page.WaitForFunctionAsync(
            "() => { " +
            "  const el = document.querySelector('.agent-detail-warning'); " +
            "  return el && !el.textContent.includes('Launching'); " +
            "}",
            null,
            new PageWaitForFunctionOptions { Timeout = timeoutMs });
        var errorEl = await _page.QuerySelectorAsync(".agent-detail-warning");
        if (errorEl is null) return null;
        return await errorEl.InnerTextAsync();
    }

    /// <summary>
    /// Returns the current text of the launch error warning, or null if it is not present.
    /// </summary>
    public async Task<string?> GetLaunchErrorAsync()
    {
        var el = await _page.QuerySelectorAsync(".agent-detail-warning");
        return el is null ? null : await el.InnerTextAsync();
    }

    /// <summary>
    /// Waits until the page returns to the launch state (template selector is visible).
    /// Useful after End Chat to confirm the page reset.
    /// </summary>
    public async Task WaitForLaunchStateAsync(int timeoutMs = 10_000)
    {
        await _page.WaitForSelectorAsync("#template-select", new() { Timeout = timeoutMs });
    }

    /// <summary>
    /// Returns true when the Launch Chat Pod button is visible but disabled
    /// (no template selected or launch in progress).
    /// </summary>
    public async Task<bool> IsLaunchButtonDisabledAsync()
    {
        return await _page.EvaluateAsync<bool>(
            "() => { const b = document.querySelector('.btn-start-chat'); return b ? b.disabled : false; }");
    }

    /// <summary>
    /// Returns the visible text of all user and agent messages in the transcript.
    /// </summary>
    // TODO [WARNING]: this selector (.chat-message-content pre) also matches the streaming
    // partial-response element when _isWaitingForResponse is true. If called during an active
    // stream the returned list includes the in-progress message alongside completed ones.
    // Callers should wait for streaming to finish (e.g. no .chat-streaming elements) before
    // calling this method if an exact count is required.
    public async Task<IReadOnlyList<string>> GetTranscriptMessagesAsync()
    {
        var elements = await _page.QuerySelectorAllAsync(".chat-message-content pre");
        var texts = new List<string>(elements.Count);
        foreach (var el in elements)
            texts.Add(await el.InnerTextAsync());
        return texts;
    }
}
