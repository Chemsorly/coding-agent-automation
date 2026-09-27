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
        // Wait for h1 to appear (indicates the run data has loaded — h1 is only rendered
        // when _loading=false and _run!=null in RunPage.razor).
        // After blazor.web.js activates the interactive circuit, OnParametersSetAsync re-runs
        // briefly setting _loading=true (h1 disappears) then _loading=false (h1 reappears).
        // WaitForSelectorAsync retries automatically and resolves on the final stable appearance.
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 20_000 });
        // Wait for network idle: ensures OnParametersSetAsync HTTP calls (GetRunAsync,
        // GetPipelineConfigAsync) have completed and the page is in its final loaded state,
        // not the transient _loading=true re-render triggered by interactive circuit activation.
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 10_000 });
    }

    /// <summary>The whole-page text, for asserting the issue identifier / title is shown.</summary>
    public async Task<string?> GetPageTextAsync() => await _page.TextContentAsync("body");

    /// <summary>The live "Pipeline progress" card (PipelineSidebar host).</summary>
    public ILocator PipelineProgressCard => _page.Locator(".cockpit-card:has(h2:has-text('Pipeline progress'))");

    /// <summary>The "Cancel Pipeline" button in the sidebar footer (present only while the run is active).</summary>
    public ILocator CancelButton => _page.Locator("[data-testid='cancel-pipeline-btn']");

    /// <summary>The re-dispatch card (present only for terminal Implementation runs with provider IDs).</summary>
    public ILocator RedispatchCard => _page.Locator("[data-testid='redispatch-card']");

    /// <summary>
    /// Drives the full cancel confirm flow: opens the confirm dialog and then either confirms or
    /// dismisses it.
    /// <para>
    /// <paramref name="confirm"/> = <see langword="true"/>: clicks "Yes, cancel"
    /// (<c>data-testid="confirm-cancel-pipeline-btn"</c>).
    /// </para>
    /// <para>
    /// <paramref name="confirm"/> = <see langword="false"/>: clicks "No"
    /// (<c>data-testid="dismiss-cancel-pipeline-btn"</c>).
    /// </para>
    /// </summary>
    public async Task CancelAsync(bool confirm)
    {
        await CancelButton.WaitForAsync(new() { Timeout = 15_000 });

        // Click-with-retry: the cancel button may be rendered in SSR before the Blazor circuit
        // connects. The first click may be ignored if the circuit is not yet interactive.
        // We retry until the confirm section appears (up to the full 15s budget).
        // IMPORTANT: After a successful click the cancel button is REPLACED by the confirm
        // section in the DOM. We must check for the confirm section BEFORE clicking again,
        // otherwise ClickAsync blocks for its full timeout waiting for the button to reappear.
        var confirmSection = _page.Locator("[data-testid='cancel-pipeline-confirm-section']");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            // If the confirm section already appeared (from a previous click), stop retrying.
            if (await confirmSection.CountAsync() > 0)
                break;

            await CancelButton.ClickAsync();
            try
            {
                await confirmSection.WaitForAsync(new() { Timeout = 1_500 });
                break; // confirm section appeared — circuit is live and click registered
            }
            catch (TimeoutException)
            {
                // Circuit not yet interactive or click was dropped; retry
            }
        }

        // confirm section is now visible; click the appropriate button
        if (confirm)
        {
            var confirmBtn = _page.Locator("[data-testid='confirm-cancel-pipeline-btn']");
            await confirmBtn.WaitForAsync(new() { Timeout = 10_000 });
            await confirmBtn.ClickAsync();
        }
        else
        {
            var dismissBtn = _page.Locator("[data-testid='dismiss-cancel-pipeline-btn']");
            await dismissBtn.WaitForAsync(new() { Timeout = 10_000 });
            await dismissBtn.ClickAsync();
        }
    }

    /// <summary>
    /// Drives the full re-dispatch confirm flow: clicks "Re-dispatch" to open the confirm card
    /// and then either confirms or cancels.
    /// <para>
    /// <paramref name="confirm"/> = <see langword="true"/>: clicks "Confirm re-dispatch"
    /// (<c>data-testid="redispatch-confirm-btn"</c>).
    /// </para>
    /// <para>
    /// <paramref name="confirm"/> = <see langword="false"/>: clicks "Cancel" to dismiss the
    /// confirm section without dispatching.
    /// </para>
    /// </summary>
    public async Task RedispatchAsync(bool confirm)
    {
        var redispatchBtn = _page.Locator("[data-testid='redispatch-btn']");
        await redispatchBtn.WaitForAsync(new() { Timeout = 10_000 });

        // Click-with-retry: same Blazor SSR/circuit race as CancelAsync above.
        // We retry until the confirm sub-section appears inside the redispatch card.
        // IMPORTANT: After a successful click the re-dispatch button is hidden and the
        // confirm section appears. Check for the confirm button BEFORE clicking again
        // to avoid blocking in ClickAsync waiting for the button to reappear.
        var confirmSection = _page.Locator("[data-testid='redispatch-confirm-btn']");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            // If the confirm section already appeared (from a previous click), stop retrying.
            if (await confirmSection.CountAsync() > 0)
                break;

            await redispatchBtn.ClickAsync();
            try
            {
                await confirmSection.WaitForAsync(new() { Timeout = 1_500 });
                break; // confirm button appeared — click registered
            }
            catch (TimeoutException)
            {
                // Circuit not yet interactive or click was dropped; retry
            }
        }

        if (confirm)
        {
            var confirmBtn = _page.Locator("[data-testid='redispatch-confirm-btn']");
            await confirmBtn.WaitForAsync(new() { Timeout = 10_000 });
            await confirmBtn.ClickAsync();
        }
        else
        {
            // The cancel button inside the confirm section has no data-testid; locate it
            // relative to the confirm-btn sibling within the re-dispatch card.
            // TODO: [WARNING] This CSS class selector (.confirm-buttons .btn-cancel) is fragile —
            // a CSS rename silently breaks the dismiss path with a timeout rather than a clear
            // failure message. Add data-testid="redispatch-cancel-btn" to the Razor markup and
            // update this locator to use [data-testid='redispatch-cancel-btn'] instead.
            var cancelBtn = _page.Locator("[data-testid='redispatch-card'] .confirm-buttons .btn-cancel");
            await cancelBtn.WaitForAsync(new() { Timeout = 10_000 });
            await cancelBtn.ClickAsync();
        }
    }
}
