using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the repository page at /pipelines/{templateId}.
/// Encapsulates navigation, card-save actions, and field accessors for the repository page.
/// </summary>
public sealed class RepositoryPageObject
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public RepositoryPageObject(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to the repository page for the given template ID and waits for it to load.</summary>
    public async Task NavigateAsync(string templateId)
    {
        await _page.GotoCockpitPageAsync($"{_baseUrl}/pipelines/{templateId}");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
    }

    /// <summary>Returns true if the page shows "Template not found" (unknown ID or no access).</summary>
    public async Task<bool> IsNotFoundAsync()
    {
        var content = await _page.TextContentAsync("body");
        return content?.Contains("Template not found", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Returns the current h1 text (template name).</summary>
    public async Task<string?> GetTemplateNameAsync()
    {
        var h1 = await _page.QuerySelectorAsync("h1");
        return h1 is not null ? await h1.TextContentAsync() : null;
    }

    // ── Behaviour card ────────────────────────────────────────────────────

    /// <summary>Returns the current value of the steering instructions textarea in the Behaviour card.</summary>
    public async Task<string?> GetSteeringContentAsync()
    {
        var textarea = await _page.QuerySelectorAsync("[data-testid='behaviour-card'] textarea");
        return textarea is not null ? await textarea.InputValueAsync() : null;
    }

    /// <summary>Sets the steering instructions textarea in the Behaviour card to the given value.</summary>
    public async Task SetSteeringContentAsync(string value)
    {
        // Expand the repository settings section if needed (it's inside the Behaviour card)
        var textarea = _page.Locator("[data-testid='behaviour-card'] textarea").First;
        await textarea.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await textarea.FillAsync(value);
        await _page.WaitForTimeoutAsync(300); // allow Blazor event propagation
    }

    /// <summary>Clicks the Save button in the Behaviour card and waits for a success feedback.</summary>
    public async Task SaveBehaviourAsync()
    {
        var saveBtn = _page.Locator("[data-testid='behaviour-card'] button.btn-save");
        await saveBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await saveBtn.ClickAsync();
        // Wait for success or error feedback
        await _page.WaitForSelectorAsync(
            ".settings-status.status-success, .settings-status.status-error",
            new() { Timeout = 10_000 });
    }

    /// <summary>Returns the success message from the Behaviour card, if shown.</summary>
    public async Task<string?> GetBehaviourSuccessMessageAsync()
    {
        var el = await _page.QuerySelectorAsync(".settings-status.status-success");
        return el is not null ? await el.TextContentAsync() : null;
    }
}
