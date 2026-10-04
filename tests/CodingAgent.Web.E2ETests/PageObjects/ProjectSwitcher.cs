using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the project-scope switcher in the cockpit top bar.
///
/// The switcher renders as a <c>&lt;label class="cockpit-project-switcher"&gt;</c> containing
/// a <c>&lt;select aria-label="Project scope"&gt;</c>. The select has one fixed option
/// ("All projects" = "") followed by one option per project.
///
/// localStorage key: <c>cockpit.selectedProjectId</c>.
/// - Absent/null → first visit; no scope applied.
/// - "" → "All projects" explicitly selected.
/// - &lt;guid&gt; → that project is active.
///
/// At narrow viewports (≤ 375 px) the switcher is visible inside the top bar; the hamburger
/// toggle shows/hides the sidebar nav, not the top bar.
/// </summary>
public sealed class ProjectSwitcher
{
    private readonly IPage _page;

    private const int DefaultTimeout = 15_000;

    public ProjectSwitcher(IPage page)
    {
        _page = page;
    }

    /// <summary>The <c>&lt;select&gt;</c> element for the project scope.</summary>
    private ILocator Select => _page.Locator("select[aria-label='Project scope']");

    // ── Read ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the currently selected project ID, or "" when "All projects" is selected.
    /// Waits up to <see cref="DefaultTimeout"/> ms for the element to appear.
    /// </summary>
    public async Task<string> GetSelectedValueAsync()
    {
        await Select.WaitForAsync(new() { Timeout = DefaultTimeout });
        return await Select.InputValueAsync() ?? "";
    }

    /// <summary>
    /// Returns the display text of the currently selected option (e.g. "All projects" or a project name).
    /// </summary>
    public async Task<string> GetSelectedTextAsync()
    {
        await Select.WaitForAsync(new() { Timeout = DefaultTimeout });
        var value = await Select.InputValueAsync() ?? "";
        // TODO: [WARNING] The `value` argument passed to EvaluateAsync is never used inside the JS
        // lambda — the function re-queries the DOM directly and ignores its parameter. The `value`
        // variable is dead code here and could mislead future callers into thinking the method
        // queries the text of a specific value rather than always reading the current selection.
        // Fix: either remove the unused `value` argument or rewrite the JS to use `s.querySelector('option[value="'+sel+'"]')`.
        return await _page.EvaluateAsync<string>(
            "(sel) => { const s = document.querySelector(\"select[aria-label='Project scope']\"); " +
            "           return s ? s.options[s.selectedIndex].text : ''; }",
            value);
    }

    /// <summary>
    /// Returns all available option texts in the switcher (including "All projects").
    /// </summary>
    public async Task<IReadOnlyList<string>> GetOptionTextsAsync()
    {
        await Select.WaitForAsync(new() { Timeout = DefaultTimeout });
        return await _page.EvaluateAsync<string[]>(
            "() => { const s = document.querySelector(\"select[aria-label='Project scope']\"); " +
            "        return s ? [...s.options].map(o => o.text) : []; }");
    }

    /// <summary>
    /// Returns true when the switcher element is present in the DOM and visible.
    /// </summary>
    public async Task<bool> IsVisibleAsync()
        => await Select.IsVisibleAsync();

    // ── Write ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Selects "All projects" (value = "").
    /// Waits for the element to be interactive before interacting.
    /// </summary>
    public Task SelectAllProjectsAsync()
        => SelectByValueAsync("");

    /// <summary>
    /// Selects the given project by its ID (the option's value attribute).
    /// Waits for the element to be interactive before interacting.
    /// </summary>
    public async Task SelectByValueAsync(string projectId)
    {
        await Select.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeout });
        await _page.WaitForBlazorAsync(DefaultTimeout);
        await Select.SelectOptionAsync(new SelectOptionValue { Value = projectId });
        // Brief wait for Blazor to process the @onchange event and trigger OnProjectChanged.
        await _page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Selects the project whose display name equals <paramref name="projectName"/>.
    /// </summary>
    public async Task SelectByNameAsync(string projectName)
    {
        await Select.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeout });
        await _page.WaitForBlazorAsync(DefaultTimeout);
        await Select.SelectOptionAsync(new SelectOptionValue { Label = projectName });
        await _page.WaitForTimeoutAsync(500);
    }

    // ── localStorage helpers ──────────────────────────────────────────────

    /// <summary>
    /// Reads <c>cockpit.selectedProjectId</c> directly from <c>localStorage</c>.
    /// Returns null when the key is absent (first visit), "" when "All projects" was saved.
    /// </summary>
    public Task<string?> ReadLocalStorageAsync()
        => _page.EvaluateAsync<string?>(
            "() => localStorage.getItem('cockpit.selectedProjectId')");

    /// <summary>
    /// Writes <paramref name="value"/> to <c>cockpit.selectedProjectId</c> in localStorage
    /// WITHOUT going through the Blazor component. Use this to simulate a stale/pre-loaded value.
    /// </summary>
    public Task WriteLocalStorageAsync(string? value)
        => _page.EvaluateAsync(
            "(v) => { if (v === null) localStorage.removeItem('cockpit.selectedProjectId'); " +
            "         else localStorage.setItem('cockpit.selectedProjectId', v); }",
            value);
}
