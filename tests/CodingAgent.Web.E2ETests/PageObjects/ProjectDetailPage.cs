using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the project detail page at <c>/projects/{id}</c>.
/// Wraps the Overview tab fields exposed by <c>ProjectDetailSection.razor</c> and the
/// template list rendered in the Templates tab.
/// </summary>
public sealed class ProjectDetailPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public ProjectDetailPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    // ── Navigation ───────────────────────────────────────────────────────

    /// <summary>
    /// Navigates to <c>/projects/{projectId}</c> and waits for the Overview tab button.
    /// Does NOT wait for the <c>&lt;h2&gt;</c> text — a new, unsaved project has an empty name.
    /// </summary>
    public async Task NavigateAsync(string projectId)
    {
        await _page.GotoCockpitPageAsync($"{_baseUrl}/projects/{projectId}");
        await _page.WaitForSelectorAsync(".tab-btn:has-text('Overview')", new() { Timeout = 15_000 });
    }

    // ── Tab navigation ───────────────────────────────────────────────────

    /// <summary>
    /// Clicks the tab button with the given text. For <c>"Templates"</c>, also waits for
    /// <c>.template-add-row</c> to confirm the tab content has loaded.
    /// </summary>
    public async Task SelectTabAsync(string tabText)
    {
        await _page.ClickAsync($".tab-btn:text-is('{tabText}')");
        if (tabText == "Templates")
        {
            await _page.WaitForSelectorAsync(".template-add-row", new() { Timeout = 10_000 });
        }
    }

    // ── Overview tab actions ─────────────────────────────────────────────

    /// <summary>
    /// Sets the Name field value and explicitly dispatches the <c>change</c> event so that
    /// Blazor Server's <c>@bind</c> handler updates <c>_name</c> before the save button
    /// is clicked. Without this, the SignalR round-trip for the <c>onchange</c> binding may
    /// not complete before <c>SaveOverviewAsync</c> runs on the server.
    /// </summary>
    public async Task FillNameAsync(string name)
    {
        await _page.FillAsync("[data-testid='project-name-input']", name);
        await _page.Locator("[data-testid='project-name-input']").DispatchEventAsync("change");
    }

    /// <summary>
    /// Sets the Description field value and explicitly dispatches the <c>change</c> event so
    /// that Blazor Server's <c>@bind</c> handler updates <c>_description</c> before the save
    /// button is clicked.
    /// </summary>
    public async Task FillDescriptionAsync(string description)
    {
        await _page.FillAsync("[data-testid='project-description-input']", description);
        await _page.Locator("[data-testid='project-description-input']").DispatchEventAsync("change");
    }

    /// <summary>
    /// Selects the epic issue provider by its config ID (matches the <c>value</c> attribute
    /// of the option, which is <c>provider.Id</c>) and waits briefly for Blazor Server to
    /// process the <c>@onchange</c> event via SignalR before <c>SaveOverviewAsync</c> is called.
    /// Pattern from <c>ProjectSwitcher.SelectByValueAsync</c>.
    /// </summary>
    public async Task SelectEpicIssueProviderAsync(string providerId)
    {
        await _page.SelectOptionAsync("[data-testid='project-epic-provider-select']", providerId);
        // Brief wait for Blazor to process the @onchange event and update _epicIssueProviderId.
        await _page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Clicks Save Overview and waits for the success inline-status message.
    /// Note: the <c>InlineStatus</c> component lives in <c>ProjectPage.razor</c> (the wrapper),
    /// not inside <c>ProjectDetailSection.razor</c>.
    /// </summary>
    public async Task SaveOverviewAsync()
    {
        await _page.ClickAsync("[data-testid='project-save-overview']");
        await _page.WaitForSelectorAsync(".inline-status-success:has-text('Project overview saved.')", new() { Timeout = 10_000 });
    }

    // ── Templates tab ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the display names of all templates in the list, in DOM order.
    /// Returns an empty list when the project has no templates (the <c>.template-list</c>
    /// element is not rendered).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetTemplateNamesAsync()
    {
        var list = _page.Locator(".template-list");
        if (await list.CountAsync() == 0)
            return [];

        var names = list.Locator(".template-name");
        var count = await names.CountAsync();
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var text = await names.Nth(i).InnerTextAsync();
            result.Add(text.Trim());
        }
        return result;
    }

    /// <summary>
    /// Moves the template named <paramref name="templateName"/> to the project identified by
    /// <paramref name="targetProjectId"/> using the move select in the template row.
    /// </summary>
    public async Task MoveTemplateAsync(string templateName, string targetProjectId) =>
        await _page.SelectOptionAsync(
            $".template-row:has(.template-name:text-is('{templateName}')) select.template-move-select",
            targetProjectId);

    // ── Locators ─────────────────────────────────────────────────────────

    /// <summary>The inline-status element (success or error) on the page.</summary>
    public ILocator Status => _page.Locator(".inline-status");
}
