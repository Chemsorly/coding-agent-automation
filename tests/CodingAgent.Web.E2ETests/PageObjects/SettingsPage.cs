using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /settings page.
/// Encapsulates navigation, tree node selection, and provider CRUD operations.
/// Uses CSS selectors and text content since the Settings page has no data-testid attributes.
/// </summary>
public sealed class SettingsPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public SettingsPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to /settings and waits for the page to be interactive.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/settings");

        // Wait for the page header to render
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        // Allow time for Blazor Server circuit to connect and event handlers to attach
        await _page.WaitForTimeoutAsync(3000);
    }

    /// <summary>Clicks a tree node by its visible text content (e.g., "Agent", "Issue", "General").</summary>
    public async Task SelectTreeNodeAsync(string nodeText)
    {
        // Tree nodes are inside collapsible groups. If the node isn't visible,
        // expand all collapsed groups first.
        var node = _page.Locator($".tree-node:has-text('{nodeText}')").First;
        if (!await node.IsVisibleAsync())
        {
            // Click all collapsed group headers to expand them
            var headers = _page.Locator(".tree-group-header[aria-expanded='false']");
            var count = await headers.CountAsync();
            for (var i = 0; i < count; i++)
            {
                await headers.Nth(i).ClickAsync();
                await _page.WaitForTimeoutAsync(300);
            }
        }

        await node.ClickAsync();

        // Wait for the node to become active (confirms navigation processed)
        await _page.WaitForSelectorAsync($".tree-node.active:has-text('{nodeText}')",
            new() { Timeout = 5_000 });

        // Allow Blazor Server to render the new content panel
        await _page.WaitForTimeoutAsync(1500);
    }

    // ── Per-kind Add shortcuts ──────────────────────────────────────────

    /// <summary>Navigates to the Agent section and clicks the "+ Add Agent Provider" button.</summary>
    public async Task ClickAddAgentProviderAsync()
    {
        await SelectTreeNodeAsync("Agent");
        await ClickAddProviderAsync();
    }

    /// <summary>Navigates to the Issue section and clicks the "+ Add Issue Provider" button.</summary>
    public async Task ClickAddIssueProviderAsync()
    {
        await SelectTreeNodeAsync("Issue");
        await ClickAddProviderAsync();
    }

    /// <summary>Navigates to the Repository section and clicks the "+ Add Repository Provider" button.</summary>
    public async Task ClickAddRepoProviderAsync()
    {
        await SelectTreeNodeAsync("Repository");
        await ClickAddProviderAsync();
    }

    /// <summary>Navigates to the Pipeline section and clicks the "+ Add Pipeline Provider" button.</summary>
    public async Task ClickAddPipelineProviderAsync()
    {
        await SelectTreeNodeAsync("Pipeline");
        await ClickAddProviderAsync();
    }

    /// <summary>Clicks the "+ Add * Provider" button (class btn-add) in the currently visible section.</summary>
    public async Task ClickAddProviderAsync()
    {
        await _page.ClickAsync("button.btn-add");

        // Wait for the form to appear (GitLabProviderForm and GitHubAppProviderForm both render
        // inside div.provider-form-row > div.provider-form)
        await _page.WaitForSelectorAsync("div.provider-form", new() { Timeout = 5_000 });
    }

    /// <summary>
    /// Clicks the Edit button on the provider card with the given display name and waits for the form to appear.
    /// Works for all provider kinds — each section renders btn-edit per card inside div.provider-card.
    /// </summary>
    public async Task ClickEditProviderAsync(string displayName)
    {
        var card = _page.Locator($"div.provider-card:has(strong:has-text('{displayName}'))");
        await card.Locator("button.btn-edit").ClickAsync();

        // All provider edit forms render inside div.provider-form (either directly for Agent
        // providers, or inside GitLabProviderForm/GitHubAppProviderForm child components)
        await _page.WaitForSelectorAsync("div.provider-form", new() { Timeout = 5_000 });

        // Allow Blazor to populate form fields
        await _page.WaitForTimeoutAsync(500);
    }

    /// <summary>Fills in the Display Name field in the provider form.</summary>
    public async Task FillDisplayNameAsync(string name)
    {
        // The Display Name input is inside a form-group with a label "Display Name"
        var formGroup = _page.Locator("div.form-group:has(label:has-text('Display Name'))");
        var input = formGroup.Locator("input[type='text']");
        await input.FillAsync(name);
    }

    /// <summary>
    /// Selects the provider type in the currently open form.
    /// The ProviderType select is the first select inside div.provider-form.
    /// </summary>
    public async Task SelectProviderTypeAsync(string providerType)
    {
        var form = _page.Locator("div.provider-form");
        var select = form.Locator("select").First;
        await select.SelectOptionAsync(new SelectOptionValue { Value = providerType });

        // Allow Blazor to re-render after the provider type change
        await _page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Fills the three required fields for a GitLab provider form:
    /// Display Name, Access Token, and Project ID.
    /// GitLabProviderForm uses @bind:event="oninput" for all inputs so FillAsync works directly.
    /// </summary>
    public async Task FillGitLabProviderFormAsync(string displayName, string accessToken, string projectId)
    {
        var form = _page.Locator("div.provider-form");

        // Display Name
        var displayNameGroup = form.Locator("div.form-group:has(label:has-text('Display Name'))");
        await displayNameGroup.Locator("input[type='text']").FillAsync(displayName);

        // Access Token (password input)
        var accessTokenGroup = form.Locator("div.form-group:has(label:has-text('Access Token'))");
        await accessTokenGroup.Locator("input").FillAsync(accessToken);

        // Project ID
        var projectIdGroup = form.Locator("div.form-group:has(label:has-text('Project ID'))");
        await projectIdGroup.Locator("input[type='text']").FillAsync(projectId);
    }

    /// <summary>Clicks the Save button inside the form-buttons container.</summary>
    public async Task ClickSaveAsync()
    {
        await _page.ClickAsync("div.form-buttons button.btn-save");

        // Wait for Blazor to process the save and re-render
        await _page.WaitForTimeoutAsync(1500);

        // After saving a provider, the Settings page may open a "Related Providers" or
        // "Configure Labels" modal overlay (via OnIssueProviderSaved / OnGitHubProviderSaved).
        // Dismiss it so subsequent interactions (Edit, Delete buttons) are not blocked.
        await DismissModalIfPresentAsync();
    }

    /// <summary>
    /// Dismisses any open modal overlay by clicking its Skip/Cancel button.
    /// No-op if no modal is currently visible.
    /// Handles both the "Related Providers" and "Configure Labels" modals that the Settings page
    /// can show automatically after a provider save.
    /// </summary>
    public async Task DismissModalIfPresentAsync()
    {
        var overlay = _page.Locator("div.modal-overlay");
        if (!await overlay.IsVisibleAsync())
            return;

        // Both modals render a btn-cancel ("Skip") button — click it to dismiss
        var cancelBtn = overlay.Locator("button.btn-cancel");
        if (await cancelBtn.IsVisibleAsync())
            await cancelBtn.ClickAsync();

        // Wait for the modal to disappear
        await overlay.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 3_000 });
    }

    /// <summary>Gets the display names from all visible provider cards.</summary>
    public async Task<IReadOnlyList<string>> GetProviderNamesAsync()
    {
        // Provider cards have a strong element containing the display name
        var cards = _page.Locator("div.provider-card strong");
        var count = await cards.CountAsync();
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var text = await cards.Nth(i).TextContentAsync();
            if (text is not null)
                names.Add(text.Trim());
        }
        return names;
    }

    /// <summary>Clicks the Delete button on the provider card with the given display name, then confirms.</summary>
    public async Task ClickDeleteProviderAsync(string displayName)
    {
        // Find the provider card containing the display name, then click its delete button
        var card = _page.Locator($"div.provider-card:has(strong:has-text('{displayName}'))");
        await card.Locator("button.btn-delete").ClickAsync();

        // Wait for the confirmation dialog to appear and click the confirm Delete button
        var confirmDialog = _page.Locator("div.agent-detail-confirm, div.delete-confirm");
        await confirmDialog.WaitForAsync(new() { Timeout = 3_000 });
        await confirmDialog.Locator("button.btn-delete").ClickAsync();

        // Wait for Blazor to process the deletion and re-render
        await _page.WaitForTimeoutAsync(1500);
    }

    /// <summary>Checks if a status message containing the specified text is visible.</summary>
    public async Task<bool> IsStatusMessageVisibleAsync(string containsText)
    {
        // Settings page uses InlineStatus component (class: inline-status) for save confirmations
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

    /// <summary>Expands any "Advanced settings" toggle sections on the current page.</summary>
    public async Task ExpandAdvancedSectionsAsync()
    {
        var toggles = _page.Locator("div.advanced-toggle");
        var count = await toggles.CountAsync();
        for (var i = 0; i < count; i++)
        {
            // Only click if not already expanded (check for "expanded" class on chevron)
            var chevron = toggles.Nth(i).Locator("span.toggle-chevron");
            var classes = await chevron.GetAttributeAsync("class") ?? "";
            if (!classes.Contains("expanded"))
            {
                await toggles.Nth(i).ClickAsync();
                await _page.WaitForTimeoutAsync(500);
            }
        }
    }

    // ── Initialize Provider helpers ─────────────────────────────────────

    /// <summary>
    /// Clicks the "Initialize Provider" button (class btn-initialize) on the card matching displayName.
    /// </summary>
    public async Task ClickInitializeProviderAsync(string displayName)
    {
        var card = _page.Locator($"div.provider-card:has(strong:has-text('{displayName}'))");
        await card.Locator("button.btn-initialize").ClickAsync();
    }

    /// <summary>
    /// Waits for the initialize status message to appear on the card matching displayName.
    /// Returns the status text. The caller should assert on the expected content.
    /// Throws if the status with the expected error/success class doesn't appear within the timeout.
    /// </summary>
    public async Task<string> WaitForInitializeStatusAsync(string displayName, bool isError)
    {
        var card = _page.Locator($"div.provider-card:has(strong:has-text('{displayName}'))");
        var expectedClass = isError ? "status-error" : "status-success";

        // Wait for the status element with the expected CSS class to appear inside the card
        var status = card.Locator($"div.settings-status.{expectedClass}");
        await status.WaitForAsync(new() { Timeout = 10_000 });

        return await status.TextContentAsync() ?? "";
    }

    // ── Setup Steps helpers ─────────────────────────────────────────────

    /// <summary>
    /// Adds a setup step to the currently open repo provider form.
    /// Expands the setup-steps Collapsible (starts collapsed), clicks "+ Add Step",
    /// fills the name and command inputs using blur to fire @onchange handlers.
    /// </summary>
    public async Task AddSetupStepAsync(string name, string command)
    {
        // TODO [WARNING]: The expand logic is gated on `if (await header.IsVisibleAsync())`. If
        // .setup-steps-section-header doesn't match — e.g. the collapsible only renders for certain
        // provider types or the class name drifts — the body is never expanded yet the code proceeds
        // to click .btn-add-inline, which will be hidden and time out with a generic message. The
        // silent skip of the expand branch masks the real cause. Consider asserting the header exists
        // (WaitForSelectorAsync) before attempting to expand and add.

        // The Collapsible header uses class setup-steps-section-header; expand it if collapsed.
        var header = _page.Locator(".setup-steps-section-header");
        if (await header.IsVisibleAsync())
        {
            // Check if already expanded: look for btn-add-inline inside the body
            var body = _page.Locator(".setup-steps-section-body");
            if (!await body.IsVisibleAsync())
                await header.ClickAsync();
            await _page.WaitForTimeoutAsync(300);
        }

        // Capture the current row count before clicking Add so we can wait for it to increase.
        var stepRowsBefore = _page.Locator(".setup-step-row");
        var previousCount = await stepRowsBefore.CountAsync();

        // Click "+ Add Step"
        var addBtn = _page.Locator(".setup-steps-section button.btn-add-inline");
        await addBtn.ClickAsync();

        // TODO [WARNING]: Previously a fixed WaitForTimeoutAsync(300) was used here, which is racy:
        // if the new row has not rendered when CountAsync() runs (slow Blazor Server circuit under CI
        // load), rowCount is 0 and Nth(-1) is passed to Playwright, producing an opaque locator
        // failure rather than a clear "row was not added" diagnostic. Use
        // Expect(rows).ToHaveCountAsync(previousCount + 1) instead once the Playwright Expect API is
        // available in this test harness; until then the fixed timeout is a pragmatic workaround.
        await _page.WaitForTimeoutAsync(300);

        // Fill the last step row (most recently added)
        var stepRows = _page.Locator(".setup-step-row");
        var rowCount = await stepRows.CountAsync();
        var lastRow = stepRows.Nth(rowCount - 1);

        // Name input uses @onchange — fill then Tab to trigger change
        var nameInput = lastRow.Locator("input.setup-step-name-input");
        await nameInput.FillAsync(name);
        await nameInput.PressAsync("Tab");

        // Command textarea uses @onchange — fill then Tab to trigger change
        var cmdInput = lastRow.Locator("textarea.setup-step-command-input");
        await cmdInput.FillAsync(command);
        await cmdInput.PressAsync("Tab");

        await _page.WaitForTimeoutAsync(200);
    }

    // ── Secrets helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Adds a secret to the currently open repo provider form.
    /// Expands the secrets Collapsible (starts collapsed), clicks "+ Add Secret",
    /// fills key and value inputs using blur to fire @onchange handlers.
    /// </summary>
    public async Task AddSecretAsync(string key, string value)
    {
        // TODO [WARNING]: Same silent-expand-skip risk as AddSetupStepAsync: if .secrets-section-header
        // doesn't match (class name drift, or the section only renders for certain provider types), the
        // expand branch is silently skipped and .btn-add-inline times out with a generic message. Consider
        // asserting the header exists before attempting to expand and add.

        // The Collapsible header uses class secrets-section-header; expand it if collapsed.
        var header = _page.Locator(".secrets-section-header");
        if (await header.IsVisibleAsync())
        {
            var body = _page.Locator(".secrets-section-body");
            if (!await body.IsVisibleAsync())
                await header.ClickAsync();
            await _page.WaitForTimeoutAsync(300);
        }

        // Capture the current row count before clicking Add so we can wait for it to increase.
        var secretRowsBefore = _page.Locator(".secret-row");
        var previousCount = await secretRowsBefore.CountAsync();

        // Click "+ Add Secret"
        var addBtn = _page.Locator(".secrets-section button.btn-add-inline");
        await addBtn.ClickAsync();

        // TODO [WARNING]: Same timing race as AddSetupStepAsync — fixed timeout before CountAsync can
        // yield rowCount==0 and Nth(-1) under CI load. Replace with Expect(rows).ToHaveCountAsync(previousCount + 1)
        // once Playwright Expect API is available in this harness.
        await _page.WaitForTimeoutAsync(300);

        // Fill the last secret row
        var secretRows = _page.Locator(".secret-row");
        var rowCount = await secretRows.CountAsync();
        var lastRow = secretRows.Nth(rowCount - 1);

        // Key input uses @onchange — fill then Tab
        var keyInput = lastRow.Locator("input.secret-key-input");
        await keyInput.FillAsync(key);
        await keyInput.PressAsync("Tab");

        // Value input uses @onchange — fill then Tab
        var valueInput = lastRow.Locator("input.secret-value-input");
        await valueInput.FillAsync(value);
        await valueInput.PressAsync("Tab");

        await _page.WaitForTimeoutAsync(200);
    }
}
