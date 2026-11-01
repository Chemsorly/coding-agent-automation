using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the new-triage form at <c>/triage/new</c>.
/// Exposes locators and actions; asserts nothing.
/// </summary>
public sealed class TriageNewPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public TriageNewPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to /triage/new and waits for the page to be ready.</summary>
    public async Task NavigateAsync() =>
        await _page.GotoCockpitPageAsync($"{_baseUrl}/triage/new");

    /// <summary>
    /// Waits until the preview card no longer shows its "Loading…" hint.
    /// Uses Playwright's built-in retry so no fixed delay is needed.
    /// </summary>
    public async Task WaitForPreviewAsync() =>
        await Assertions.Expect(_page.Locator("[data-testid=triage-preview]"))
                        .Not.ToContainTextAsync("Loading…");

    // ── Required fields ──────────────────────────────────────────────────

    /// <summary>Fills the three required fields in one call.</summary>
    public async Task FillRequiredAsync(string title, string happened, string expected)
    {
        await Title.FillAsync(title);
        await Happened.FillAsync(happened);
        await Expected.FillAsync(expected);
    }

    /// <summary>The title input (<c>data-testid=triage-title</c>). Binds on <c>oninput</c>.</summary>
    public ILocator Title => _page.Locator("[data-testid=triage-title]");

    /// <summary>The what-happened textarea (<c>data-testid=triage-happened</c>). Binds on <c>oninput</c>.</summary>
    public ILocator Happened => _page.Locator("[data-testid=triage-happened]");

    /// <summary>The expected textarea (<c>data-testid=triage-expected</c>). Binds on <c>oninput</c>.</summary>
    public ILocator Expected => _page.Locator("[data-testid=triage-expected]");

    /// <summary>The start / submit button (<c>data-testid=triage-start-button</c>).</summary>
    public ILocator StartButton => _page.Locator("[data-testid=triage-start-button]");

    // ── When presets ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the when-preset chip button whose visible label matches <paramref name="label"/>
    /// (e.g. "Last hour", "Today", "Last 7 days", "Custom").
    /// </summary>
    public ILocator WhenPreset(string label) =>
        _page.Locator(".triage-chipbtn").Filter(new LocatorFilterOptions { HasText = label });

    /// <summary>Clicks the when-preset chip button identified by <paramref name="label"/>.</summary>
    public async Task SelectWhenAsync(string label) =>
        await WhenPreset(label).ClickAsync();

    // ── Optional date-time fields (visible only when Custom preset is active) ──

    /// <summary>The "First seen" datetime-local input (<c>#triage-from</c>).</summary>
    public ILocator From => _page.Locator("#triage-from");

    /// <summary>The "Until" datetime-local input (<c>#triage-until</c>).</summary>
    public ILocator Until => _page.Locator("#triage-until");

    // ── Optional context fields ───────────────────────────────────────────

    /// <summary>The environment text input (<c>#triage-env</c>). Binds on change.</summary>
    public ILocator Environment => _page.Locator("#triage-env");

    /// <summary>The "Where" text input (<c>#triage-where</c>). Binds on change.</summary>
    public ILocator Where => _page.Locator("#triage-where");

    // ── Preview card ─────────────────────────────────────────────────────

    /// <summary>The preview card container (<c>data-testid=triage-preview</c>).</summary>
    public ILocator Preview => _page.Locator("[data-testid=triage-preview]");

    /// <summary>
    /// The preview problem warnbox (<c>data-testid=triage-preview-problem</c>).
    /// Shown either when no executor template exists or when a route refusal is present.
    /// </summary>
    public ILocator PreviewProblem => _page.Locator("[data-testid=triage-preview-problem]");

    /// <summary>The similar triages card (<c>data-testid=triage-similar</c>).</summary>
    public ILocator Similar => _page.Locator("[data-testid=triage-similar]");

    // ── No-project state ─────────────────────────────────────────────────

    /// <summary>
    /// The no-project empty state element (<c>data-testid=triage-new-no-project</c>).
    /// Shown when the current user has no operator project available.
    /// </summary>
    public ILocator NoProject => _page.Locator("[data-testid=triage-new-no-project]");
}
