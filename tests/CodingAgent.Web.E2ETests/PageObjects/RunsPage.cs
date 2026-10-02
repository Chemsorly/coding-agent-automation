using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /runs page — the paginated run history with outcome tabs
/// (All / Completed / Failed / Cancelled), type and initiated-by filters,
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

    // ── Navigation ────────────────────────────────────────────────────────────

    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/runs");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
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

    /// <summary>Returns the text of the currently active outcome tab.</summary>
    public async Task<string?> GetActiveTabAsync()
        => await _page.Locator(".cockpit-segmented button.is-active").InnerTextAsync();

    // ── Row queries ───────────────────────────────────────────────────────────

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
        await row.WaitForAsync(new() { Timeout = 10_000 });
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
    public async Task<bool> IsRunVisibleAsync(string issueIdentifier)
        => await RunRow(issueIdentifier).CountAsync() > 0;

    /// <summary>Returns the total number of rows currently displayed in the table body.</summary>
    public async Task<int> GetRowCountAsync()
        => await _page.Locator("tbody tr").CountAsync();

    /// <summary>
    /// Returns all visible issue identifiers (the text after '#' in each row's Run cell),
    /// in the order they appear in the DOM.
    /// </summary>
    // TODO [WARNING]: The JS selector `span[style*="font-weight: 600"]` is coupled to an inline
    // style on the identifier cell. If Runs.razor switches to a CSS class for bold text, this
    // selector silently returns no spans and the method returns an empty list — causing all sort
    // and paging ordering assertions to pass vacuously with a 0-element list. Consider using a
    // data attribute (e.g. data-issue-id) on the identifier cell and selecting by that instead.
    public async Task<List<string>> GetVisibleIssueIdentifiersAsync()
    {
        // Use string[] instead of List<string>: Playwright's EvaluateAsync deserializer throws
        // NullReferenceException when deserializing an empty JS array into List<T> in some
        // Playwright versions. string[] deserializes correctly and handles the empty case.
        var result = await _page.EvaluateAsync<string[]?>(@"() => {
            const rows = document.querySelectorAll('tbody tr');
            const ids = [];
            for (const row of rows) {
                const spans = row.querySelectorAll('td span[style*=""font-weight: 600""]');
                for (const s of spans) {
                    const text = s.textContent.trim();
                    // Row cells: #IDENTIFIER or PR #IDENTIFIER
                    const match = text.match(/#([^\s]+)$/);
                    if (match) { ids.push(match[1]); break; }
                }
            }
            return ids;
        }");
        return result?.ToList() ?? [];
    }

    /// <summary>
    /// Returns the StartedAt tooltip text (the full datetime in the 'When' cell title attribute)
    /// for the given issue identifier's row.
    /// </summary>
    public async Task<string?> GetWhenTitleAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = 10_000 });
        // The "When" column td has a `title` attribute with the full datetime
        return await row.EvaluateAsync<string?>(@"row => {
            const cells = row.querySelectorAll('td');
            // Find the td with a title that looks like a datetime (has ':' and '-')
            for (const cell of cells) {
                const title = cell.getAttribute('title') || '';
                if (title.includes(':') && title.includes('-')) return title;
            }
            return null;
        }");
    }

    // ── Type filter ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the "Filter by Type" column-header dropdown to the given value
    /// (e.g. "", "Implementation", "Review", "Decomposition").
    /// </summary>
    public async Task SetTypeFilterAsync(string value)
    {
        await _page.Locator("select[aria-label='Filter by Type']").SelectOptionAsync(value);
        await _page.WaitForTimeoutAsync(500);
    }

    // ── Initiated-by filter ───────────────────────────────────────────────────

    /// <summary>
    /// Sets the "Filter by Initiated by" column-header dropdown.
    /// Pass "" to clear, or e.g. "loop:issue", "manual".
    /// </summary>
    public async Task SetInitiatedByFilterAsync(string value)
    {
        await _page.Locator("select[aria-label='Filter by Initiated by']").SelectOptionAsync(value);
        await _page.WaitForTimeoutAsync(500);
    }

    // ── Feedback-only toggle ──────────────────────────────────────────────────

    /// <summary>Checks the "Feedback only" checkbox if not already checked, then waits.</summary>
    public async Task EnableFeedbackOnlyAsync()
    {
        var chk = _page.Locator("label.cockpit-filter-toggle input[type='checkbox']");
        if (!await chk.IsCheckedAsync())
        {
            await chk.CheckAsync();
            await _page.WaitForTimeoutAsync(1500);
        }
    }

    /// <summary>Unchecks the "Feedback only" checkbox if checked, then waits.</summary>
    public async Task DisableFeedbackOnlyAsync()
    {
        var chk = _page.Locator("label.cockpit-filter-toggle input[type='checkbox']");
        if (await chk.IsCheckedAsync())
        {
            await chk.UncheckAsync();
            await _page.WaitForTimeoutAsync(1500);
        }
    }

    // ── Sort headers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the sort header button for the given column label
    /// (e.g. "WHEN", "DURATION", "RESULT", "TYPE" — case-insensitive).
    /// </summary>
    public async Task ClickSortHeaderAsync(string columnLabel)
    {
        await _page.Locator($"button.sort-header:has-text('{columnLabel}')").First.ClickAsync();
        await _page.WaitForTimeoutAsync(500);
    }

    /// <summary>Returns true if the current sort indicator for the given column is ascending (↑).</summary>
    public async Task<bool?> GetSortDirectionAsync(string columnLabel)
    {
        var indicator = await _page.Locator(
            $"button.sort-header:has-text('{columnLabel}') .sort-indicator").First.InnerTextAsync();
        if (indicator?.Contains("↑") == true) return true;
        if (indicator?.Contains("↓") == true) return false;
        return null; // column not currently sorted
    }

    // ── Paging ────────────────────────────────────────────────────────────────

    /// <summary>Clicks the "Prev" pager button and waits for the page to update.</summary>
    public async Task ClickPrevAsync()
    {
        await _page.Locator("button.cockpit-pager-btn:has-text('Prev')").ClickAsync();
        await _page.WaitForTimeoutAsync(1500);
    }

    /// <summary>Clicks the "Next" pager button and waits for the page to update.</summary>
    public async Task ClickNextAsync()
    {
        await _page.Locator("button.cockpit-pager-btn:has-text('Next')").ClickAsync();
        await _page.WaitForTimeoutAsync(1500);
    }

    /// <summary>Returns true when the "Prev" pager button is disabled.</summary>
    public async Task<bool> IsPrevDisabledAsync()
        => await _page.Locator("button.cockpit-pager-btn:has-text('Prev')").IsDisabledAsync();

    /// <summary>Returns true when the "Next" pager button is disabled.</summary>
    public async Task<bool> IsNextDisabledAsync()
        => await _page.Locator("button.cockpit-pager-btn:has-text('Next')").IsDisabledAsync();

    /// <summary>Returns the pager info text (e.g. "Page 1 · 25 shown").</summary>
    public async Task<string?> GetPagerInfoAsync()
        => await _page.Locator(".cockpit-pager-info").InnerTextAsync();

    // ── Row navigation ────────────────────────────────────────────────────────

    /// <summary>Clicks the row for the given issue identifier and waits for navigation.</summary>
    public async Task ClickRowAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = 10_000 });
        await row.ClickAsync();
        // Blazor Server navigation is SignalR-driven: WaitForLoadStateAsync(NetworkIdle) resolves
        // before the Blazor client applies the URL change, leaving Page.Url still on /runs.
        // Wait for the URL to change to the run detail route instead.
        await _page.WaitForURLAsync(url => url.Contains("/runs/"), new() { Timeout = 15_000 });
    }

    // ── Links column ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the href of the issue link in the Links column for the given run row, or null.
    /// </summary>
    public async Task<string?> GetIssueLinkHrefAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = 10_000 });
        // Use timeout:0 so GetAttributeAsync returns null immediately when the link is absent
        // (e.g. when IssueUrl is null), rather than waiting 30s and throwing a TimeoutException.
        return await row.Locator("td.runs-links-cell a[title^='Open issue']")
            .GetAttributeAsync("href", new() { Timeout = 0 });
    }

    /// <summary>
    /// Returns the href of the PR link in the Links column for the given run row, or null.
    /// </summary>
    public async Task<string?> GetPrLinkHrefAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = 10_000 });
        // Use timeout:0 so GetAttributeAsync returns null immediately when the link is absent.
        return await row.Locator("td.runs-links-cell a.runs-link-pr")
            .GetAttributeAsync("href", new() { Timeout = 0 });
    }

    // ── Empty / filter states ─────────────────────────────────────────────────

    /// <summary>Returns true if the "No runs match the current filters" empty state is visible.</summary>
    public async Task<bool> IsFilterEmptyStateVisibleAsync()
        => await _page.Locator(".cockpit-empty:has-text('No runs match the current filters')").IsVisibleAsync();

    /// <summary>Clicks the "Clear filters" link in the filter-empty-state.</summary>
    public async Task ClearFiltersAsync()
    {
        await _page.Locator("button.cockpit-link-btn:has-text('Clear filters')").ClickAsync();
        await _page.WaitForTimeoutAsync(500);
    }
}
