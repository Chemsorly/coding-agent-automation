using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /runs page — the paginated run history with outcome tabs
/// (All / Completed / Failed / Cancelled) and the badge-labelled result column.
/// </summary>
public sealed class RunsPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public RunsPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/runs");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await _page.WaitForTimeoutAsync(2000);
    }

    /// <summary>
    /// Clicks the segmented outcome tab whose label matches <paramref name="tabName"/>
    /// (e.g. "All", "Completed", "Failed", "Cancelled") and waits briefly for the page to reload.
    /// </summary>
    public async Task SelectTabAsync(string tabName)
    {
        await _page.Locator($".cockpit-segmented button:has-text('{tabName}')").ClickAsync();
        await _page.WaitForTimeoutAsync(2000);
    }

    /// <summary>
    /// Row locator for the given issue identifier. Uses the #identifier pattern rendered by Runs.razor.
    /// </summary>
    private ILocator RunRow(string issueIdentifier) =>
        _page.Locator("tbody tr").Filter(new() { HasTextString = $"#{issueIdentifier}" });

    /// <summary>
    /// Returns the text of the <c>.step-badge</c> element in the row for the given issue identifier.
    /// Asserts the row is visible before reading.
    /// </summary>
    public async Task<string> GetBadgeLabelForRunAsync(string issueIdentifier)
    {
        var row = RunRow(issueIdentifier).First;
        await row.WaitForAsync(new() { Timeout = 10_000 });
        var text = await row.Locator(".step-badge").InnerTextAsync();
        return text.Trim();
    }

    /// <summary>
    /// Returns true when the run row for <paramref name="issueIdentifier"/> is visible on
    /// the current tab. Does not wait — evaluates the DOM at call time.
    /// </summary>
    // TODO [WARNING]: CountAsync() counts DOM nodes regardless of CSS visibility — a hidden row
    // (e.g. display:none) would still return true. If the Runs page hides rows without removing
    // them from the DOM, this method would give false positives. Consider using
    // Locator.IsVisibleAsync() or combining Filter with IsVisibleAsync for a stricter check.
    public async Task<bool> IsRunVisibleAsync(string issueIdentifier)
        => await RunRow(issueIdentifier).CountAsync() > 0;
}
