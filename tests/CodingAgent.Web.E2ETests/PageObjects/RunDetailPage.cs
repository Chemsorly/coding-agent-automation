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

    /// <summary>Returns true when the "Cancel Pipeline" button is visible in the sidebar.</summary>
    public async Task<bool> IsCancelButtonVisibleAsync()
        => await CancelButton.IsVisibleAsync();

    /// <summary>
    /// Returns true when the "Live output" card is present on the page.
    /// This card is rendered only while the run is active (<c>_isLive == true</c>). It has no
    /// <c>data-testid</c>; the <c>data-testid="output-tail-card"</c> is the post-run tail card.
    /// </summary>
    public async Task<bool> HasLiveOutputPanelAsync()
        => await _page.Locator(".cockpit-card:has(h2:has-text('Live output'))").IsVisibleAsync();

    /// <summary>
    /// Clicks the sidebar's "Cancel Pipeline" button (present only while the run is active),
    /// then optionally confirms or dismisses the confirmation prompt.
    ///
    /// <para>
    /// When <paramref name="confirm"/> is <c>true</c>, clicks "Yes, cancel"
    /// (<c>data-testid="confirm-cancel-pipeline-btn"</c>), which posts the Cancelled status.
    /// When <paramref name="confirm"/> is <c>false</c>, clicks "No"
    /// (<c>data-testid="dismiss-cancel-pipeline-btn"</c>), leaving the run unchanged.
    /// </para>
    /// </summary>
    public async Task CancelAsync(bool confirm = true)
    {
        await CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await CancelButton.ClickAsync();

        // Wait for the confirmation section to appear in PipelineSidebar
        await _page.WaitForSelectorAsync("[data-testid='cancel-pipeline-confirm-section']",
            new() { Timeout = 10_000 });

        if (confirm)
            await _page.ClickAsync("[data-testid='confirm-cancel-pipeline-btn']");
        else
            await _page.ClickAsync("[data-testid='dismiss-cancel-pipeline-btn']");
    }

    /// <summary>Returns true when the cancel confirmation section is visible (after clicking Cancel Pipeline).</summary>
    public async Task<bool> IsCancelConfirmSectionVisibleAsync()
        => await _page.Locator("[data-testid='cancel-pipeline-confirm-section']").IsVisibleAsync();

    /// <summary>Returns true when the run badge shows "Cancelled".</summary>
    public async Task<bool> IsShownAsCancelledAsync()
        => await _page.Locator(".step-badge:has-text('Cancelled')").IsVisibleAsync();

    /// <summary>Waits until the run badge shows "Cancelled" or times out.</summary>
    public async Task WaitForCancelledStateAsync(int timeoutMs = 20_000)
    {
        await _page.WaitForSelectorAsync(".step-badge:has-text('Cancelled')",
            new() { Timeout = timeoutMs });
    }

    /// <summary>Returns the cancel-error callout text if visible, or null.</summary>
    public async Task<string?> GetCancelErrorTextAsync()
    {
        var callout = _page.Locator("[data-testid='cancel-error-callout']");
        if (!await callout.IsVisibleAsync()) return null;
        return await callout.TextContentAsync();
    }

    // ── Re-dispatch ───────────────────────────────────────────────────────

    /// <summary>The re-dispatch card — present for terminal Implementation runs with provider IDs.</summary>
    public ILocator RedispatchCard => _page.Locator("[data-testid='redispatch-card']");

    /// <summary>Returns true when the re-dispatch card is visible on the page.</summary>
    public async Task<bool> IsRedispatchCardVisibleAsync()
        => await RedispatchCard.IsVisibleAsync();

    /// <summary>Returns true when the "Re-dispatch" button is visible on the page.</summary>
    public async Task<bool> IsRedispatchButtonVisibleAsync()
        => await _page.Locator("[data-testid='redispatch-btn']").IsVisibleAsync();

    /// <summary>Returns true when the redispatch confirm button is visible (after clicking Re-dispatch).</summary>
    public async Task<bool> IsRedispatchConfirmVisibleAsync()
        => await _page.Locator("[data-testid='redispatch-confirm-btn']").IsVisibleAsync();

    /// <summary>Returns true when the "Re-dispatched successfully." message is visible on the page.</summary>
    public async Task<bool> IsRedispatchSuccessVisibleAsync()
        => await _page.Locator("text=Re-dispatched successfully").IsVisibleAsync();

    /// <summary>Waits until the "Re-dispatched successfully." message is visible.</summary>
    public async Task WaitForRedispatchSuccessAsync(int timeoutMs = 15_000)
    {
        await _page.WaitForSelectorAsync("text=Re-dispatched successfully",
            new() { Timeout = timeoutMs });
    }

    /// <summary>Returns the re-dispatch error callout text if visible, or null.</summary>
    public async Task<string?> GetRedispatchErrorTextAsync()
    {
        var callout = _page.Locator("[role='alert']:has-text('Re-dispatch failed')");
        if (!await callout.IsVisibleAsync()) return null;
        return await callout.TextContentAsync();
    }

    /// <summary>
    /// Clicks the "Re-dispatch" button to open the confirmation section, then optionally confirms
    /// or cancels the re-dispatch.
    ///
    /// <para>
    /// When <paramref name="confirm"/> is <c>true</c>, clicks "Confirm re-dispatch"
    /// (<c>data-testid="redispatch-confirm-btn"</c>), which dispatches a new WorkItem.
    /// When <paramref name="confirm"/> is <c>false</c>, clicks the "Cancel" button adjacent to
    /// the confirm button, collapsing the confirm section without dispatching.
    /// </para>
    /// </summary>
    public async Task RedispatchAsync(bool confirm = true)
    {
        var redispatchBtn = _page.Locator("[data-testid='redispatch-btn']");
        await redispatchBtn.WaitForAsync(new() { Timeout = 15_000 });
        await redispatchBtn.ClickAsync();

        // Wait for the confirm section to render
        await _page.WaitForSelectorAsync("[data-testid='redispatch-confirm-btn']",
            new() { Timeout = 10_000 });

        if (confirm)
        {
            await _page.ClickAsync("[data-testid='redispatch-confirm-btn']");
        }
        else
        {
            // TODO [WARNING]: `.confirm-buttons` is not scoped to a unique data-testid. If both the
            // cancel-pipeline confirm section and the redispatch confirm section render simultaneously,
            // this locator can match multiple elements and Playwright strict mode will throw. Scope
            // the container to a unique data-testid (e.g. `[data-testid='redispatch-confirm-section']`)
            // and use `Exact = true` on the button name to avoid matching "Cancel Pipeline" or similar.
            // Click the "Cancel" button in the confirm-buttons section
            // The confirm section contains both "Confirm re-dispatch" and "Cancel"
            var confirmBtnContainer = _page.Locator(".confirm-buttons");
            await confirmBtnContainer.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        }
    }

    /// <summary>Waits until the re-dispatch card is visible or times out.</summary>
    public async Task WaitForRedispatchCardVisibleAsync(int timeoutMs = 15_000)
    {
        await _page.WaitForSelectorAsync("[data-testid='redispatch-card']",
            new() { Timeout = timeoutMs });
    }

    /// <summary>Waits until the cancel button is absent (run completed/cancelled) or times out.</summary>
    public async Task WaitForCancelButtonAbsentAsync(int timeoutMs = 20_000)
    {
        // TODO [WARNING]: Replace this manual polling loop with Playwright's built-in locator wait:
        //   await CancelButton.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = timeoutMs });
        // The current busy-wait is equivalent but can spuriously throw if the last WaitForTimeoutAsync
        // call pushes DateTime.UtcNow past the deadline immediately after the element disappears.
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!await IsCancelButtonVisibleAsync()) return;
            await _page.WaitForTimeoutAsync(200);
        }
        throw new TimeoutException($"Cancel button still visible after {timeoutMs}ms");
    }

    /// <summary>
    /// Returns true if the run is shown as active (the Cancel Pipeline button is visible).
    /// </summary>
    public async Task<bool> IsRunActiveAsync()
        => await IsCancelButtonVisibleAsync();

    /// <summary>
    /// Returns true if the run is shown as a Review run.
    /// </summary>
    public async Task<bool> IsReviewRunAsync()
        => await _page.Locator(".run-type-review").IsVisibleAsync();

    /// <summary>
    /// Returns true if the run is shown as a Decomposition run.
    /// </summary>
    public async Task<bool> IsDecompRunAsync()
        => await _page.Locator(".run-type-decomp").IsVisibleAsync();

    /// <summary>
    /// Returns true if the run is shown as an Implementation run.
    /// </summary>
    public async Task<bool> IsImplRunAsync()
        => await _page.Locator(".run-type-impl").IsVisibleAsync();

    /// <summary>
    /// Returns true if the Run page shows an "Issue #&lt;issueIdentifier&gt;" chip linking to the issue.
    /// Uses the <c>cockpit-link-chip</c> anchor rendered by RunPage.razor when <c>run.IssueUrl</c>
    /// is non-null (added for issue #3095 coverage).
    /// </summary>
    public async Task<bool> HasIssueLinkAsync(string issueIdentifier) =>
        await _page.Locator($"a.cockpit-link-chip:has-text('Issue #{issueIdentifier}')").IsVisibleAsync();
}
