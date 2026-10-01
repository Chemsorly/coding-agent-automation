using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /attention page.
/// Encapsulates DOM queries for Attention sections and the blocked-issues section.
///
/// Key DOM structure:
/// - Each non-empty run-history section has data-testid="attention-section-{testId}"
///   where testId is: "needs-refinement", "failed-runs", "plans-posted"
/// - Each section's count badge has data-testid="attention-section-count-{testId}"
/// - Each row has data-testid="attention-row-{issueIdentifier}"
/// - The blocked issues count badge has data-testid="attention-blocked-count"
/// - Each blocked row has data-testid="attention-blocked-row-{identifier}"
///
/// IMPORTANT: Sections with zero rows are NOT rendered in the DOM at all. Tests asserting
/// on zero-count states must use CountAsync() == 0, not read a count badge.
/// </summary>
public sealed class AttentionPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    private const int DefaultTimeout = 15_000;

    public AttentionPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to /attention and waits for the page to finish its initial load.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/attention");
        // Wait for the h1 to appear (prerendered HTML)
        await _page.WaitForSelectorAsync("h1", new() { Timeout = DefaultTimeout });
        // Wait for the loading card to disappear (Blazor Server data load)
        await WaitForLoadCompleteAsync();
    }

    /// <summary>Waits until the loading card is gone and section content is visible.</summary>
    public async Task WaitForLoadCompleteAsync()
    {
        // Wait until the main data round-trip is done: Blazor's OnInitializedAsync completes,
        // _loading is set to false, and the page re-renders with either attention-section cards
        // or the "nothing needs attention" empty state.
        //
        // The previous guard (!cockpit-empty || !any-contains-Loading) was insufficient:
        // the blocked-issues section renders a cockpit-empty with "Checking…" (not "Loading"),
        // which caused the old condition to fire as soon as _loading became false — but before
        // the attention-section data-testid elements landed in the DOM, producing false negatives
        // on IsSectionVisibleAsync calls immediately after this method returned.
        //
        // The new guard waits for the blocked-count badge (data-testid="attention-blocked-count"),
        // which is always rendered in the else branch (after _loading = false) regardless of
        // whether any attention sections have rows. This is the most stable sentinel: it is
        // absent during prerender and the "Loading…" state, and always present once Blazor has
        // finished its data round-trip and re-rendered the page.
        await _page.WaitForSelectorAsync(
            "[data-testid='attention-blocked-count']",
            new() { Timeout = DefaultTimeout });
    }

    /// <summary>
    /// Returns the count shown in the section's badge, or null if the section is not rendered
    /// (zero rows means the section card is absent from the DOM entirely).
    /// </summary>
    public async Task<int?> GetSectionCountAsync(string testId)
    {
        var badge = _page.Locator($"[data-testid='attention-section-count-{testId}']");
        var count = await badge.CountAsync();
        if (count == 0)
            return null;
        var text = await badge.TextContentAsync();
        return int.TryParse(text?.Trim(), out var n) ? n : null;
    }

    /// <summary>
    /// Returns true if the section card is present in the DOM (i.e., it has at least one row).
    /// </summary>
    public async Task<bool> IsSectionVisibleAsync(string testId)
        => await _page.Locator($"[data-testid='attention-section-{testId}']").CountAsync() > 0;

    /// <summary>Returns true if an attention row for the given issue identifier is visible.</summary>
    public async Task<bool> IsRowVisibleAsync(string issueIdentifier)
        => await _page.Locator($"[data-testid='attention-row-{issueIdentifier}']").CountAsync() > 0;

    /// <summary>
    /// Returns the current text of the blocked-issues count badge.
    /// Returns "checking…" while still loading.
    /// </summary>
    public async Task<string?> GetBlockedCountTextAsync()
    {
        var badge = _page.Locator("[data-testid='attention-blocked-count']");
        if (await badge.CountAsync() == 0)
            return null;
        return (await badge.TextContentAsync())?.Trim();
    }

    /// <summary>
    /// Waits until the blocked-issues section has finished loading (badge no longer shows "checking…").
    /// </summary>
    public async Task WaitForBlockedIssuesLoadedAsync(int timeoutMs = 15_000)
    {
        await _page.WaitForFunctionAsync(
            "() => { " +
            "  const badge = document.querySelector('[data-testid=\"attention-blocked-count\"]'); " +
            "  return badge && badge.textContent.trim() !== 'checking\u2026'; " +
            "}",
            null,
            new() { Timeout = timeoutMs });
    }

    /// <summary>Returns the parsed integer count from the blocked-issues badge, or null if loading/absent.</summary>
    public async Task<int?> GetBlockedCountAsync()
    {
        var text = await GetBlockedCountTextAsync();
        return text is not null && int.TryParse(text, out var n) ? n : null;
    }

    /// <summary>Returns true if a blocked-issues row for the given identifier is visible.</summary>
    public async Task<bool> IsBlockedRowVisibleAsync(string identifier)
        => await _page.Locator($"[data-testid='attention-blocked-row-{identifier}']").CountAsync() > 0;

    /// <summary>Returns the text content of the blocked row's subline (e.g. "Blocked by #99").</summary>
    public async Task<string?> GetBlockedRowSublineAsync(string identifier)
    {
        var row = _page.Locator($"[data-testid='attention-blocked-row-{identifier}']");
        if (await row.CountAsync() == 0)
            return null;
        // The subline is the second div inside the row
        var subline = row.Locator("div > div:nth-child(2)");
        if (await subline.CountAsync() == 0)
            return null;
        return (await subline.TextContentAsync())?.Trim();
    }

    /// <summary>
    /// Reads the attention badge count from the top bar.
    /// Must be called AFTER the Attention page has loaded and called State.SetAttentionCount.
    /// </summary>
    public async Task<int?> GetTopBarBadgeCountAsync()
    {
        var badge = _page.Locator("[data-testid='topbar-attention-badge']");
        if (await badge.CountAsync() == 0)
            return null;
        var text = (await badge.TextContentAsync())?.Trim() ?? "";
        // Format: "N need attention" or "Attention" (when 0)
        if (text == "Attention")
            return 0;
        // Extract the leading number
        var parts = text.Split(' ');
        return int.TryParse(parts[0], out var n) ? n : 0;
    }
}
