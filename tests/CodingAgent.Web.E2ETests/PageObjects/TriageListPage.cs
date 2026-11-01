using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the triage list at <c>/triage</c>.
/// Exposes locators and actions; asserts nothing.
/// </summary>
public sealed class TriageListPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public TriageListPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to /triage and waits for the page to be ready.</summary>
    public async Task NavigateAsync() =>
        // TODO: Triage.razor is project-scoped (queries with State.SelectedProjectId). For a fresh
        // admin context the localStorage restore resolves to all-projects (same scope), so the
        // seeded default-project records are always visible and ToHaveCountAsync auto-retries.
        // If the fixture's default scope ever changes, add WaitForProjectSwitcherRestoredAsync()
        // here (as ProjectSwitcherTests does) to avoid reading stale data before the restore fires.
        await _page.GotoCockpitPageAsync($"{_baseUrl}/triage");

    // ── Row / empty state ────────────────────────────────────────────────

    /// <summary>All triage rows currently visible in the table.</summary>
    public ILocator Rows => _page.Locator("[data-testid=triage-row]");

    /// <summary>The empty-state element shown when no rows match.</summary>
    public ILocator Empty => _page.Locator("[data-testid=triage-empty]");

    /// <summary>The "New triage" link in the toolbar.</summary>
    public ILocator NewTriageLink => _page.Locator("[data-testid=triage-new]");

    // ── Tabs ─────────────────────────────────────────────────────────────

    /// <summary>Returns the tab button whose <c>data-testid</c> is <c>triage-tab-{testIdSuffix}</c>.</summary>
    public ILocator Tab(string testIdSuffix) =>
        _page.Locator($"[data-testid=triage-tab-{testIdSuffix}]");

    /// <summary>Clicks the tab identified by <paramref name="testIdSuffix"/>.</summary>
    public async Task ClickTabAsync(string testIdSuffix) =>
        await Tab(testIdSuffix).ClickAsync();

    // ── Source filter ────────────────────────────────────────────────────

    /// <summary>
    /// Clicks a source filter button by its exact text label.
    /// Valid labels: <c>"All sources"</c>, <c>"Operator"</c>, <c>"Tracker issue"</c>.
    /// </summary>
    public async Task ClickSourceAsync(string text) =>
        await _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = text, Exact = true }).ClickAsync();

    // ── Search ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fills the search box with <paramref name="text"/> and presses Tab to fire the
    /// <c>@onchange</c> handler in Triage.razor.
    /// </summary>
    public async Task SearchAsync(string text)
    {
        var input = _page.Locator("[data-testid=triage-search]");
        await input.FillAsync(text);
        await input.PressAsync("Tab");
    }

    // ── Pager ────────────────────────────────────────────────────────────

    /// <summary>The pager info text element (<c>.cockpit-pager-info</c>).</summary>
    public ILocator PagerInfo => _page.Locator(".cockpit-pager-info");

    /// <summary>The previous-page button (first <c>.cockpit-pager-btn</c>).</summary>
    public ILocator PrevButton => _page.Locator(".cockpit-pager-btn").First;

    /// <summary>The next-page button (last <c>.cockpit-pager-btn</c>).</summary>
    public ILocator NextButton => _page.Locator(".cockpit-pager-btn").Last;

    /// <summary>Clicks the next-page button.</summary>
    public async Task NextPageAsync() => await NextButton.ClickAsync();

    /// <summary>Clicks the previous-page button.</summary>
    public async Task PrevPageAsync() => await PrevButton.ClickAsync();
}
