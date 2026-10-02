using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the top-bar project scope switcher rendered by CockpitLayout.
///
/// DOM structure (CockpitLayout.razor):
///   &lt;label class="cockpit-project-switcher"&gt;
///     &lt;select aria-label="Project scope"&gt;
///       &lt;option value=""&gt;All projects&lt;/option&gt;
///       &lt;option value="{id}"&gt;{Name}&lt;/option&gt; (one per project)
///     &lt;/select&gt;
///   &lt;/label&gt;
///
/// The switcher is always present on pages that use @layout CockpitLayout.
/// </summary>
public sealed class ProjectSwitcherPage
{
    private readonly IPage _page;

    public ProjectSwitcherPage(IPage page) => _page = page;

    private ILocator SwitcherSelect => _page.Locator("select[aria-label='Project scope']");

    /// <summary>Selects a project by its display name, or "All projects" for no scope.</summary>
    public Task SelectProjectByNameAsync(string name)
        => SwitcherSelect.SelectOptionAsync(new SelectOptionValue { Label = name });

    /// <summary>Selects a project by its ID value (empty string = All projects).</summary>
    public Task SelectProjectByIdAsync(string id)
        => SwitcherSelect.SelectOptionAsync(new SelectOptionValue { Value = id });

    /// <summary>Returns the currently selected option value (project ID, or "" for All projects).</summary>
    public Task<string> GetSelectedValueAsync()
        => SwitcherSelect.InputValueAsync();

    /// <summary>
    /// Waits until the select reflects <paramref name="expectedValue"/>.
    /// Use after page reload to wait for CockpitLayout.OnAfterRenderAsync to restore from localStorage.
    ///
    /// The default 15 s timeout covers both CockpitLayout.OnInitializedAsync (project list fetch over
    /// the API) and OnAfterRenderAsync (localStorage read + State.SetProject JS interop), which must
    /// both complete before the select DOM value updates.
    ///
    /// <paramref name="expectedValue"/> is passed as the JS function argument rather than interpolated
    /// into the function body, so the polling is safe for any string value (no single-quote hazard).
    /// </summary>
    public Task WaitForValueAsync(string expectedValue, int timeoutMs = 15_000)
        => _page.WaitForFunctionAsync(
            "id => document.querySelector(\"select[aria-label='Project scope']\")?.value === id",
            expectedValue,
            new() { Timeout = timeoutMs });

    /// <summary>Returns true if the option with the given value exists in the select.</summary>
    public async Task<bool> HasOptionAsync(string value)
    {
        // TODO: The value is string-interpolated directly into the CSS attribute selector
        // (`option[value='{value}']`). A value containing a single-quote would produce a
        // malformed selector and throw a PlaywrightException rather than returning false.
        // WaitForValueAsync (above) avoids this by passing the value as a JS function argument;
        // the same discipline should be applied here (e.g. use Locator("option").Filter(...)).
        var count = await SwitcherSelect.Locator($"option[value='{value}']").CountAsync();
        return count > 0;
    }
}
