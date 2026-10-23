using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for Settings → Projects (the "Manage" list at /settings?section=projects).
/// </summary>
public sealed class ProjectsManagePage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public ProjectsManagePage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    // ── Navigation ───────────────────────────────────────────────────────

    /// <summary>Navigates to Settings → Projects and waits for the Add Project button.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoCockpitPageAsync($"{_baseUrl}/settings?section=projects");
        await _page.WaitForSelectorAsync("[data-testid='projects-add']", new() { Timeout = 15_000 });
    }

    // ── Actions ──────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the Add Project button, waits for navigation to /projects/*, and returns the new project ID.
    /// </summary>
    public async Task<string> ClickAddProjectAsync()
    {
        await _page.ClickAsync("[data-testid='projects-add']");
        await _page.WaitForURLAsync("**/projects/*", new() { Timeout = 15_000 });
        var url = _page.Url;
        return url[(url.LastIndexOf("/projects/", StringComparison.Ordinal) + "/projects/".Length)..];
    }

    /// <summary>
    /// Clicks the Delete button for <paramref name="projectId"/>, confirms in the panel,
    /// and waits until the row is removed from the DOM.
    /// </summary>
    public async Task DeleteAsync(string projectId)
    {
        await DeleteButton(projectId).ClickAsync();
        await _page.WaitForSelectorAsync("[data-testid='project-delete-confirm']", new() { Timeout = 10_000 });
        await _page.ClickAsync("[data-testid='project-delete-confirm-button']");
        await Row(projectId).WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 15_000 });
    }

    // ── Locators ─────────────────────────────────────────────────────────

    /// <summary>The project row <c>&lt;tr&gt;</c> for the given project ID.</summary>
    public ILocator Row(string projectId) =>
        _page.Locator($"[data-testid='project-row'][data-project-id='{projectId}']");

    /// <summary>The Delete button inside the project row for the given project ID.</summary>
    public ILocator DeleteButton(string projectId) =>
        Row(projectId).Locator("[data-testid='project-delete']");

    /// <summary>
    /// The project's link in the Settings tree nav at
    /// <c>SettingsTreeNav.razor</c> line 47: <c>&lt;a class="tree-node" href="/projects/{id}"&gt;</c>.
    /// </summary>
    public ILocator TreeLink(string projectId) =>
        _page.Locator($"a.tree-node[href='/projects/{projectId}']");
}
