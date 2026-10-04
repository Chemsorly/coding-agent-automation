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
        // Wait for the Blazor interactive circuit to connect. The static prerender renders all
        // elements immediately, but Blazor Server onclick handlers are only active once the
        // circuit is established. RunPage sets data-interactive="true" in OnAfterRenderAsync
        // (which only fires after circuit connect), so this is the reliable "ready to click" marker.
        await _page.WaitForSelectorAsync(
            "[data-testid='run-page'][data-interactive='true']",
            new() { Timeout = 15_000 });
    }

    /// <summary>The whole-page text, for asserting the issue identifier / title is shown.</summary>
    public async Task<string?> GetPageTextAsync() => await _page.TextContentAsync("body");

    /// <summary>The live "Pipeline progress" card (PipelineSidebar host).</summary>
    public ILocator PipelineProgressCard => _page.Locator(".cockpit-card:has(h2:has-text('Pipeline progress'))");

    public ILocator CancelButton => _page.Locator("[data-testid='cancel-pipeline-btn']");

    /// <summary>The "Yes, cancel" confirm button inside the cancel confirmation prompt.</summary>
    public ILocator ConfirmCancelButton => _page.Locator("[data-testid='confirm-cancel-pipeline-btn']");

    /// <summary>The "No" dismiss button inside the cancel confirmation prompt.</summary>
    public ILocator DismissCancelButton => _page.Locator("[data-testid='dismiss-cancel-pipeline-btn']");

    /// <summary>The "Re-dispatch" button (visible on terminal Implementation runs).</summary>
    public ILocator RedispatchButton => _page.Locator("[data-testid='redispatch-btn']");

    /// <summary>The "Confirm re-dispatch" button inside the re-dispatch confirmation prompt.</summary>
    public ILocator ConfirmRedispatchButton => _page.Locator("[data-testid='redispatch-confirm-btn']");

    /// <summary>Returns true when the "Cancel Pipeline" button is visible in the sidebar.</summary>
    public async Task<bool> IsCancelButtonVisibleAsync()
        => await CancelButton.IsVisibleAsync();

    /// <summary>Returns true when the re-dispatch card is present on the page.</summary>
    public async Task<bool> IsRedispatchCardVisibleAsync()
        => await _page.Locator("[data-testid='redispatch-card']").IsVisibleAsync();

    /// <summary>Returns true when the "Re-dispatch" button is visible (before confirming).</summary>
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
    /// Clicks the sidebar's "Cancel Pipeline" button (the first step of the two-step confirm flow).
    /// Does NOT click the confirm button; call <see cref="CancelAsync(bool)"/> to drive the full flow.
    /// </summary>
    public async Task CancelAsync()
    {
        await CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await CancelButton.ClickAsync();
    }

    /// <summary>
    /// Drives the two-step cancel confirmation flow.
    ///
    /// <list type="bullet">
    ///   <item>Clicks "Cancel Pipeline" to open the confirmation prompt.</item>
    ///   <item>When <paramref name="confirm"/> is <c>true</c>, clicks "Yes, cancel" to proceed.</item>
    ///   <item>When <paramref name="confirm"/> is <c>false</c>, clicks "No" to dismiss without cancelling.</item>
    /// </list>
    /// </summary>
    /// <param name="confirm">Whether to confirm (<c>true</c>) or dismiss (<c>false</c>) the cancel prompt.</param>
    public async Task CancelAsync(bool confirm)
    {
        await CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await CancelButton.ClickAsync();

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
    /// Drives the two-step re-dispatch confirmation flow.
    ///
    /// <list type="bullet">
    ///   <item>Clicks "Re-dispatch" to open the confirmation prompt.</item>
    ///   <item>When <paramref name="confirm"/> is <c>true</c>, clicks "Confirm re-dispatch".</item>
    ///   <item>When <paramref name="confirm"/> is <c>false</c>, clicks the cancel button to dismiss.</item>
    /// </list>
    /// </summary>
    /// <param name="confirm">Whether to confirm (<c>true</c>) or dismiss (<c>false</c>) the re-dispatch prompt.</param>
    public async Task RedispatchAsync(bool confirm)
    {
        await RedispatchButton.WaitForAsync(new() { Timeout = 15_000 });
        await RedispatchButton.ClickAsync();

        if (confirm)
        {
            await ConfirmRedispatchButton.WaitForAsync(new() { Timeout = 10_000 });
            await ConfirmRedispatchButton.ClickAsync();
        }
        else
        {
            // TODO [WARNING]: This uses a CSS class selector (.agent-detail-confirm .btn-cancel) rather
            // than a stable data-testid attribute (e.g. data-testid="redispatch-dismiss-btn").
            // If the CSS class is renamed, this selector will break silently (Playwright timeout
            // instead of a clear mismatch error). Add data-testid="redispatch-dismiss-btn" to the
            // dismiss button in RunPage.razor and update this locator to use it for symmetry with all
            // other confirm/dismiss controls in this page object.
            // Note: the confirm=false path is not exercised by any current test, so this selector
            // is entirely untested in the E2E suite.
            var dismissBtn = _page.Locator(".agent-detail-confirm .btn-cancel");
            await dismissBtn.WaitForAsync(new() { Timeout = 10_000 });
            await dismissBtn.ClickAsync();
        }
    }

    /// <summary>
    /// Returns true when the output tail card (<c>data-testid="output-tail-card"</c>) is visible.
    /// This card is rendered for completed/terminal runs that produced output; it is absent while
    /// the run is active (<c>_isLive == true</c>) and absent for terminal runs with no output.
    /// </summary>
    public async Task<bool> HasOutputTailCardAsync()
        => await _page.Locator("[data-testid='output-tail-card']").IsVisibleAsync();

    /// <summary>
    /// Returns the lines displayed inside the output tail card (<c>data-testid="output-tail-card"</c>).
    /// Splits the pre-formatted text on newlines and trims blank entries.
    /// Returns an empty collection when the card is not present or contains no text.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetOutputTailLinesAsync()
    {
        var card = _page.Locator("[data-testid='output-tail-card']");
        var pre = card.Locator("pre");
        var text = await pre.TextContentAsync() ?? string.Empty;
        return text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Returns true if the Run page shows an "Issue #&lt;issueIdentifier&gt;" chip linking to the issue.
    /// Uses the <c>cockpit-link-chip</c> anchor rendered by RunPage.razor when <c>run.IssueUrl</c>
    /// is non-null (added for issue #3095 coverage).
    /// </summary>
    public async Task<bool> HasIssueLinkAsync(string issueIdentifier) =>
        await _page.Locator($"a.cockpit-link-chip:has-text('Issue #{issueIdentifier}')").IsVisibleAsync();

    /// <summary>Waits until the run page shows the run as cancelled (no Cancel button visible).</summary>
    public async Task WaitForCancelledStateAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = (int)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds;
        // TODO [WARNING]: Both WaitForFunctionAsync and WaitForSelectorAsync below receive the same
        // effectiveTimeout independently. They are sequential, so the effective maximum wait is up to
        // 2 × effectiveTimeout (e.g. 30 s when called with the default 15 s timeout). This is unlikely
        // to cause test failures but may mask slow cancellation by tolerating twice the intended deadline.
        // Fix: track elapsed time after the first wait and pass the remaining budget to the second wait.
        // Wait for the Cancel button to disappear (run is no longer live)
        await _page.WaitForFunctionAsync(
            "() => !document.querySelector('[data-testid=\"cancel-pipeline-btn\"]')",
            null,
            new() { Timeout = effectiveTimeout });
        // Also wait for a "Cancelled" badge to appear
        await _page.WaitForSelectorAsync(".step-badge:has-text('Cancelled')", new() { Timeout = effectiveTimeout });
    }

    /// <summary>
    /// Waits until "Re-dispatched successfully" is shown on the page.
    /// </summary>
    /// TODO [WARNING]: This method uses `:has-text('Re-dispatched successfully')` with no element
    /// type qualifier, so it matches every ancestor DOM node that contains that text (including
    /// `<body>`, `<div>`, etc.). This is functionally equivalent for a success assertion but would
    /// also resolve against a broader match if the text appeared in an unrelated context. Consider
    /// scoping to the specific container, e.g. `[data-testid='redispatch-card'] :has-text(...)`,
    /// once the element structure is stable.
    public async Task WaitForRedispatchSuccessAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = (int)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds;
        await _page.WaitForSelectorAsync(
            ":has-text('Re-dispatched successfully')",
            new() { Timeout = effectiveTimeout });
    }

    /// <summary>
    /// Waits until the re-dispatch error callout is shown on the page and returns its text.
    /// The error is rendered inside <c>div.summary-failure-callout[role=alert]</c> within the
    /// re-dispatch card (<c>data-testid="redispatch-card"</c>) as:
    /// <c>Re-dispatch failed: &lt;exception message&gt;</c>.
    /// </summary>
    /// <param name="timeout">Maximum time to wait before throwing.</param>
    /// <returns>The full text content of the error callout.</returns>
    public async Task<string> WaitForRedispatchErrorAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = (int)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds;
        var errorLocator = _page.Locator("[data-testid='redispatch-card'] .summary-failure-callout[role='alert']");
        await errorLocator.WaitForAsync(new() { Timeout = effectiveTimeout });
        return await errorLocator.TextContentAsync() ?? string.Empty;
    }
}
