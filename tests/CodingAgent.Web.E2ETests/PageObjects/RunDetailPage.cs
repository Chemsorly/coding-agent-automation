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

    /// <summary>Confirmation section that appears after clicking "Cancel Pipeline".</summary>
    public ILocator CancelConfirmSection => _page.Locator("[data-testid='cancel-pipeline-confirm-section']");

    /// <summary>The "Yes, cancel" confirmation button inside the confirm section.</summary>
    public ILocator ConfirmCancelButton => _page.Locator("[data-testid='confirm-cancel-pipeline-btn']");

    /// <summary>The "No" dismiss button inside the confirm section.</summary>
    public ILocator DismissCancelButton => _page.Locator("[data-testid='dismiss-cancel-pipeline-btn']");

    /// <summary>The Re-dispatch button (visible on terminal Implementation runs with provider IDs).</summary>
    public ILocator RedispatchButton => _page.Locator("[data-testid='redispatch-btn']");

    /// <summary>The "Confirm re-dispatch" button inside the re-dispatch confirm section.</summary>
    public ILocator RedispatchConfirmButton => _page.Locator("[data-testid='redispatch-confirm-btn']");

    /// <summary>The re-dispatch card (visible on terminal Implementation runs with provider IDs).</summary>
    public ILocator RedispatchCard => _page.Locator("[data-testid='redispatch-card']");

    /// <summary>Returns true when the "Cancel Pipeline" button is visible in the sidebar.</summary>
    public async Task<bool> IsCancelButtonVisibleAsync()
        => await CancelButton.IsVisibleAsync();

    /// <summary>Returns true when the "Re-dispatch" button is visible on the page.</summary>
    public async Task<bool> IsRedispatchButtonVisibleAsync()
        => await RedispatchButton.IsVisibleAsync();

    /// <summary>Returns true when the re-dispatch card is present (visible or hidden) on the page.</summary>
    public async Task<bool> IsRedispatchCardPresentAsync()
        => await RedispatchCard.CountAsync() > 0;

    /// <summary>
    /// Returns true when the "Live output" card is present on the page.
    /// This card is rendered only while the run is active (<c>_isLive == true</c>). It has no
    /// <c>data-testid</c>; the <c>data-testid="output-tail-card"</c> is the post-run tail card.
    /// </summary>
    public async Task<bool> HasLiveOutputPanelAsync()
        => await _page.Locator(".cockpit-card:has(h2:has-text('Live output'))").IsVisibleAsync();

    /// <summary>
    /// Clicks the sidebar's "Cancel Pipeline" button (present only while the run is active),
    /// optionally confirming or dismissing the confirmation prompt.
    /// </summary>
    /// <param name="confirm">
    /// <c>true</c> to click "Yes, cancel" after the confirm section appears;
    /// <c>false</c> to click "No" (dismiss without cancelling);
    /// <c>null</c> (default, legacy) to click the initial button without interacting with confirm.
    /// </param>
    public async Task CancelAsync(bool? confirm = null)
    {
        await CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await CancelButton.ClickAsync();

        if (confirm is null)
            return;

        // Wait for the confirmation section to appear
        await CancelConfirmSection.WaitForAsync(new() { Timeout = 10_000 });

        if (confirm.Value)
            await ConfirmCancelButton.ClickAsync();
        else
            await DismissCancelButton.ClickAsync();
    }

    /// <summary>
    /// Clicks the "Re-dispatch" button and optionally confirms or cancels.
    /// </summary>
    /// <param name="confirm">
    /// <c>true</c> to click "Confirm re-dispatch";
    /// <c>false</c> to click "Cancel" on the confirmation dialog.
    /// </param>
    public async Task RedispatchAsync(bool confirm)
    {
        await RedispatchButton.WaitForAsync(new() { Timeout = 15_000 });
        await RedispatchButton.ClickAsync();

        // Wait for the confirm section to appear (the confirm button is now visible)
        await RedispatchConfirmButton.WaitForAsync(new() { Timeout = 10_000 });

        if (confirm)
            await RedispatchConfirmButton.ClickAsync();
        else
            // TODO [WARNING]: This uses a CSS class selector (.agent-detail-confirm .btn-cancel)
            // rather than a data-testid attribute like every other locator in this file. It is
            // fragile under UI refactors and no test currently exercises confirm=false for
            // re-dispatch. Replace with a [data-testid='redispatch-cancel-btn'] (or equivalent)
            // once the markup is known, and add a test for the cancel path.
            await _page.Locator(".agent-detail-confirm .btn-cancel").ClickAsync();
    }

    /// <summary>
    /// Polls until the page shows "Cancelled" in its body text, indicating the run has reached
    /// the Cancelled terminal state and the UI has refreshed.
    /// </summary>
    // TODO [WARNING]: Polling _page.TextContentAsync("body") is broad — any element containing
    // the word "Cancelled" (e.g., a sidebar badge from a different run) can cause a false positive.
    // Scope the locator to the run-status area, e.g. [data-testid='run-status'], to eliminate
    // this risk. Also consider using Playwright's built-in Expect(...).ToBeVisibleAsync() which
    // gives cleaner timeout messages. Additionally, CancellationToken is not accepted here; if the
    // test runner cancels externally the loop keeps polling until the internal deadline.
    public async Task WaitForCancelledAsync(int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var text = await _page.TextContentAsync("body");
            if (text?.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) == true)
                return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Page did not show 'Cancelled' within {timeoutMs}ms");
    }

    /// <summary>
    /// Polls until the page shows "Re-dispatched successfully" in its body text.
    /// </summary>
    // TODO [WARNING]: Same broad body-text polling concern as WaitForCancelledAsync — scope to a
    // specific data-testid element and accept a CancellationToken to support cooperative
    // cancellation from the test runner.
    public async Task WaitForRedispatchSuccessAsync(int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var text = await _page.TextContentAsync("body");
            if (text?.Contains("Re-dispatched successfully", StringComparison.OrdinalIgnoreCase) == true)
                return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Page did not show 'Re-dispatched successfully' within {timeoutMs}ms");
    }

    /// <summary>
    /// Polls until the page shows "Re-dispatch failed" in its body text.
    /// </summary>
    // TODO [WARNING]: Same broad body-text polling concern as WaitForCancelledAsync — scope to a
    // specific data-testid element and accept a CancellationToken to support cooperative
    // cancellation from the test runner.
    public async Task WaitForRedispatchErrorAsync(int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var text = await _page.TextContentAsync("body");
            if (text?.Contains("Re-dispatch failed", StringComparison.OrdinalIgnoreCase) == true)
                return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Page did not show 'Re-dispatch failed' within {timeoutMs}ms");
    }

    /// <summary>
    /// Returns true if the Run page shows an "Issue #&lt;issueIdentifier&gt;" chip linking to the issue.
    /// Uses the <c>cockpit-link-chip</c> anchor rendered by RunPage.razor when <c>run.IssueUrl</c>
    /// is non-null (added for issue #3095 coverage).
    /// </summary>
    public async Task<bool> HasIssueLinkAsync(string issueIdentifier) =>
        await _page.Locator($"a.cockpit-link-chip:has-text('Issue #{issueIdentifier}')").IsVisibleAsync();
}
