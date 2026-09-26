using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /overview page — the cockpit landing page with the stat strip,
/// active-runs card, and recent-activity card.
/// </summary>
public sealed class OverviewPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public OverviewPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/overview");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await _page.WaitForTimeoutAsync(2000);
    }

    private ILocator StatStrip => _page.Locator(".cockpit-stat-strip");

    /// <summary>
    /// Reads the numeric value of the "Active" tile in the stat strip.
    /// The tile renders as <c>&lt;span class="cockpit-stat-l"&gt;Active&lt;/span&gt;&lt;span class="cockpit-stat-v"&gt;N&lt;/span&gt;</c>.
    /// </summary>
    public async Task<int> GetActiveCountAsync()
    {
        var tile = StatStrip.Locator(".cockpit-stat:has(.cockpit-stat-l:has-text('Active'))");
        var text = await tile.Locator(".cockpit-stat-v").InnerTextAsync();
        return int.Parse(text.Trim());
    }

    /// <summary>
    /// Reads the numeric value of the "Queue" tile in the stat strip.
    /// </summary>
    public async Task<int> GetQueueCountAsync()
    {
        var tile = StatStrip.Locator(".cockpit-stat:has(.cockpit-stat-l:has-text('Queue'))");
        var text = await tile.Locator(".cockpit-stat-v").InnerTextAsync();
        return int.Parse(text.Trim());
    }
}
