using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /runs page — the paginated run history with outcome tabs
/// (All / Completed / Failed / Cancelled), type and initiated-by filter dropdowns,
/// a "Feedback only" checkbox, sortable columns, paging, and row navigation.
/// </summary>
public sealed class RunsPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    private const int DefaultTimeout = 15_000;

    public RunsPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/runs");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = DefaultTimeout });
        await _page.WaitForTimeoutAsync(2000);
    }

    // ── Outcome tabs ──────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the segmented outcome tab whose label matches <paramref name="tabName"/>
    /// (e.g. "All", "Completed", "Failed", "Cancelled") and waits briefly for the page to reload.
    /// </summary>
    public async Task SelectTabAsync(string tabName)
    {
        await _page.Locator($".cockpit-segmented button:has-text('{tabName}')").ClickAsync();
        await _page.WaitForTimeoutAsync(2000);
    }

    // ── Row helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Row locator for the given issue identifier. Uses the #identifier pattern rendered by Runs.razor.
    /// </summary>
    private ILocator RunRow(string issueIdentifier) =>
        _page.Locator("tbody tr").Filter(new() { HasTextString = $"#{issueIdentifier}" });

    /// <summary>
    /// Returns the text of the <c>.step-badge</c> element in the row for the given issue identifier.
    /// Asserts the row is visible before reading.
    /// </summary>
    public async Task<string> GetBadgeLabelForRunAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = DefaultTimeout });
        // InnerTextAsync returns the browser-rendered text, which may be uppercased by CSS
        // text-transform on .step-badge. Normalize to title-case so callers can compare
        // directly against RunOutcomeDisplay.Label() values without caring about CSS transforms.
        var text = await row.Locator(".step-badge").InnerTextAsync();
        var trimmed = text.Trim();
        return trimmed.Length == 0
            ? trimmed
            : char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }

    /// <summary>
    /// Returns true when the run row for <paramref name="issueIdentifier"/> is visible on
    /// the current tab. Does not wait — evaluates the DOM at call time.
    /// </summary>
    // TODO [WARNING]: CountAsync() counts DOM nodes regardless of CSS visibility — a hidden row
    // (e.g. display:none) would still return true. If the Runs page hides rows without removing
    // them from the DOM, this method would give false positives. Consider using
    // Locator.IsVisibleAsync() or combining Filter with IsVisibleAsync for a stricter check.
    public async Task<bool> IsRunVisibleAsync(string issueIdentifier)
        => await RunRow(issueIdentifier).CountAsync() > 0;

    // ── Row count ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the number of data rows currently visible in the table body.
    /// The table is only rendered when <c>_filteredRuns.Count &gt; 0</c> in <c>Runs.razor</c>;
    /// returns 0 when the page is in the empty-state or loading state.
    /// </summary>
    public async Task<int> GetRowCountAsync()
    {
        var tbody = _page.Locator("tbody tr");
        return await tbody.CountAsync();
    }

    // ── Issue IDs ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the issue identifiers for all rows currently visible on the page, in DOM order.
    /// Strips the <c>#</c> prefix (and <c>PR </c> prefix for Review runs) so callers compare
    /// against bare identifiers such as <c>"42"</c>, not <c>"#42"</c>.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetVisibleIssueIdsAsync()
    {
        // Each row renders a bold span containing "#issueId" (or "PR #issueId" for Review runs).
        // The span has inline style="font-weight: 600;" and is the first <span> inside the 4th <td>.
        // We locate them by looking for spans inside tbody whose text starts with "#" or "PR #".
        var spans = _page.Locator("tbody tr td span[style*='font-weight']");
        var count = await spans.CountAsync();
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var text = (await spans.Nth(i).InnerTextAsync()).Trim();
            // Strip "PR " prefix for review runs, then strip "#" prefix
            if (text.StartsWith("PR #", StringComparison.Ordinal))
                text = text["PR #".Length..];
            else if (text.StartsWith("#", StringComparison.Ordinal))
                text = text[1..];
            result.Add(text);
        }
        return result.AsReadOnly();
    }

    // ── Column values ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the text content of cells in the given 1-based column index across all visible rows.
    /// Useful for asserting sort order on arbitrary columns.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetColumnValuesAsync(int columnIndex)
    {
        var cells = _page.Locator($"tbody tr td:nth-child({columnIndex})");
        var count = await cells.CountAsync();
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
            result.Add((await cells.Nth(i).InnerTextAsync()).Trim());
        return result.AsReadOnly();
    }

    // ── Wait helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Waits until the page is no longer in the loading state.
    /// Runs.razor renders <c>&lt;div class="cockpit-empty"&gt;Loading runs…&lt;/div&gt;</c>
    /// while <c>_loading == true</c>. This waits until that specific text is gone, then
    /// waits until either a table body or a non-loading empty-state is present.
    /// </summary>
    private async Task WaitForRunsLoadAsync()
    {
        // First: wait until the "Loading runs…" cockpit-empty is gone
        await _page.WaitForFunctionAsync(
            "() => ![...document.querySelectorAll('.cockpit-empty')].some(e => e.textContent.includes('Loading runs'))",
            null,
            new() { Timeout = DefaultTimeout });
        // Then: ensure we have either a table body or a stable empty state
        await _page.WaitForFunctionAsync(
            "() => document.querySelector('tbody') !== null || document.querySelector('.cockpit-empty') !== null",
            null,
            new() { Timeout = DefaultTimeout });
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Selects a value in the "Filter by Type" dropdown (e.g. <c>"Review"</c>, <c>"Implementation"</c>,
    /// <c>"Decomposition"</c>, or <c>""</c> for All). Waits for the table to re-render.
    /// </summary>
    public async Task SelectTypeFilterAsync(string value)
    {
        await _page.SelectOptionAsync("select[aria-label='Filter by Type']",
            new SelectOptionValue { Value = value });
        // Type filter is client-side — Blazor synchronously recomputes _filteredRuns and
        // re-renders. Wait for the loading state to clear (in case the select triggers a reload).
        await WaitForRunsLoadAsync();
    }

    /// <summary>
    /// Selects a value in the "Filter by Initiated by" dropdown
    /// (e.g. <c>"loop:issue"</c>, <c>"manual"</c>, or <c>""</c> for All).
    /// </summary>
    public async Task SelectInitiatedByFilterAsync(string value)
    {
        await _page.SelectOptionAsync("select[aria-label='Filter by Initiated by']",
            new SelectOptionValue { Value = value });
        await WaitForRunsLoadAsync();
    }

    /// <summary>
    /// Toggles the "Feedback only" checkbox. This is a server-side filter — clicking it calls
    /// <c>ToggleFeedbackOnly</c> which reloads from the API. Waits for the reload to complete.
    /// </summary>
    public async Task ToggleFeedbackOnlyAsync()
    {
        await _page.Locator("label.cockpit-filter-toggle input[type='checkbox']").ClickAsync();
        // Server-side reload: wait for the loading state to appear then clear
        await WaitForRunsLoadAsync();
    }

    /// <summary>
    /// Clicks the "Clear filters" button. This button is only present in the DOM when
    /// <c>_filteredRuns.Count == 0</c> (the no-rows-match empty state). Call this only after
    /// applying filters that produce zero visible rows.
    /// </summary>
    public async Task ClearFiltersAsync()
    {
        var btn = _page.Locator("button.cockpit-link-btn:has-text('Clear filters')");
        await btn.WaitForAsync(new() { Timeout = DefaultTimeout });
        await btn.ClickAsync();
        // ResetClientFilters is synchronous — no server reload. Wait for stable state.
        await WaitForRunsLoadAsync();
    }

    // ── Sort headers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the sort header button whose visible text contains <paramref name="columnName"/>
    /// (e.g. <c>"When"</c>, <c>"Duration"</c>, <c>"Result"</c>, <c>"Type"</c>).
    /// <para>
    /// Sorting calls <c>ToggleSort</c> → <c>PersistSortToUrl</c> →
    /// <c>Nav.NavigateTo(uri, replace:true)</c>. This is an in-place URL rewrite, NOT a full
    /// navigation — <c>WaitForNavigationAsync</c> will not fire. We wait for the sort indicator
    /// arrow (<c>↑</c> or <c>↓</c>) to appear in the clicked header as a stable DOM signal.
    /// </para>
    /// </summary>
    public async Task ClickSortHeaderAsync(string columnName)
    {
        await _page.Locator($"button.sort-header:has-text('{columnName}')").ClickAsync();
        // Wait for the sort indicator (↑ or ↓) to appear in the header, confirming re-render.
        // TODO [WARNING]: This querySelector is not scoped to the clicked column — it is satisfied
        // by a sort indicator already present on any other column before the click. If a sort
        // indicator is already in the DOM (e.g. from a previous sort click), this wait completes
        // immediately without confirming the new sort has taken effect, making subsequent
        // GetVisibleIssueIdsAsync calls potentially racy. Fix: scope the wait to the specific
        // header button for columnName, or wait for the indicator to disappear then reappear.
        await _page.WaitForFunctionAsync(
            $"() => !!document.querySelector('button.sort-header span.sort-indicator')",
            null,
            new() { Timeout = DefaultTimeout });
    }

    // ── Paging ────────────────────────────────────────────────────────────────

    /// <summary>Returns true when the "Prev" pager button is disabled.</summary>
    public async Task<bool> IsPrevDisabledAsync()
    {
        var btn = _page.Locator("button.cockpit-pager-btn:has-text('Prev')");
        return await btn.IsDisabledAsync();
    }

    /// <summary>Returns true when the "Next" pager button is disabled.</summary>
    public async Task<bool> IsNextDisabledAsync()
    {
        var btn = _page.Locator("button.cockpit-pager-btn:has-text('Next')");
        return await btn.IsDisabledAsync();
    }

    /// <summary>Clicks "Next" and waits for the page data to reload.</summary>
    public async Task ClickNextPageAsync()
    {
        // TODO [WARNING]: Replace WaitForTimeoutAsync(2000) with WaitForRunsLoadAsync() for
        // deterministic synchronisation. Paging triggers a server-side reload (LoadAsync) —
        // a fixed sleep is fragile on slow CI and wastes time on fast machines.
        await _page.Locator("button.cockpit-pager-btn:has-text('Next')").ClickAsync();
        await _page.WaitForTimeoutAsync(2000);
    }

    /// <summary>Clicks "Prev" and waits for the page data to reload.</summary>
    public async Task ClickPrevPageAsync()
    {
        // TODO [WARNING]: Replace WaitForTimeoutAsync(2000) with WaitForRunsLoadAsync() for
        // deterministic synchronisation. Paging triggers a server-side reload (LoadAsync) —
        // a fixed sleep is fragile on slow CI and wastes time on fast machines.
        await _page.Locator("button.cockpit-pager-btn:has-text('Prev')").ClickAsync();
        await _page.WaitForTimeoutAsync(2000);
    }

    // ── Row navigation ────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the clickable data row whose run column contains the given issue identifier.
    /// Only non-Consolidation rows have the <c>monitoring-row-clickable</c> class and trigger navigation.
    /// </summary>
    public async Task ClickRowAsync(string issueIdentifier)
    {
        var row = _page.Locator("tr.monitoring-row-clickable")
            .Filter(new() { HasTextString = $"#{issueIdentifier}" })
            .First;
        await row.WaitForAsync(new() { Timeout = DefaultTimeout });
        await row.ClickAsync();
    }

    /// <summary>Returns the current browser URL.</summary>
    public Task<string> GetCurrentUrlAsync() => Task.FromResult(_page.Url);

    // ── Links ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <c>href</c> of the issue link (the non-PR link) in the row for
    /// <paramref name="issueIdentifier"/>, or null if absent.
    /// </summary>
    public async Task<string?> GetIssueLinkHrefAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = DefaultTimeout });
        var link = row.Locator("td.runs-links-cell a.runs-link:not(.runs-link-pr)");
        if (await link.CountAsync() == 0)
            return null;
        return await link.GetAttributeAsync("href");
    }

    /// <summary>
    /// Returns the <c>href</c> of the PR link in the row for <paramref name="issueIdentifier"/>,
    /// or null if absent.
    /// </summary>
    public async Task<string?> GetPrLinkHrefAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = DefaultTimeout });
        var link = row.Locator("td.runs-links-cell a.runs-link-pr");
        if (await link.CountAsync() == 0)
            return null;
        return await link.GetAttributeAsync("href");
    }
}
