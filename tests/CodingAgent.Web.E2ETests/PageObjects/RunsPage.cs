using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /runs page — the paginated run history with outcome tabs
/// (All / Completed / Failed / Cancelled), Type and Initiated-by filter dropdowns,
/// a "Feedback only" checkbox, sortable column headers, paging, and row navigation.
/// </summary>
public sealed class RunsPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public RunsPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to /runs and waits for the page heading to render.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/runs");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        // TODO [WARNING]: The fixed 2000 ms sleep is unreliable under CI load — Blazor may not
        // have finished rendering the table rows by the time the sleep elapses. Prefer waiting
        // for an observable DOM condition (e.g. WaitForSelectorAsync("tbody tr") or
        // WaitForFunctionAsync that checks row count > 0) instead of an arbitrary timeout.
        await _page.WaitForTimeoutAsync(2000);
    }

    // ── Outcome tabs ──────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the segmented outcome tab whose label matches <paramref name="tabName"/>
    /// (e.g. "All", "Completed", "Failed", "Cancelled") and waits for the page to reload.
    /// </summary>
    public async Task SelectTabAsync(string tabName)
    {
        await _page.Locator($".cockpit-segmented button:has-text('{tabName}')").ClickAsync();
        // TODO [WARNING]: Fixed 1500 ms sleep — can expire before Blazor finishes re-rendering
        // under CI load. Prefer WaitForSelectorAsync / WaitForFunctionAsync on a stable DOM
        // condition (e.g. wait for the active tab button to gain the selected CSS class).
        await _page.WaitForTimeoutAsync(1500);
    }

    /// <summary>
    /// Returns the number of rows visible in the table body on the current page/tab.
    /// Returns 0 when the empty-state placeholder is shown instead of a table.
    /// </summary>
    public async Task<int> GetRowCountAsync()
    {
        // If there's no tbody at all, the table hasn't rendered (empty state).
        var count = await _page.Locator("tbody tr").CountAsync();
        return count;
    }

    // ── Column filters ────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the "Filter by Type" dropdown to <paramref name="value"/>
    /// (e.g. "", "Implementation", "Review", "Decomposition").
    /// </summary>
    public async Task SetTypeFilterAsync(string value)
    {
        await _page.Locator("select[aria-label='Filter by Type']").SelectOptionAsync(value);
        await _page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Sets the "Filter by Initiated by" dropdown to <paramref name="value"/>
    /// (e.g. "", "loop:issue", "loop:review", "loop:decomposition", "manual").
    /// </summary>
    public async Task SetInitiatedByFilterAsync(string value)
    {
        await _page.Locator("select[aria-label='Filter by Initiated by']").SelectOptionAsync(value);
        await _page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Toggles the "Feedback only" checkbox to <paramref name="check"/>.
    /// Waits briefly for the server-side reload when enabling (feedbackOnly triggers a re-query).
    /// </summary>
    public async Task SetFeedbackOnlyAsync(bool check)
    {
        var checkbox = _page.Locator("input[aria-label='Feedback only']");
        var current = await checkbox.IsCheckedAsync();
        if (current != check)
        {
            await checkbox.ClickAsync();
            // TODO [WARNING]: Fixed 1500 ms sleep for the server-side re-query triggered by
            // feedbackOnly. This can expire before Blazor finishes re-rendering under CI load.
            // Prefer WaitForFunctionAsync on an observable post-reload condition.
            await _page.WaitForTimeoutAsync(1500);
        }
    }

    /// <summary>
    /// Clicks the "Clear filters" link in the empty-state message, if present.
    /// </summary>
    public async Task ClearClientFiltersAsync()
    {
        await _page.Locator("button.cockpit-link-btn:has-text('Clear filters')").ClickAsync();
        await _page.WaitForTimeoutAsync(500);
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the sort header button for <paramref name="columnName"/>
    /// (e.g. "When", "Duration", "Result", "Type") once.
    /// The first click sets the column; a second click toggles direction.
    /// </summary>
    public async Task ClickSortHeaderAsync(string columnName)
    {
        await _page.Locator($"button.sort-header:has-text('{columnName}')").ClickAsync();
        await _page.WaitForTimeoutAsync(500);
    }

    // ── Paging ────────────────────────────────────────────────────────────────

    /// <summary>Returns true when the "Prev" paging button is enabled (page > 1).</summary>
    public async Task<bool> IsPrevEnabledAsync()
    {
        var btn = _page.Locator("button.cockpit-pager-btn:has-text('Prev')");
        return !await btn.IsDisabledAsync();
    }

    /// <summary>Returns true when the "Next" paging button is enabled (more pages exist).</summary>
    public async Task<bool> IsNextEnabledAsync()
    {
        var btn = _page.Locator("button.cockpit-pager-btn:has-text('Next')");
        return !await btn.IsDisabledAsync();
    }

    /// <summary>Clicks "Next" and waits for the page to reload.</summary>
    public async Task ClickNextAsync()
    {
        await _page.Locator("button.cockpit-pager-btn:has-text('Next')").ClickAsync();
        // TODO [WARNING]: Fixed 1500 ms sleep — can expire before Blazor finishes re-rendering
        // under CI load. Prefer WaitForFunctionAsync on an observable post-reload condition.
        await _page.WaitForTimeoutAsync(1500);
    }

    /// <summary>Clicks "Prev" and waits for the page to reload.</summary>
    public async Task ClickPrevAsync()
    {
        await _page.Locator("button.cockpit-pager-btn:has-text('Prev')").ClickAsync();
        // TODO [WARNING]: Fixed 1500 ms sleep — can expire before Blazor finishes re-rendering
        // under CI load. Prefer WaitForFunctionAsync on an observable post-reload condition.
        await _page.WaitForTimeoutAsync(1500);
    }

    // ── Row data ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Row locator for the given issue identifier. Matches rows whose text contains #<paramref name="issueIdentifier"/>.
    /// </summary>
    private ILocator RunRow(string issueIdentifier) =>
        _page.Locator("tbody tr").Filter(new() { HasTextString = $"#{issueIdentifier}" });

    /// <summary>
    /// Returns the text of the <c>.step-badge</c> element in the row for the given issue identifier.
    /// Normalises CSS text-transform by title-casing the result.
    /// </summary>
    public async Task<string> GetBadgeLabelForRunAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = 10_000 });
        var text = await row.Locator(".step-badge").InnerTextAsync();
        var trimmed = text.Trim();
        return trimmed.Length == 0
            ? trimmed
            : char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }

    /// <summary>
    /// Returns true when the run row for <paramref name="issueIdentifier"/> is visible on
    /// the current tab/page. Does not wait — evaluates the DOM at call time.
    /// </summary>
    public async Task<bool> IsRunVisibleAsync(string issueIdentifier)
        => await RunRow(issueIdentifier).CountAsync() > 0;

    /// <summary>
    /// Returns the text content of each cell in the column at zero-based <paramref name="colIndex"/>
    /// for all currently visible rows. Useful for asserting sort order.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetColumnValuesAsync(int colIndex)
    {
        var cells = await _page.Locator($"tbody tr td:nth-child({colIndex + 1})").AllInnerTextsAsync();
        return cells.Select(t => t.Trim()).ToList();
    }

    /// <summary>
    /// Returns the <c>href</c> of the issue link (cockpit-link icon) in the row for
    /// <paramref name="issueIdentifier"/>, or null if absent.
    /// </summary>
    public async Task<string?> GetIssueLinkHrefAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        var link = row.Locator("a.runs-link").First;
        if (await link.CountAsync() == 0) return null;
        return await link.GetAttributeAsync("href");
    }

    /// <summary>
    /// Returns the <c>href</c> of the PR link (cockpit-link-pr icon) in the row for
    /// <paramref name="issueIdentifier"/>, or null if absent.
    /// </summary>
    public async Task<string?> GetPrLinkHrefAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        var link = row.Locator("a.runs-link-pr").First;
        if (await link.CountAsync() == 0) return null;
        return await link.GetAttributeAsync("href");
    }

    /// <summary>
    /// Clicks the run row for <paramref name="issueIdentifier"/> (which triggers navigation to
    /// /runs/{runId}) and waits for the new page to settle.
    /// </summary>
    public async Task ClickRunRowAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = 10_000 });
        await row.ClickAsync();
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await _page.WaitForTimeoutAsync(1000);
    }

    /// <summary>Returns the current URL of the page (for asserting post-navigation URL).</summary>
    public string CurrentUrl => _page.Url;

    /// <summary>
    /// Returns all issue identifiers rendered in the run column of the current page, in DOM order.
    /// The run column contains "#&lt;identifier&gt;" text.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetVisibleIssueIdentifiersAsync()
    {
        // TODO [WARNING]: This parsing is fragile for Review runs. Runs.razor renders Review run
        // identifiers as "PR #R000" (i.e. "PR " prefix + "#" + identifier). The chained
        // .TrimStart('#').TrimStart('P').TrimStart('R') on "PR #R000" produces " #R000" →
        // after .Trim() → "#R000", which still contains the leading '#'. Consequently
        // IndexOf("R000") returns -1 for all Review run identifiers, silently weakening
        // sort-order and overlap assertions. Replace with a precise locator or a regex
        // (e.g. Regex.Match(t, @"#(\S+)").Groups[1].Value) to extract the bare identifier
        // regardless of the "PR " prefix.
        // The Run column cell contains a span with font-weight:600 holding "#<identifier>"
        var spans = _page.Locator("tbody tr td span[style*='font-weight']");
        var texts = await spans.AllInnerTextsAsync();
        return texts
            .Select(t => t.Trim().TrimStart('#').TrimStart('P').TrimStart('R').Trim())
            .Where(t => t.Length > 0)
            .ToList();
    }
}
