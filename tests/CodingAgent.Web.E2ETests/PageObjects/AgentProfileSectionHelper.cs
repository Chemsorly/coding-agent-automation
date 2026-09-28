using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object helper for the Agent Profiles section of the Settings page.
/// Encapsulates CRUD interactions: table inspection, add/edit/delete form, and status assertion.
///
/// CSS selector notes:
/// - No data-testid attributes exist on this component; all selectors use CSS classes and text.
/// - The Enabled field is a plain checkbox (<c>div.form-checkbox input[type='checkbox']</c>),
///   NOT a toggle-switch (that pattern is used by QualityGateConfigSection and ReviewerConfigSection).
/// - After FillAsync on @bind inputs, BlurAsync() is required to trigger Blazor's onchange handler.
/// - When the form is open, <c>button.btn-add</c> inside <c>div.provider-form</c> is the
///   "Add MCP Server" button — never click it in CRUD tests that don't test MCP management.
/// </summary>
public sealed class AgentProfileSectionHelper
{
    private readonly IPage _page;

    public AgentProfileSectionHelper(IPage page) => _page = page;

    // ── Form container ────────────────────────────────────────────────────

    private ILocator Form => _page.Locator("div.provider-form");

    // ── Form inputs ───────────────────────────────────────────────────────

    private ILocator DisplayNameInput =>
        _page.Locator("div.form-group:has(label:has-text('Display Name')) input[type='text']");

    private ILocator MatchLabelsInput =>
        _page.Locator("div.form-group:has(label:has-text('Match Labels')) input[type='text']");

    // Only one <select> in the main form body (before the MCP sub-section)
    private ILocator AgentProviderSelect => _page.Locator("div.provider-form select");

    // AgentProfileSection uses a plain checkbox, not a toggle-switch span
    private ILocator EnabledCheckbox => _page.Locator("div.form-checkbox input[type='checkbox']");

    private ILocator PriorityInput =>
        _page.Locator("div.form-group:has(label:has-text('Priority')) input[type='number']");

    private ILocator SaveButton => _page.Locator("div.form-buttons button.btn-save");

    private ILocator CancelButton => _page.Locator("div.form-buttons button.btn-cancel");

    // ── Delete confirm dialog ─────────────────────────────────────────────

    private ILocator DeleteConfirmDialog => _page.Locator("div.agent-detail-confirm");

    // ── Public methods ────────────────────────────────────────────────────

    /// <summary>Clicks the outer "+ Add Agent Profile" button (only valid when form is closed).</summary>
    public async Task ClickAddAsync()
    {
        // The outer button.btn-add is only present when the form is closed.
        // When the form is open, btn-add inside div.provider-form is "Add MCP Server" — don't click that.
        await _page.ClickAsync("button.btn-add");
        await Form.WaitForAsync(new() { Timeout = 5_000 });
    }

    /// <summary>Fills the Display Name input and blurs to trigger Blazor @bind onchange.</summary>
    public async Task FillDisplayNameAsync(string name)
    {
        await DisplayNameInput.FillAsync(name);
        await DisplayNameInput.BlurAsync();
    }

    /// <summary>Fills the Match Labels input (comma-separated) and blurs.</summary>
    public async Task FillMatchLabelsAsync(string labels)
    {
        await MatchLabelsInput.FillAsync(labels);
        await MatchLabelsInput.BlurAsync();
    }

    /// <summary>Selects the Agent Provider by its display name in the select element.</summary>
    public async Task SelectAgentProviderAsync(string displayName)
    {
        await AgentProviderSelect.SelectOptionAsync(new SelectOptionValue { Label = displayName });
    }

