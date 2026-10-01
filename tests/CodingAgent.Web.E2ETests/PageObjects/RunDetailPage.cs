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

    /// <summary>The confirm-cancel button inside the sidebar confirmation prompt.</summary>
    public ILocator ConfirmCancelButton => _page.Locator("[data-testid='confirm-cancel-pipeline-btn']");

    /// <summary>The dismiss-cancel button inside the sidebar confirmation prompt.</summary>
    public ILocator DismissCancelButton => _page.Locator("[data-testid='dismiss-cancel-pipeline-btn']");

    /// <summary>The re-dispatch trigger button (shown for terminal implementation runs).</summary>
    public ILocator RedispatchButton => _page.Locator("[data-testid='redispatch-btn']");

    /// <summary>The confirm re-dispatch button inside the confirmation panel.</summary>
    public ILocator RedispatchConfirmButton => _page.Locator("[data-testid='redispatch-confirm-btn']");

    /// <summary>The re-dispatch card container (shown when CanRedispatch is true).</summary>
    public ILocator RedispatchCard => _page.Locator("[data-testid='redispatch-card']");

    /// <summary>Returns true when the "Cancel Pipeline" button is visible in the sidebar.</summary>
    public async Task<bool> IsCancelButtonVisibleAsync()
        => await CancelButton.IsVisibleAsync();

    /// <summary>Returns true when the "Re-dispatch" card is visible on the page.</summary>
    public async Task<bool> IsRedispatchCardVisibleAsync()
        => await RedispatchCard.IsVisibleAsync();

    /// <summary>
    /// Returns true when the "Live output" card is present on the page.
    /// This card is rendered only while the run is active (<c>_isLive == true</c>). It has no
    /// <c>data-testid</c>; the <c>data-testid="output-tail-card"</c> is the post-run tail card.
    /// </summary>
    public async Task<bool> HasLiveOutputPanelAsync()
        => await _page.Locator(".cockpit-card:has(h2:has-text('Live output'))").IsVisibleAsync();

    /// <summary>
    /// Clicks the sidebar's "Cancel Pipeline" button (present only while the run is active).
    /// This is the legacy non-confirm overload — just clicks the button without completing
    /// the confirm dialog.
    /// </summary>
    public async Task CancelAsync()
    {
        await CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await CancelButton.ClickAsync();
    }

    /// <summary>
    /// Cancel with optional confirmation.
    /// <para>
    /// When <paramref name="confirm"/> is <c>true</c>: opens the confirm prompt by clicking
    /// "Cancel Pipeline", then clicks "Yes, cancel" to confirm the cancellation.
    /// </para>
    /// <para>
    /// When <paramref name="confirm"/> is <c>false</c>: opens the confirm prompt by clicking
    /// "Cancel Pipeline", then clicks "No" to dismiss it without cancelling.
    /// </para>
    /// </summary>
    public async Task CancelAsync(bool confirm)
    {
        // Step 1: click "Cancel Pipeline" to open the inline confirmation prompt.
        await CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await CancelButton.ClickAsync();

        // Step 2: confirm or dismiss.
        if (confirm)
        {
            await ConfirmCancelButton.WaitForAsync(new() { Timeout = 10_000 });
            await ConfirmCancelButton.ClickAsync();
        }
        else
        {
            await DismissCancelButton.WaitForAsync(new() { Timeout = 10_000 });
            await DismissCancelButton.ClickAsync();
        }
    }

    /// <summary>
    /// Re-dispatch with optional confirmation.
    /// <para>
    /// When <paramref name="confirm"/> is <c>true</c>: opens the confirm panel by clicking
    /// "Re-dispatch", then clicks "Confirm re-dispatch" to submit.
    /// </para>
    /// <para>
    /// When <paramref name="confirm"/> is <c>false</c>: opens the confirm panel by clicking
    /// "Re-dispatch", then clicks "Cancel" to dismiss without dispatching.
    /// </para>
    /// </summary>
    public async Task RedispatchAsync(bool confirm)
    {
        // Step 1: click "Re-dispatch" to reveal the confirmation panel.
        await RedispatchButton.WaitForAsync(new() { Timeout = 15_000 });
        await RedispatchButton.ClickAsync();

        // Step 2: confirm or dismiss.
        if (confirm)
        {
            await RedispatchConfirmButton.WaitForAsync(new() { Timeout = 10_000 });
            await RedispatchConfirmButton.ClickAsync();
        }
        else
        {
            // The dismiss button inside the redispatch confirm panel uses the generic .btn-cancel class.
            var cancelBtn = _page.Locator("[data-testid='redispatch-card'] .btn-cancel");
            await cancelBtn.WaitForAsync(new() { Timeout = 10_000 });
            await cancelBtn.ClickAsync();
        }
    }

    /// <summary>
    /// Returns true if the Run page shows an "Issue #&lt;issueIdentifier&gt;" chip linking to the issue.
    /// Uses the <c>cockpit-link-chip</c> anchor rendered by RunPage.razor when <c>run.IssueUrl</c>
    /// is non-null (added for issue #3095 coverage).
    /// </summary>
    public async Task<bool> HasIssueLinkAsync(string issueIdentifier) =>
        await _page.Locator($"a.cockpit-link-chip:has-text('Issue #{issueIdentifier}')").IsVisibleAsync();

    /// <summary>Returns true when the page body contains the text "Re-dispatched successfully".</summary>
    public async Task<bool> HasRedispatchSuccessAsync()
        => (await GetPageTextAsync() ?? string.Empty).Contains("Re-dispatched successfully");

    /// <summary>Returns true when a re-dispatch error callout is visible on the page.</summary>
    public async Task<bool> HasRedispatchErrorAsync()
        => await _page.Locator(".summary-failure-callout:has-text('Re-dispatch failed')").IsVisibleAsync();

    /// <summary>Waits until the "Re-dispatched successfully" message appears on the page.</summary>
    public async Task WaitForRedispatchSuccessAsync(int timeoutMs = 15_000)
        => await _page.Locator(":text('Re-dispatched successfully')").WaitForAsync(new() { Timeout = timeoutMs });

    /// <summary>
    /// Waits until the Cancel Pipeline button is no longer visible (i.e., run is no longer active).
    /// </summary>
    public async Task WaitForCancelButtonGoneAsync(int timeoutMs = 20_000)
        => await CancelButton.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = timeoutMs });

    /// <summary>
    /// Returns the full page text, for checking status badges such as "Cancelled".
    /// </summary>
    public async Task<string> GetBodyTextAsync()
        => await _page.TextContentAsync("body") ?? string.Empty;
}
