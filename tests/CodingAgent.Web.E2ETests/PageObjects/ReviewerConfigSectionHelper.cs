using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object helper for the Reviewer Configurations section of the Settings page.
/// Encapsulates CRUD interactions: table inspection, add/edit/delete form, agent sub-form
/// management, reset-to-defaults, and status assertion.
///
/// CSS selector notes:
/// - No data-testid attributes exist on this component; all selectors use CSS classes and text.
/// - The Enabled field uses a toggle-switch span pattern (same as QualityGateConfigSection):
///   <c>&lt;span class="toggle-switch"&gt;&lt;input type="checkbox" /&gt;</c>
///   Click <c>.toggle-slider</c> (the visible element) rather than the hidden input.
/// - ShowAddForm() pre-populates ONE empty agent card at index 0. Fill card 0 directly;
///   only click "+ Add Agent" when a second or subsequent card is needed.
/// - The "+ Add Agent" button.btn-add is INSIDE div.provider-form (differs from the outer
///   "Add Reviewer Configuration" button, which is replaced by the form when open).
/// - The "Reset collection to defaults" button (btn-revert) is only visible when the form is closed.
/// </summary>
public sealed class ReviewerConfigSectionHelper
{
    private readonly IPage _page;

    public ReviewerConfigSectionHelper(IPage page) => _page = page;

    // ── Form container ────────────────────────────────────────────────────

    private ILocator Form => _page.Locator("div.provider-form");

    // ── Form inputs ───────────────────────────────────────────────────────

    private ILocator DisplayNameInput =>
        _page.Locator("div.form-group:has(label:has-text('Display Name')) input[type='text']");

    private ILocator MatchLabelsInput =>
        _page.Locator("div.form-group:has(label:has-text('Match Labels')) input[type='text']");

    // The toggle input is visually hidden; click the visible slider instead.
    // The toggle-switch class is on the <span>, NOT the <label>.
    private ILocator EnabledToggleSlider =>
        _page.Locator("span.toggle-switch .toggle-slider");

    private ILocator EnabledToggleInput =>
        _page.Locator("span.toggle-switch input[type='checkbox']");

    private ILocator SaveButton => _page.Locator("div.form-buttons button.btn-save");

    private ILocator CancelButton => _page.Locator("div.form-buttons button.btn-cancel");

    // ── Delete confirm dialog ─────────────────────────────────────────────

    private ILocator DeleteConfirmDialog => _page.Locator("div.agent-detail-confirm");

    // ── Public methods ────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the outer "+ Add Reviewer Configuration" button.
    /// After the form opens, one empty agent card is already present at index 0.
    /// </summary>
    public async Task ClickAddAsync()
    {
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

    /// <summary>
    /// Sets the Enabled toggle state. Clicks the visible slider if the current state
    /// does not match the desired state.
    /// </summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        var isChecked = await EnabledToggleInput.IsCheckedAsync();
        if (isChecked != enabled)
            await EnabledToggleSlider.ClickAsync();
    }

    /// <summary>
    /// Fills the Name field of the Nth agent card (0-indexed).
    /// Card 0 is always pre-populated when the form opens via "+ Add Reviewer Configuration".
    /// </summary>
    public async Task FillAgentNameAsync(int agentIndex, string name)
    {
        var card = _page.Locator("div.reviewer-agent-card").Nth(agentIndex);
        var input = card.Locator("input[type='text']");
        await input.FillAsync(name);
        await input.BlurAsync();
    }

    /// <summary>Fills the Prompt textarea of the Nth agent card (0-indexed).</summary>
    public async Task FillAgentPromptAsync(int agentIndex, string prompt)
    {
        var card = _page.Locator("div.reviewer-agent-card").Nth(agentIndex);
        var textarea = card.Locator("textarea");
        await textarea.FillAsync(prompt);
        await textarea.BlurAsync();
    }

    /// <summary>
    /// Clicks the "+ Add Agent" button inside the reviewer form to append a new agent card.
    /// Must only be called when the form is open.
    /// </summary>
    public async Task ClickAddAgentAsync()
    {
        // When the form is open, button.btn-add inside div.provider-form is "+ Add Agent"
        await Form.Locator("button.btn-add").ClickAsync();
    }

    /// <summary>Clicks Save and waits for the form to close.</summary>
    public async Task SaveAsync()
    {
        await SaveButton.ClickAsync();
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
        var enabledIcon = row.Locator("span.status-icon.is-on");
        return await enabledIcon.CountAsync() > 0;
    }

    /// <summary>
    /// Returns the agent names summary text from the Agents cell of the named row.
    /// </summary>
    public async Task<string?> GetAgentNamesForRowAsync(string displayName)
    {
        var row = _page.Locator($"tr:has-text('{displayName}')").First;
        // Agents cell is the 3rd column (0-indexed: Name=0, MatchLabels=1, Agents=2)
        var agentsCell = row.Locator("td").Nth(2);
        return (await agentsCell.TextContentAsync())?.Trim();
    }

    /// <summary>
    /// Clicks the "Reset collection to defaults" button (btn-revert).
    /// Only available when the form is closed.
    /// </summary>
    public async Task ClickResetToDefaultsAsync()
    {
        await _page.ClickAsync("button.btn-revert");
        await DeleteConfirmDialog.WaitForAsync(new() { Timeout = 3_000 });
    }

    /// <summary>
    /// Confirms the reset-to-defaults dialog by clicking the "Reset" button.
    /// </summary>
    public async Task ConfirmResetAsync()
    {
        await DeleteConfirmDialog.Locator("button.btn-delete").ClickAsync();
        // TODO [WARNING]: Fixed 1s sleep; replace with DeleteConfirmDialog.WaitForAsync(Hidden) or
        // a table-row appears wait to avoid false-positive assertions on slow CI.
        await _page.WaitForTimeoutAsync(1_000);
    }

    /// <summary>
    /// Cancels the reset-to-defaults dialog by clicking "Cancel".
    /// </summary>
    public async Task CancelResetAsync()
    {
        await DeleteConfirmDialog.Locator("button.btn-cancel").ClickAsync();
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
