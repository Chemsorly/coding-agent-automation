using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

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

    /// <summary>
    /// Waits until the issue appears in the "Queue" card. Required before asserting queue
    /// presence — <see cref="NavigateAsync"/> uses a 2-second fixed sleep which is not a
    /// deterministic wait for a newly-enqueued item.
    /// </summary>
    public async Task WaitForQueuedAsync(string issueIdentifier, int timeoutMs = 15_000)
        => await QueueRow(issueIdentifier).First.WaitForAsync(new() { Timeout = timeoutMs });

    /// <summary>
    /// Removes a queued item by clicking its "Remove" button. Unlike in-flight cancellation
    /// (which uses a two-step confirmation), the Queue Remove button fires immediately with no
    /// confirmation dialog.
    /// </summary>
    public async Task RemoveQueuedAsync(string issueIdentifier)
    {
        var row = QueueRow(issueIdentifier);
        await row.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
    }

    /// <summary>
    /// Sets the priority weight for a queued item. Fills the <c>input.priority-input</c>
    /// within the queue row and blurs it to trigger Blazor's <c>@onchange</c> handler.
    /// <c>FillAsync</c> alone does not fire <c>@onchange</c> — blur is required.
    /// </summary>
    public async Task SetPriorityAsync(string issueIdentifier, int weight)
    {
        var input = QueueRow(issueIdentifier).Locator("input.priority-input");
        await input.FillAsync(weight.ToString());
        await input.BlurAsync();
    }

    /// <summary>
    /// Cancels the in-flight run for the given issue. Clicks the initial "Cancel" button to open
    /// the confirmation dialog, then clicks "Yes" to confirm — matching the two-step confirmation
    /// UI introduced to prevent accidental single-click cancellations.
    /// </summary>
    public async Task CancelInFlightAsync(string issueIdentifier)
    {
        var row = InFlightRow(issueIdentifier);
        // Step 1: click the initial "Cancel" button to open the confirmation prompt.
        await row.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        // Step 2: click "Yes" to confirm the cancellation.
        await row.GetByRole(AriaRole.Button, new() { Name = "Yes" }).ClickAsync();
    }
}
