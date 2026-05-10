using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.PageObjects;

/// <summary>
/// Page object for the /consolidation page.
/// Encapsulates navigation and interactions with consolidation template cards,
/// harness suggestions section, run history table, and trigger buttons.
/// </summary>
public sealed class ConsolidationPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public ConsolidationPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to the /consolidation page and waits for it to render.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/consolidation");

        // Wait for the page header to render
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        // Allow time for the Blazor Server circuit to connect and data to load
        await _page.WaitForTimeoutAsync(3000);
    }

    /// <summary>Gets the page title text.</summary>
    public async Task<string?> GetPageTitleAsync()
    {
        return await _page.TextContentAsync("h1");
    }

    /// <summary>Gets the number of template cards displayed on the page.</summary>
    public async Task<int> GetTemplateCardCountAsync()
    {
        var cards = await _page.QuerySelectorAllAsync(".consolidation-card");
        return cards.Count;
    }

    /// <summary>Checks if the "No enabled templates" empty state is shown.</summary>
    public async Task<bool> IsNoTemplatesMessageVisibleAsync()
    {
        var element = await _page.QuerySelectorAsync(".monitoring-empty:has-text('No enabled templates')");
        return element is not null;
    }

    /// <summary>Gets the template card title for a specific card by index.</summary>
    public async Task<string?> GetTemplateCardTitleAsync(int index)
    {
        var titles = await _page.QuerySelectorAllAsync(".consolidation-card-title");
        if (index >= titles.Count) return null;
        return await titles[index].TextContentAsync();
    }

    /// <summary>Checks if the Brain Consolidation button is visible for a template card.</summary>
    public async Task<bool> IsBrainButtonVisibleAsync(string templateName)
    {
        var button = await _page.QuerySelectorAsync(
            $".consolidation-card:has(.consolidation-card-title:has-text('{templateName}')) button:has-text('Brain Consolidation')");
        return button is not null;
    }

    /// <summary>Checks if the Refactoring Scan button is visible for a template card.</summary>
    public async Task<bool> IsRefactoringButtonVisibleAsync(string templateName)
    {
        var button = await _page.QuerySelectorAsync(
            $".consolidation-card:has(.consolidation-card-title:has-text('{templateName}')) button:has-text('Refactoring Scan')");
        return button is not null;
    }

    /// <summary>Clicks the Brain Consolidation trigger button for a template.</summary>
    public async Task ClickBrainConsolidationAsync(string templateName)
    {
        await _page.ClickAsync(
            $".consolidation-card:has(.consolidation-card-title:has-text('{templateName}')) button:has-text('Brain Consolidation')");
    }

    /// <summary>Clicks the Refactoring Scan trigger button for a template.</summary>
    public async Task ClickRefactoringScanAsync(string templateName)
    {
        await _page.ClickAsync(
            $".consolidation-card:has(.consolidation-card-title:has-text('{templateName}')) button:has-text('Refactoring Scan')");
    }

    /// <summary>Clicks the Generate Suggestions button.</summary>
    public async Task ClickGenerateSuggestionsAsync()
    {
        await _page.ClickAsync("button:has-text('Generate Suggestions')");
    }

    /// <summary>Checks if the "No suggestions generated yet" empty state is shown.</summary>
    public async Task<bool> IsNoSuggestionsMessageVisibleAsync()
    {
        var element = await _page.QuerySelectorAsync(".monitoring-empty:has-text('No suggestions generated yet')");
        return element is not null;
    }

    /// <summary>Gets the harness suggestions metadata text (generated date, run count, success rate).</summary>
    public async Task<string?> GetSuggestionsMetaAsync()
    {
        var element = await _page.QuerySelectorAsync(".consolidation-suggestions-meta");
        return element is not null ? await element.TextContentAsync() : null;
    }

    /// <summary>Gets the number of suggestion items displayed.</summary>
    public async Task<int> GetSuggestionItemCountAsync()
    {
        var items = await _page.QuerySelectorAllAsync(".consolidation-suggestion-item");
        return items.Count;
    }

    /// <summary>Gets the text of a specific suggestion item by index.</summary>
    public async Task<string?> GetSuggestionTextAsync(int index)
    {
        var items = await _page.QuerySelectorAllAsync(".consolidation-suggestion-text");
        if (index >= items.Count) return null;
        return await items[index].TextContentAsync();
    }

    /// <summary>Gets the number of rows in the run history table.</summary>
    public async Task<int> GetRunHistoryRowCountAsync()
    {
        var rows = await _page.QuerySelectorAllAsync(".monitoring-table tbody tr");
        return rows.Count;
    }

    /// <summary>Checks if the "No consolidation runs yet" empty state is shown.</summary>
    public async Task<bool> IsNoRunsMessageVisibleAsync()
    {
        var element = await _page.QuerySelectorAsync(".monitoring-empty:has-text('No consolidation runs yet')");
        return element is not null;
    }

    /// <summary>Gets the status message text (success or error feedback after trigger).</summary>
    public async Task<string?> GetStatusMessageAsync()
    {
        var element = await _page.QuerySelectorAsync(".consolidation-status-message");
        return element is not null ? await element.TextContentAsync() : null;
    }

    /// <summary>Checks if the status message is an error.</summary>
    public async Task<bool> IsStatusMessageErrorAsync()
    {
        var element = await _page.QuerySelectorAsync(".consolidation-status-error");
        return element is not null;
    }

    /// <summary>Waits for the run history table to have at least the specified number of rows.</summary>
    public async Task WaitForRunHistoryCountAsync(int minCount, int timeoutMs = 10_000)
    {
        await _page.WaitForFunctionAsync(
            $"() => document.querySelectorAll('.monitoring-table tbody tr').length >= {minCount}",
            null,
            new() { Timeout = timeoutMs });
    }

    /// <summary>Waits for a status message to appear on the page.</summary>
    public async Task WaitForStatusMessageAsync(int timeoutMs = 10_000)
    {
        await _page.WaitForSelectorAsync(".consolidation-status-message", new() { Timeout = timeoutMs });
    }

    /// <summary>Gets the text content of a specific run history row cell.</summary>
    public async Task<string?> GetRunHistoryRowTextAsync(int rowIndex)
    {
        var rows = await _page.QuerySelectorAllAsync(".monitoring-table tbody tr");
        if (rowIndex >= rows.Count) return null;
        return await rows[rowIndex].TextContentAsync();
    }

    /// <summary>Checks if the Brain Consolidation button is disabled for a template.</summary>
    public async Task<bool> IsBrainButtonDisabledAsync(string templateName)
    {
        var button = await _page.QuerySelectorAsync(
            $".consolidation-card:has(.consolidation-card-title:has-text('{templateName}')) button:has-text('Brain Consolidation')");
        if (button is null) return false;
        return await button.IsDisabledAsync();
    }

    /// <summary>Checks if the Generate Suggestions button is disabled.</summary>
    public async Task<bool> IsGenerateSuggestionsDisabledAsync()
    {
        var button = await _page.QuerySelectorAsync("button:has-text('Generate Suggestions')");
        if (button is null) return false;
        return await button.IsDisabledAsync();
    }
}
