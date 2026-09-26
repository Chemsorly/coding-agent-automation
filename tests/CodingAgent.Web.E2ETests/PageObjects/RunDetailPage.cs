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
        // Do not use a fixed WaitForTimeoutAsync here — the Blazor Server circuit establishment
        // time varies from ~500ms to >3s depending on CI runner load, making any fixed delay
        // either too short (flaky) or wasteful on fast machines.
        // CancelAsync and RedispatchAsync use click-retry loops to handle the prerender race
        // where the cancel/redispatch buttons are visible in SSR but @onclick isn't wired yet.
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

        // Click-with-retry: the cancel button is rendered in SSR before the Blazor circuit
        // connects. The first click may be ignored if the circuit is not yet interactive.
        // We retry until the confirm section appears (up to the full 15s budget).
        var confirmSection = _page.Locator("[data-testid='cancel-pipeline-confirm-section']");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await CancelButton.ClickAsync();
            try
            {
                await confirmSection.WaitForAsync(new() { Timeout = 1_500 });
                break; // confirm section appeared — circuit is live and click registered
            }
            catch (TimeoutException)
            {
                // Circuit not yet interactive; retry the click
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
        var confirmSection = _page.Locator("[data-testid='redispatch-confirm-btn']");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await redispatchBtn.ClickAsync();
            try
            {
                await confirmSection.WaitForAsync(new() { Timeout = 1_500 });
                break; // confirm button appeared — click registered
            }
            catch (TimeoutException)
            {
                // Circuit not yet interactive; retry
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
