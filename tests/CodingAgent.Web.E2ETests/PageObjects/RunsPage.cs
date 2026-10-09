using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /runs page — the paginated run history with outcome tabs
/// (All / Completed / Failed / Cancelled), Type and Initiated-by column filters,
/// Feedback-only checkbox, sortable column headers (Result, Type, Duration, When),
/// Prev/Next pager, and row navigation to /runs/{id}.
///
/// Key DOM elements:
/// - .cockpit-segmented button            — outcome tabs (All / Completed / Failed / Cancelled)
/// - input[aria-label='Feedback only']    — Feedback only checkbox
/// - select[aria-label='Filter by Type']  — Type column filter
/// - select[aria-label='Filter by Initiated by'] — Initiated by column filter
/// - button.sort-header                   — sortable column headers
/// - tbody tr                             — run rows
/// - td .step-badge                       — outcome badge
/// - td .runs-link                        — issue link
/// - td .runs-link-pr                     — PR link
/// - button:has-text('Prev')              — previous page
/// - button:has-text('Next')              — next page
/// - .cockpit-pager-info                  — pager info text
/// </summary>
public sealed class RunsPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    private const int DefaultNavigationTimeout = 15_000;
    private const int DefaultWaitMs = 2_000;

    public RunsPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public async Task NavigateAsync()
    {
        await _page.GotoCockpitPageAsync($"{_baseUrl}/runs");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = DefaultNavigationTimeout });
    }

    // ── Outcome tabs ──────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the segmented outcome tab whose label matches <paramref name="tabName"/>
    /// (e.g. "All", "Completed", "Failed", "Cancelled") and waits for the server reload.
    /// </summary>
    public async Task SelectTabAsync(string tabName)
    {
        await _page.Locator($".cockpit-segmented button:has-text('{tabName}')").ClickAsync();
        await _page.WaitForTimeoutAsync(DefaultWaitMs);
    }

    // ── Client-side column filters ────────────────────────────────────────────

    /// <summary>
    /// Selects a value in the "Filter by Type" dropdown. Pass an empty string to reset to "All".
    /// Corresponds to values "Implementation", "Review", "Decomposition", or "" (All).
    /// </summary>
    // TODO [WARNING]: The 500ms wait after selection is a fixed sleep. This is Blazor Server, so even
    // a "client-side" filter is a SignalR round-trip. Under CI load the re-render may not land within
    // 500ms, causing intermittent flaky counts. Replace the fixed timeout with a condition wait on an
    // observable DOM change (e.g. wait for the expected row count, or wait for a loading indicator to
    // disappear) to make this resilient under load.
    public async Task SelectTypeFilterAsync(string value)
    {
        await _page.SelectOptionAsync("[aria-label='Filter by Type']", value);
        // Client-side filter — no server round-trip; a brief yield lets Blazor rerender.
        await _page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Selects a value in the "Filter by Initiated by" dropdown.
    /// Corresponds to values "loop:issue", "loop:review", "loop:decomposition", "manual", or "" (All).
    /// </summary>
    // TODO [WARNING]: Same fixed-sleep caveat as SelectTypeFilterAsync — Blazor Server re-renders
    // via SignalR; 500ms is usually adequate but can be insufficient under CI load. See above.
    public async Task SelectInitiatedByFilterAsync(string value)
    {
        await _page.SelectOptionAsync("[aria-label='Filter by Initiated by']", value);
        await _page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Resets both client-side column filters by clicking the "Clear filters" link,
    /// which is only present when at least one client-side filter is active and the filtered
    /// result is empty. For non-empty results with active filters, set each filter dropdown
    /// to an empty string individually.
    /// </summary>
    // TODO [WARNING]: This method only works when the filtered result is EMPTY. Runs.razor renders the
    // "Clear filters" button exclusively inside the empty-state branch (_filteredRuns.Count == 0).
    // When a non-empty set of rows still passes the filter the button does not exist in the DOM and
    // WaitForAsync will throw TimeoutException after 5 s. For non-empty filtered results, call
    // SelectTypeFilterAsync("") and SelectInitiatedByFilterAsync("") individually instead.
    public async Task ClearClientFiltersViaButtonAsync()
    {
        var btn = _page.Locator("button.cockpit-link-btn:has-text('Clear filters')");
        await btn.WaitForAsync(new() { Timeout = 5_000 });
        await btn.ClickAsync();
        await _page.WaitForTimeoutAsync(500);
    }

    // ── Feedback-only checkbox ────────────────────────────────────────────────

    /// <summary>
    /// Sets the "Feedback only" checkbox to <paramref name="checked"/>. If it is already
    /// in the desired state, does nothing.
    /// </summary>
    public async Task SetFeedbackOnlyAsync(bool @checked)
    {
        var checkbox = _page.Locator("input[aria-label='Feedback only']");
        var isChecked = await checkbox.IsCheckedAsync();
        if (isChecked != @checked)
        {
            await checkbox.ClickAsync();
            await _page.WaitForTimeoutAsync(DefaultWaitMs);
        }
    }

    // ── Column sorting ────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the sort header button for <paramref name="columnName"/>
    /// (e.g. "Result", "Type", "Duration", "When") and waits for a re-render.
    /// Two consecutive clicks toggle the sort direction.
    /// </summary>
    // TODO [WARNING]: The 500ms wait is a fixed sleep. Blazor Server re-renders via SignalR, so under
    // CI load rows may not have updated within 500ms. Replace with a condition wait (e.g. detect that a
    // sort-active indicator appears, or poll until expected first-row value changes) for CI resilience.
    public async Task ClickSortHeaderAsync(string columnName)
    {
        await _page.Locator($"button.sort-header:has-text('{columnName}')").ClickAsync();
        await _page.WaitForTimeoutAsync(500);
    }

    // ── Row queries ───────────────────────────────────────────────────────────

    /// <summary>
    /// Row locator filtered by the run identifier (matches "#identifier" in the Run column).
    /// </summary>
    private ILocator RunRow(string issueIdentifier) =>
        _page.Locator("tbody tr").Filter(new() { HasTextString = $"#{issueIdentifier}" });

    /// <summary>
    /// Returns the text of the <c>.step-badge</c> element in the row for the given issue identifier.
    /// Waits for the row to be visible.
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
    /// Returns true when the run row for <paramref name="issueIdentifier"/> is present in
    /// the current DOM. Does not wait — evaluates the DOM at call time.
    /// </summary>
    public async Task<bool> IsRunVisibleAsync(string issueIdentifier)
        => await RunRow(issueIdentifier).CountAsync() > 0;

    /// <summary>
    /// Returns the total number of data rows currently rendered in the table body.
    /// </summary>
    public async Task<int> GetRowCountAsync()
        => await _page.Locator("tbody tr").CountAsync();

    /// <summary>
    /// Returns all issue identifiers rendered in the current page, in DOM order (top to bottom).
    /// Reads the bold "#identifier" span inside the Run column.
    /// </summary>
    // TODO [WARNING]: td:nth-child(4) is a positional selector. Any reorder or insertion of a column
    // before the Run column (column 4) will silently return wrong data from all callers
    // (Scenario 4, 4b). Add a stable hook such as a data attribute (data-col="run") or a dedicated
    // CSS class on the Razor template instead of relying on position.
    // TODO [WARNING]: The `if (hashIdx >= 0)` guard silently drops any cell whose text does not
    // contain '#'. If the Run-cell markup changes so the first span no longer includes '#', the
    // returned list will be shorter than expected and paging assertions (Assert.Equal(pageSize,
    // page1Ids.Count), Distinct().Count() == 60) will fail with a confusing count mismatch rather
    // than a clear parsing-failure message. Consider throwing if hashIdx < 0 for a fast-fail.
    public async Task<IReadOnlyList<string>> GetVisibleIssueIdentifiersAsync()
    {
        var runCells = _page.Locator("tbody tr td:nth-child(4) span:first-child");
        var count = await runCells.CountAsync();
        var ids = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var text = (await runCells.Nth(i).InnerTextAsync()).Trim();
            // Remove leading "PR " or "#" prefix; the raw identifier is after "#"
            var hashIdx = text.IndexOf('#');
            if (hashIdx >= 0)
                ids.Add(text[(hashIdx + 1)..].Trim());
        }
        return ids;
    }

    /// <summary>
    /// Returns the text in the "When" column cell of the given row (zero-based index),
    /// specifically the <c>title</c> attribute of the "When" td that contains the ISO timestamp.
    /// Use this to compare ordering precisely rather than the relative "X ago" display text.
    /// Falls back to the cell's text content when the title attribute is absent.
    /// </summary>
    // TODO [WARNING]: The selector td[title*='-'][title*=':'] is a heuristic. It happens to be
    // unique today because no other <td> carries a title with both a dash and a colon. If a future
    // column adds a <td title="..."> with a date-like value (e.g. a tooltip showing a timestamp),
    // .First could silently select the wrong cell. Replace with a stable hook such as a dedicated
    // CSS class (e.g. .runs-when-cell) or a data attribute (data-col="when") on the Razor template.
    // Note: this method is currently unused by any test.
    public async Task<string?> GetWhenTitleForRowAsync(int rowIndex)
    {
        // When column is the 7th <td> (1-indexed) when the Tokens column is absent, or 8th when present.
        // Use the td with a title attribute that looks like a date (contains a dash and colon).
        var row = _page.Locator("tbody tr").Nth(rowIndex);
        var whenCell = row.Locator("td[title*='-'][title*=':']").First;
        if (await whenCell.CountAsync() == 0)
            return null;
        return await whenCell.GetAttributeAsync("title");
    }

    /// <summary>
    /// Returns the text content of the Duration cell for the row at <paramref name="rowIndex"/>.
    /// Duration is the 5th column (1-indexed) — "running", "Xs", "Xm Ys", or "Xh Ym".
    /// </summary>
    // TODO [WARNING]: td:nth-child(5) is a positional selector. If a column is inserted before
    // position 5 in Runs.razor, this silently reads the wrong cell. The conditional Tokens column
    // sits at position 6 today so it does not shift column 5, but any future reorder would break
    // this. Add a stable hook (data-col="duration" or a dedicated CSS class) on the Razor template.
    // Note: this method is currently unused by any test.
    public async Task<string?> GetDurationTextForRowAsync(int rowIndex)
    {
        // Duration column is the 5th td.
        var cell = _page.Locator("tbody tr").Nth(rowIndex).Locator("td:nth-child(5)");
        if (await cell.CountAsync() == 0)
            return null;
        return (await cell.InnerTextAsync()).Trim();
    }

    // ── Row click navigation ──────────────────────────────────────────────────

    /// <summary>
    /// Clicks the row for <paramref name="issueIdentifier"/> and waits for navigation to /runs/{id}.
    /// Asserts that the URL changes before returning; raises <see cref="TimeoutException"/> if
    /// navigation does not happen within the default timeout.
    /// </summary>
    public async Task ClickRunRowAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = 10_000 });
        // Start the URL-wait task BEFORE the click so no history.pushState event is missed.
        // Blazor's NavigationManager fires history.pushState (not a network "load" event) —
        // starting WaitForURLAsync after ClickAsync creates a race where the SPA navigation
        // can complete before the listener is attached, causing a 15s timeout waiting for "Load".
        // Use Commit (not Load) to match Blazor SPA navigation semantics.
        var navTask = _page.WaitForURLAsync("**/runs/*",
            new() { WaitUntil = WaitUntilState.Commit, Timeout = DefaultNavigationTimeout });
        await row.ClickAsync();
        await navTask;
    }

    // ── Links column ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the href of the issue link in the row for <paramref name="issueIdentifier"/>,
    /// or null if no issue link is rendered in that row.
    /// </summary>
    public async Task<string?> GetIssueLinkHrefAsync(string issueIdentifier)
    {
        var link = RunRow(issueIdentifier).First.Locator("a.runs-link:not(.runs-link-pr)").First;
        if (await link.CountAsync() == 0)
            return null;
        return await link.GetAttributeAsync("href");
    }

    /// <summary>
    /// Returns the href of the PR link in the row for <paramref name="issueIdentifier"/>,
    /// or null if no PR link is rendered in that row.
    /// </summary>
    public async Task<string?> GetPrLinkHrefAsync(string issueIdentifier)
    {
        var link = RunRow(issueIdentifier).First.Locator("a.runs-link-pr").First;
        if (await link.CountAsync() == 0)
            return null;
        return await link.GetAttributeAsync("href");
    }

    // ── Pager ─────────────────────────────────────────────────────────────────

    /// <summary>Returns true when the "Prev" button is disabled (currently on page 1).</summary>
    public async Task<bool> IsPrevDisabledAsync()
        => await _page.Locator("button.cockpit-pager-btn:has-text('Prev')").IsDisabledAsync();

    /// <summary>Returns true when the "Next" button is disabled (currently on the last page).</summary>
    public async Task<bool> IsNextDisabledAsync()
        => await _page.Locator("button.cockpit-pager-btn:has-text('Next')").IsDisabledAsync();

    /// <summary>Clicks "Next" and waits for the next page to load.</summary>
    public async Task GoToNextPageAsync()
    {
        await _page.Locator("button.cockpit-pager-btn:has-text('Next')").ClickAsync();
        await _page.WaitForTimeoutAsync(DefaultWaitMs);
    }

    /// <summary>Clicks "Prev" and waits for the previous page to load.</summary>
    public async Task GoToPrevPageAsync()
    {
        await _page.Locator("button.cockpit-pager-btn:has-text('Prev')").ClickAsync();
        await _page.WaitForTimeoutAsync(DefaultWaitMs);
    }

    /// <summary>Returns the text content of the pager info element (e.g. "Page 1 · 25 shown").</summary>
    public async Task<string> GetPagerInfoTextAsync()
        => (await _page.Locator(".cockpit-pager-info").InnerTextAsync()).Trim();

    // ── Empty state ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the "No runs match the current filters" empty-state message is visible.
    /// This appears when server results exist but all are filtered out client-side.
    /// </summary>
    public async Task<bool> IsEmptyFilterStateVisibleAsync()
    {
        var el = _page.Locator(".cockpit-empty:has-text('No runs match the current filters')");
        return await el.CountAsync() > 0;
    }
}
