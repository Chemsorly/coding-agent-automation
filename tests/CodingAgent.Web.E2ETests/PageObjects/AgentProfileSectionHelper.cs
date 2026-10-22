using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object helper for the Agent Profiles section of the Settings page (spec 053): a profile is a job template, the
/// settings of that template's tool and MCP servers.
///
/// Notes:
/// - The Enabled field is a plain checkbox, not a toggle-switch.
/// - After FillAsync on @bind inputs, BlurAsync() is required to trigger Blazor's onchange handler.
/// - When the form is open, <c>button.btn-add</c> inside the form is the "Add MCP Server" button.
/// </summary>
public sealed class AgentProfileSectionHelper
{
    private readonly IPage _page;

    public AgentProfileSectionHelper(IPage page) => _page = page;

    private ILocator Form => _page.Locator("div.provider-form").First;

    private ILocator DisplayNameInput =>
        _page.Locator("div.form-group:has(label:has-text('Display Name')) input[type='text']");

    private ILocator JobTemplateSelect => _page.Locator("[data-testid='profile-job-template']");

    private ILocator AgentNameInput => _page.Locator("[data-testid='profile-agent-name']");

    private ILocator EnabledCheckbox => Form.Locator(":scope > div.form-checkbox input[type='checkbox']");

    private ILocator SaveButton => Form.Locator(":scope > div.form-buttons button.btn-save");

    private ILocator DeleteConfirmDialog => _page.Locator("div.agent-detail-confirm");

    private ILocator Row(string displayName) =>
        _page.Locator("tr[data-testid^='profile-row-']").Filter(new() { Has = _page.Locator($"td:first-child:text-is('{displayName}')") });

    /// <summary>Clicks the outer "+ Add Agent Profile" button (only valid when the form is closed).</summary>
    public async Task ClickAddAsync()
    {
        await _page.ClickAsync("button.btn-add:has-text('Add Agent Profile')");
        await Form.WaitForAsync(new() { Timeout = 5_000 });
    }

    public async Task FillDisplayNameAsync(string name)
    {
        await DisplayNameInput.FillAsync(name);
        await DisplayNameInput.BlurAsync();
    }

    /// <summary>Selects the Helm job template by its key; the form then shows that template's tool settings.</summary>
    public Task SelectJobTemplateAsync(string key) => JobTemplateSelect.SelectOptionAsync(new SelectOptionValue { Value = key });

    /// <summary>The Kiro tool's Agent Name setting; present only for a Kiro job template.</summary>
    public async Task FillAgentNameAsync(string agentName)
    {
        await AgentNameInput.FillAsync(agentName);
        await AgentNameInput.BlurAsync();
    }

    public Task<bool> IsAgentNameShownAsync() => AgentNameInput.IsVisibleAsync();

    public async Task SetEnabledAsync(bool enabled)
    {
        if (await EnabledCheckbox.IsCheckedAsync() != enabled)
            await EnabledCheckbox.ClickAsync();
    }

    /// <summary>Clicks Save and waits for the form to close.</summary>
    public async Task SaveAsync()
    {
        await SaveButton.ClickAsync();
        await Form.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
    }

    /// <summary>Clicks Save and returns the error the form shows (the API's refusal).</summary>
    public async Task<string> SaveExpectingErrorAsync()
    {
        await SaveButton.ClickAsync();
        var error = Form.Locator("div.settings-status.status-error");
        await error.WaitForAsync(new() { Timeout = 5_000 });
        return (await error.TextContentAsync())?.Trim() ?? "";
    }

    public async Task EditRowAsync(string displayName)
    {
        await Row(displayName).Locator("button.btn-edit").ClickAsync();
        await Form.WaitForAsync(new() { Timeout = 5_000 });
    }

    /// <summary>
    /// Clicks Delete on the named row, confirms, and waits for the outcome: the "deleted" toast, or the refusal of a
    /// profile that routes name.
    /// </summary>
    public async Task DeleteRowAsync(string displayName)
    {
        await Row(displayName).Locator("button.btn-delete").ClickAsync();
        await DeleteConfirmDialog.WaitForAsync(new() { Timeout = 3_000 });
        await DeleteConfirmDialog.Locator("button.btn-delete").ClickAsync();
        await _page.Locator("div.inline-status, div.settings-status")
            .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex("deleted|Failed to delete") })
            .First.WaitForAsync(new() { Timeout = 10_000 });
    }

    /// <summary>The Display Name of every row in the table.</summary>
    public async Task<IReadOnlyList<string>> GetTableRowNamesAsync() =>
        (await _page.Locator("tr[data-testid^='profile-row-'] td:first-child").AllTextContentsAsync()).Select(t => t.Trim()).ToList();

    /// <summary>The job template cell of the named row: the key, the tool badge and any flag.</summary>
    public async Task<string> GetJobTemplateCellAsync(string displayName) =>
        (await Row(displayName).Locator("td").Nth(1).TextContentAsync())?.Trim() ?? "";

    /// <summary>True when the Enabled cell of the named row shows the enabled icon.</summary>
    public async Task<bool> IsRowEnabledAsync(string displayName) =>
        await Row(displayName).Locator("td").Nth(3).Locator("span.status-icon.is-on").CountAsync() > 0;

    /// <summary>True when a status toast or inline status containing the text is visible.</summary>
    public async Task<bool> IsStatusMessageVisibleAsync(string containsText)
    {
        var status = _page.Locator("div.inline-status, div.settings-status");
        foreach (var text in await status.AllTextContentsAsync())
        {
            if (text.Contains(containsText, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
