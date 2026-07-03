using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.PageObjects;

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
        var node = _page.Locator($"div.tree-node:has-text('{nodeText}')").First;
        if (!await node.IsVisibleAsync())
        {
            // Click all collapsed group headers to expand them
            var headers = _page.Locator("div.tree-group-header[aria-expanded='false']");
            var count = await headers.CountAsync();
            for (var i = 0; i < count; i++)
            {
                await headers.Nth(i).ClickAsync();
                await _page.WaitForTimeoutAsync(300);
            }
        }

        await node.ClickAsync();

        // Wait for the node to become active (confirms Blazor processed the click)
        await _page.WaitForSelectorAsync($"div.tree-node.active:has-text('{nodeText}')",
            new() { Timeout = 5_000 });

        // Allow Blazor Server to render the new content panel
        await _page.WaitForTimeoutAsync(1500);
    }

    /// <summary>Clicks the "+ Add Agent Provider" button (class btn-add).</summary>
    public async Task ClickAddProviderAsync()
    {
        await _page.ClickAsync("button.btn-add");

        // Wait for the form to appear
        await _page.WaitForSelectorAsync("div.provider-form", new() { Timeout = 5_000 });
    }

    /// <summary>Fills in the Display Name field in the provider form.</summary>
    public async Task FillDisplayNameAsync(string name)
    {
        // The Display Name input is inside a form-group with a label "Display Name"
        var formGroup = _page.Locator("div.form-group:has(label:has-text('Display Name'))");
        var input = formGroup.Locator("input[type='text']");
        await input.FillAsync(name);
    }

    /// <summary>Clicks the Save button inside the form-buttons container.</summary>
    public async Task ClickSaveAsync()
    {
        await _page.ClickAsync("div.form-buttons button.btn-save");

        // Wait for Blazor to process the save and re-render
        await _page.WaitForTimeoutAsync(1500);
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
}