    /// <summary>Sets the Enabled plain checkbox state.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        var isChecked = await EnabledCheckbox.IsCheckedAsync();
        if (isChecked != enabled)
            await EnabledCheckbox.ClickAsync();
    }

    /// <summary>Fills the Priority number input and blurs.</summary>
    public async Task FillPriorityAsync(int priority)
    {
        await PriorityInput.FillAsync(priority.ToString());
        await PriorityInput.BlurAsync();
    }

    /// <summary>Clicks Save and waits for the form to close (success) or a validation error to appear.</summary>
    public async Task SaveAsync()
    {
        await SaveButton.ClickAsync();
        // On success the form closes; on failure _formError renders inside the form.
        // Wait for the form to disappear (success path).
        // TODO [WARNING]: On validation failure the form stays open and renders _formError, so the 5s wait throws
        // a TimeoutException rather than surfacing the validation message. Consider racing this wait against
        // div.settings-status.status-error and failing with the rendered error text for diagnosability.
        await Form.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
    }

    /// <summary>Clicks Cancel and waits for the form to close.</summary>
    public async Task CancelAsync()
    {
        await CancelButton.ClickAsync();
        await Form.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
    }

    /// <summary>Clicks the Edit button on the row identified by Display Name.</summary>
    public async Task EditRowAsync(string displayName)
    {
        // TODO [WARNING]: tr:has-text() is a substring match; if two row names share a prefix, .First may
        // target the wrong row. Use an exact first-column cell match for robustness.
        var row = _page.Locator($"tr:has-text('{displayName}')").First;
        await row.Locator("button.btn-edit").ClickAsync();
        await Form.WaitForAsync(new() { Timeout = 5_000 });
    }

    /// <summary>
    /// Clicks the Delete button on the named row, waits for the confirm dialog,
    /// then confirms the deletion.
    /// </summary>
    public async Task DeleteRowAsync(string displayName)
    {
        // TODO [WARNING]: tr:has-text() is a substring match; see EditRowAsync note above.
        var row = _page.Locator($"tr:has-text('{displayName}')").First;
        await row.Locator("button.btn-delete").ClickAsync();
        await DeleteConfirmDialog.WaitForAsync(new() { Timeout = 3_000 });
        await DeleteConfirmDialog.Locator("button.btn-delete").ClickAsync();
        // TODO [WARNING]: Fixed 1s sleep; replace with DeleteConfirmDialog.WaitForAsync(Hidden) or
        // a row-disappears wait to avoid false-positive assertions on slow CI.
        await _page.WaitForTimeoutAsync(1_000);
    }

    /// <summary>Returns the Display Name text of every row currently in the table.</summary>
    public async Task<IReadOnlyList<string>> GetTableRowNamesAsync()
    {
        var cells = _page.Locator("table.monitoring-table tbody tr td:first-child");
        var count = await cells.CountAsync();
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var text = await cells.Nth(i).TextContentAsync();
            if (text is not null)
                names.Add(text.Trim());
        }
        return names;
    }

    /// <summary>
    /// Returns true if the row for the given Display Name has the enabled (is-on) status icon.
    /// </summary>
    public async Task<bool> IsRowEnabledAsync(string displayName)
    {
        var row = _page.Locator($"tr:has-text('{displayName}')").First;
        // TODO [WARNING]: AgentProfileSection renders a SECOND span.status-icon.is-on in the Template column
        // when the profile has non-empty MatchLabels ("Template matched", AgentProfileSection.razor:95).
        // This locator is not scoped to the Enabled cell — scope it to the Enabled column to eliminate the
        // latent false-positive risk (a future change keeping the template icon while disabling would fool this check).
        var enabledIcon = row.Locator("span.status-icon.is-on");
        return await enabledIcon.CountAsync() > 0;
    }

    /// <summary>Returns true if a status toast containing the given text is currently visible.</summary>
    public async Task<bool> IsStatusMessageVisibleAsync(string containsText)
    {
        var status = _page.Locator("div.inline-status, div.settings-status");
        var count = await status.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var text = await status.Nth(i).TextContentAsync();
            if (text?.Contains(containsText, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        return false;
    }
}
