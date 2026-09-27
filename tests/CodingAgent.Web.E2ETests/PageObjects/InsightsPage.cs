using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /insights page.
/// Encapsulates DOM queries for Insights headline tiles, outcome mix, by-run-type, and quality gate sections.
///
/// Key DOM structure:
/// - data-testid="insights-total"           — total run count text
/// - data-testid="insights-success-rate"    — success rate tile value (e.g. "75%" or "—")
/// - data-testid="insights-avg-cycle"       — avg cycle time tile value
/// - data-testid="insights-retry-rate"      — retry rate tile value
/// - data-testid="insights-succeeded"       — outcome mix succeeded count
/// - data-testid="insights-failed"          — outcome mix failed count
/// - data-testid="insights-cancelled"       — outcome mix cancelled count
/// - data-testid="insights-restarted"       — outcome mix restarted count (only if > 0)
/// - data-testid="insights-runtype-count-{name}" — by-run-type count for given type name
///   (type labels: "implementation", "review", "decomp-analysis", "decomposition", "consolidation")
/// - data-testid="insights-gate-no-data"    — no gate data message
/// - data-testid="insights-gate-no-failures" — no gate failures message
/// </summary>
public sealed class InsightsPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    private const int DefaultTimeout = 15_000;

    public InsightsPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to /insights and waits for the initial load.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/insights");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = DefaultTimeout });
        await WaitForLoadCompleteAsync();
    }

    /// <summary>Waits until the loading card is gone (page data loaded).</summary>
    public async Task WaitForLoadCompleteAsync()
    {
        // Wait for the Loading… card to disappear
        await _page.WaitForFunctionAsync(
            "() => !document.querySelector('.cockpit-card .cockpit-empty') || " +
            "      ![...document.querySelectorAll('.cockpit-card .cockpit-empty')].some(e => e.textContent.trim() === 'Loading\u2026')",
            null,
            new() { Timeout = DefaultTimeout });
    }

    /// <summary>Selects a time window from the dropdown. Values: "1", "6", "24", "168", "0".</summary>
    public async Task SelectWindowAsync(string windowHours)
    {
        await _page.SelectOptionAsync("[aria-label='Time window']", windowHours);
        // Wait for the re-load triggered by @bind:after
        await WaitForLoadCompleteAsync();
        // TODO [WARNING]: The WaitForTimeoutAsync(500) below is a fixed sleep that is both unreliable and wasteful.
        // Between SelectOptionAsync and Blazor Server setting _loading=true, the DOM still shows the previous
        // window's data (no loading card yet), so the first WaitForLoadCompleteAsync above returns immediately
        // before the new load begins. On a slow CI runner the 500ms may expire before the reload completes,
        // causing the second WaitForLoadCompleteAsync to pass while data is still stale. Replace with a
        // deterministic wait that polls until data-testid="insights-total" shows the newly expected count,
        // or wait for the loading card to appear before waiting for it to disappear.
        await _page.WaitForTimeoutAsync(500);
        await WaitForLoadCompleteAsync();
    }

    /// <summary>Returns the total run count, or null if the page shows "No completed runs yet."</summary>
    public async Task<int?> GetTotalAsync()
    {
        var el = _page.Locator("[data-testid='insights-total']");
        if (await el.CountAsync() == 0)
            return null;
        var text = (await el.TextContentAsync())?.Trim();
        return int.TryParse(text, out var n) ? n : null;
    }

    /// <summary>Returns the success rate as an integer (e.g. 75 for 75%), or null if "—".</summary>
    public async Task<int?> GetSuccessRateAsync()
    {
        var el = _page.Locator("[data-testid='insights-success-rate']");
        if (await el.CountAsync() == 0)
            return null;
        // TODO [WARNING]: The rendered HTML is `@successRate<span>%</span>`, so TextContentAsync concatenates
        // both text nodes (e.g. "75%"). TrimEnd('%') works here but is fragile coupling to DOM structure. If
        // the inner <span> ever renders with surrounding whitespace (e.g. "75 %"), TrimEnd('%') would leave
        // a trailing space and int.TryParse would return false, silently returning null. Use
        // text.Replace("%", "").Trim() for robustness.
        var text = (await el.TextContentAsync())?.Trim().TrimEnd('%').Trim();
        if (text == "—" || string.IsNullOrEmpty(text))
            return null;
        return int.TryParse(text, out var n) ? n : null;
    }

    /// <summary>Returns the succeeded outcome count, or null if the outcome mix is not visible.</summary>
    public async Task<int?> GetSucceededCountAsync()
        => await GetCountByTestIdAsync("insights-succeeded");

    /// <summary>Returns the failed outcome count, or null if the outcome mix is not visible.</summary>
    public async Task<int?> GetFailedCountAsync()
        => await GetCountByTestIdAsync("insights-failed");

    /// <summary>Returns the cancelled outcome count, or null if the outcome mix is not visible.</summary>
    public async Task<int?> GetCancelledCountAsync()
        => await GetCountByTestIdAsync("insights-cancelled");

    /// <summary>Returns the restarted outcome count, or 0 if not visible (only rendered when > 0).</summary>
    public async Task<int> GetRestartedCountAsync()
        => await GetCountByTestIdAsync("insights-restarted") ?? 0;

    /// <summary>
    /// Returns the count for a specific run type label.
    /// The testId suffix is derived by lowercasing the display label and replacing spaces with '-', dots removed.
    /// E.g. "Implementation" → "implementation", "Decomp. analysis" → "decomp-analysis".
    /// </summary>
    public async Task<int?> GetRunTypeCountAsync(string displayLabel)
    {
        var suffix = displayLabel.ToLowerInvariant().Replace(" ", "-").Replace(".", "");
        return await GetCountByTestIdAsync($"insights-runtype-count-{suffix}");
    }

    /// <summary>Returns true if the "No gate data for runs in this window." message is visible.</summary>
    public async Task<bool> IsNoGateDataMessageVisibleAsync()
        => await _page.Locator("[data-testid='insights-gate-no-data']").CountAsync() > 0;

    /// <summary>Returns true if the "No gate failures in N run(s) with gate data." message is visible.</summary>
    public async Task<bool> IsNoGateFailuresMessageVisibleAsync()
        => await _page.Locator("[data-testid='insights-gate-no-failures']").CountAsync() > 0;

    /// <summary>Returns the exact text of the "no gate failures" message, or null if absent.</summary>
    public async Task<string?> GetNoGateFailuresMessageTextAsync()
    {
        var el = _page.Locator("[data-testid='insights-gate-no-failures']");
        if (await el.CountAsync() == 0)
            return null;
        return (await el.TextContentAsync())?.Trim();
    }

    /// <summary>Returns the exact text of the "no gate data" message, or null if absent.</summary>
    public async Task<string?> GetNoGateDataMessageTextAsync()
    {
        var el = _page.Locator("[data-testid='insights-gate-no-data']");
        if (await el.CountAsync() == 0)
            return null;
        return (await el.TextContentAsync())?.Trim();
    }

    private async Task<int?> GetCountByTestIdAsync(string testId)
    {
        var el = _page.Locator($"[data-testid='{testId}']");
        if (await el.CountAsync() == 0)
            return null;
        var text = (await el.TextContentAsync())?.Trim();
        return int.TryParse(text, out var n) ? n : null;
    }
}
