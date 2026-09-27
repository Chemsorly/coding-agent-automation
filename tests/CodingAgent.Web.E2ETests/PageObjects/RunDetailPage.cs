using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /runs/{runId} page (RunPage) — the cockpit replacement for the old
/// monitoring run-detail modal. It is a full page, not a modal: the run detail, live "Pipeline
/// progress" (PipelineSidebar), live output, and feedback all render inline. The sidebar renders a
/// "Cancel Pipeline" button (<c>data-testid="cancel-pipeline-btn"</c>) while the run is active.
/// </summary>
public sealed class RunDetailPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public RunDetailPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates directly to a run's detail page and waits for the header to render.</summary>
    public async Task NavigateAsync(string runId)
    {
        await _page.GotoAsync($"{_baseUrl}/runs/{runId}");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await _page.WaitForTimeoutAsync(1500);
    }

    /// <summary>The whole-page text, for asserting the issue identifier / title is shown.</summary>
    public async Task<string?> GetPageTextAsync() => await _page.TextContentAsync("body");

    /// <summary>The live "Pipeline progress" card (PipelineSidebar host).</summary>
    public ILocator PipelineProgressCard => _page.Locator(".cockpit-card:has(h2:has-text('Pipeline progress'))");

    public ILocator CancelButton => _page.Locator("[data-testid='cancel-pipeline-btn']");

    /// <summary>The inline cancel confirm section rendered by the sidebar after the initial Cancel click.</summary>
    public ILocator CancelConfirmSection => _page.Locator("[data-testid='cancel-pipeline-confirm-section']");

    /// <summary>The "Yes, cancel" confirmation button rendered inside the confirm section.</summary>
    public ILocator ConfirmCancelButton => _page.Locator("[data-testid='confirm-cancel-pipeline-btn']");

    /// <summary>The "No" dismiss button rendered inside the cancel confirm section.</summary>
    public ILocator DismissCancelButton => _page.Locator("[data-testid='dismiss-cancel-pipeline-btn']");

    /// <summary>The re-dispatch card, visible only for terminal Implementation runs with provider IDs.</summary>
    public ILocator RedispatchCard => _page.Locator("[data-testid='redispatch-card']");

    /// <summary>The initial "Re-dispatch" trigger button (before confirm).</summary>
    public ILocator RedispatchButton => _page.Locator("[data-testid='redispatch-btn']");

    /// <summary>The "Confirm re-dispatch" button inside the confirmation box.</summary>
    public ILocator RedispatchConfirmButton => _page.Locator("[data-testid='redispatch-confirm-btn']");

    /// <summary>Returns true when the "Cancel Pipeline" button is visible in the sidebar.</summary>
    public async Task<bool> IsCancelButtonVisibleAsync()
        => await CancelButton.IsVisibleAsync();

    /// <summary>Returns true when the re-dispatch card is present and visible on the page.</summary>
    public async Task<bool> IsRedispatchCardVisibleAsync()
        => await RedispatchCard.IsVisibleAsync();

    /// <summary>Returns true when the initial "Re-dispatch" button is visible (i.e. confirm box not yet shown).</summary>
    public async Task<bool> IsRedispatchButtonVisibleAsync()
        => await RedispatchButton.IsVisibleAsync();

    /// <summary>
    /// Returns true when the "Live output" card is present on the page.
    /// This card is rendered only while the run is active (<c>_isLive == true</c>). It has no
    /// <c>data-testid</c>; the <c>data-testid="output-tail-card"</c> is the post-run tail card.
    /// </summary>
    public async Task<bool> HasLiveOutputPanelAsync()
        => await _page.Locator(".cockpit-card:has(h2:has-text('Live output'))").IsVisibleAsync();

    /// <summary>
    /// Clicks the sidebar's "Cancel Pipeline" button.
    ///
    /// When <paramref name="confirm"/> is <c>true</c> (default), the method also clicks the
    /// "Yes, cancel" confirm button that appears afterward, completing the full cancellation flow.
    /// When <paramref name="confirm"/> is <c>false</c>, it clicks "No" to dismiss the confirm
    /// prompt without cancelling.
    /// </summary>
    public async Task CancelAsync(bool confirm = true)
    {
        await CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        // Wait for the Blazor circuit to register @onclick on the cancel button before clicking.
        // Without this, ClickAsync() fires before the server-side handler is active and
        // _showCancelConfirm never becomes true on a slow CI runner.
        await _page.WaitForInteractiveAsync("[data-testid='cancel-pipeline-btn']");
        await CancelButton.ClickAsync();

        // Wait for the confirm section to appear (the sidebar shows it after the initial click)
        await CancelConfirmSection.WaitForAsync(new() { Timeout = 10_000 });

        if (confirm)
            await ConfirmCancelButton.ClickAsync();
        else
            await DismissCancelButton.ClickAsync();
    }

    /// <summary>
    /// Clicks the "Re-dispatch" button and optionally confirms.
    ///
    /// When <paramref name="confirm"/> is <c>true</c>, the method clicks the initial trigger
    /// button, waits for the confirm box to appear, then clicks "Confirm re-dispatch".
    /// When <paramref name="confirm"/> is <c>false</c>, it clicks the trigger button, waits for
    /// the confirm box, then clicks the "Cancel" dismiss button — leaving the run page unchanged.
    /// </summary>
    public async Task RedispatchAsync(bool confirm = true)
    {
        await RedispatchButton.WaitForAsync(new() { Timeout = 10_000 });
        // Wait for the Blazor circuit to register @onclick on the redispatch button before clicking.
        // Without this, ClickAsync() fires before the server-side handler is active and
        // _showRedispatchConfirm never becomes true on a slow CI runner.
        await _page.WaitForInteractiveAsync("[data-testid='redispatch-btn']");
        await RedispatchButton.ClickAsync();

        // Wait for the confirm box to appear
        await RedispatchConfirmButton.WaitForAsync(new() { Timeout = 10_000 });

        if (confirm)
        {
            await RedispatchConfirmButton.ClickAsync();
        }
        else
        {
            // TODO [WARNING]: This locator uses a CSS class selector (.agent-detail-confirm .btn-cancel)
            // rather than a data-testid attribute. All other confirm/dismiss buttons in this file use
            // data-testid locators, making them robust to CSS refactoring. If the .agent-detail-confirm
            // wrapper or .btn-cancel class is renamed or restructured in the Razor component, this locator
            // will silently find nothing and ClickAsync() will throw a timeout — but only when
            // confirm: false is passed. Align with the data-testid convention used elsewhere.
            // Click the Cancel button inside the confirm box
            var cancelInsideConfirm = _page.Locator(".agent-detail-confirm .btn-cancel");
            await cancelInsideConfirm.ClickAsync();
        }
    }

    /// <summary>
    /// Returns true if the Run page shows an "Issue #&lt;issueIdentifier&gt;" chip linking to the issue.
    /// Uses the <c>cockpit-link-chip</c> anchor rendered by RunPage.razor when <c>run.IssueUrl</c>
    /// is non-null (added for issue #3095 coverage).
    /// </summary>
    public async Task<bool> HasIssueLinkAsync(string issueIdentifier) =>
        await _page.Locator($"a.cockpit-link-chip:has-text('Issue #{issueIdentifier}')").IsVisibleAsync();
}
