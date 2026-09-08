using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.PageObjects;

/// <summary>
/// Page object for the /work page — the cockpit replacement for the old monitoring page's
/// active-runs + job-queue tables. "In flight" and "Queue" are separate <c>.cockpit-card</c>s,
/// each with an <c>&lt;h2&gt;</c> header; rows render the issue as <c>#{identifier}</c>.
/// </summary>
public sealed class WorkPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public WorkPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/work");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await _page.WaitForTimeoutAsync(2000);
    }

    private ILocator InFlightCard => _page.Locator(".cockpit-card:has(h2:has-text('In flight'))");
    private ILocator QueueCard => _page.Locator(".cockpit-card:has(h2:has-text('Queue'))");

    /// <summary>Row for the given issue within the "In flight" card.</summary>
    public ILocator InFlightRow(string issueIdentifier) =>
        InFlightCard.Locator("tbody tr").Filter(new() { HasTextString = $"#{issueIdentifier}" });

    /// <summary>Row for the given issue within the "Queue" card.</summary>
    public ILocator QueueRow(string issueIdentifier) =>
        QueueCard.Locator("tbody tr").Filter(new() { HasTextString = $"#{issueIdentifier}" });

    public async Task<bool> IsIssueInFlightAsync(string issueIdentifier)
        => await InFlightRow(issueIdentifier).CountAsync() > 0;

    public async Task<bool> IsIssueQueuedAsync(string issueIdentifier)
        => await QueueRow(issueIdentifier).CountAsync() > 0;

    /// <summary>Waits until the issue appears in the "In flight" card (10s auto-refresh cadence).</summary>
    public async Task WaitForInFlightAsync(string issueIdentifier, int timeoutMs = 15_000)
        => await InFlightRow(issueIdentifier).First.WaitForAsync(new() { Timeout = timeoutMs });

    /// <summary>Clicks the Cancel button on the in-flight row for the given issue.</summary>
    public async Task CancelInFlightAsync(string issueIdentifier)
        => await InFlightRow(issueIdentifier).GetByRole(AriaRole.Button).ClickAsync();
}
