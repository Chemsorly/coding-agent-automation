using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object helper for the Quality Gate Configurations section of the Settings page.
/// Encapsulates CRUD interactions: table inspection, add/edit/delete form, and status assertion.
///
/// CSS selector notes:
/// - No data-testid attributes exist on this component; all selectors use CSS classes and text.
/// - The Enabled field uses a toggle-switch span pattern:
///   <c>&lt;span class="toggle-switch"&gt;&lt;input type="checkbox" /&gt;</c>
///   The toggle input is visually hidden (opacity:0); click <c>.toggle-slider</c> to toggle it.
/// - After FillAsync on @bind inputs, BlurAsync() is required to trigger Blazor's onchange handler.
/// - Empty MatchLabels rows show a <c>badge-global</c> badge in the table.
/// </summary>
public sealed class QualityGateConfigSectionHelper
{
    private readonly IPage _page;

    public QualityGateConfigSectionHelper(IPage page) => _page = page;

    // ── Form container ────────────────────────────────────────────────────

    private ILocator Form => _page.Locator("div.provider-form");

    // ── Form inputs ───────────────────────────────────────────────────────

    private ILocator DisplayNameInput =>
        _page.Locator("div.form-group:has(label:has-text('Display Name')) input[type='text']");

    private ILocator MatchLabelsInput =>
        _page.Locator("div.form-group:has(label:has-text('Match Labels')) input[type='text']");

    private ILocator CompilationCommandInput =>
        _page.Locator("div.form-group:has(label:has-text('Compilation Command')) input[type='text']");

    private ILocator TestCommandInput =>
        _page.Locator("div.form-group:has(label:has-text('Test Command')) input[type='text']");

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

    /// <summary>Clicks the outer "+ Add Quality Gate Configuration" button.</summary>
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

    /// <summary>Fills the Compilation Command input and blurs.</summary>
    public async Task FillCompilationCommandAsync(string command)
    {
        await CompilationCommandInput.FillAsync(command);
        await CompilationCommandInput.BlurAsync();
    }

    /// <summary>Fills the Test Command input and blurs.</summary>
    public async Task FillTestCommandAsync(string command)
    {
        await TestCommandInput.FillAsync(command);
        await TestCommandInput.BlurAsync();
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
    /// Returns the text content of the Commands cell for the named row.
    /// Useful for asserting that a command change was persisted to the table.
    /// </summary>
    public async Task<string?> GetCommandsForRowAsync(string displayName)
    {
        var row = _page.Locator($"tr:has-text('{displayName}')").First;
        // Commands cell is the 3rd column (0-indexed: Name=0, MatchLabels=1, Commands=2)
        // TODO [WARNING]: The Commands cell renders FormatCommands, which truncates to 80 chars with an ellipsis.
        // For long commands, compare against the cell's title attribute (FormatCommandsFull) rather than TextContent.
        var commandsCell = row.Locator("td").Nth(2);
        return (await commandsCell.TextContentAsync())?.Trim();
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
