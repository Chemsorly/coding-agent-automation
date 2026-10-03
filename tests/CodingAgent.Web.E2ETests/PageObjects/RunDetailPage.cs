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
    /// Returns the text of all lines currently shown inside the live output panel.
    /// The panel renders a single <c>&lt;pre&gt;</c> element with lines joined by <c>\n</c>;
    /// this splits on newlines and trims to return individual non-empty lines.
    /// Returns an empty list when the "Waiting for output…" placeholder is shown or the panel
    /// is absent.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLiveOutputLinesAsync()
    {
        var pre = _page.Locator(".cockpit-card:has(h2:has-text('Live output')) pre.run-live-log");
        if (!await pre.IsVisibleAsync())
            return [];
        var text = await pre.TextContentAsync() ?? "";
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Returns true when the "Agent output (last N lines)" card is present — shown for finished
    /// runs when <c>run.OutputTail</c> is non-empty. Uses <c>data-testid="output-tail-card"</c>.
    /// </summary>
    public async Task<bool> HasOutputTailCardAsync()
        => await _page.Locator("[data-testid='output-tail-card']").IsVisibleAsync();

    /// <summary>
    /// Returns the text of all lines shown inside the saved output tail card.
    /// Splits the single <c>&lt;pre&gt;</c> on newlines just like <see cref="GetLiveOutputLinesAsync"/>.
    /// Returns an empty list when the card is absent or empty.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetOutputTailLinesAsync()
    {
        var pre = _page.Locator("[data-testid='output-tail-card'] pre.run-live-log");
        if (!await pre.IsVisibleAsync())
            return [];
        var text = await pre.TextContentAsync() ?? "";
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Waits for the live output panel to show at least <paramref name="minimumLineCount"/> lines,
    /// polling until the count is reached or the timeout expires.
    /// </summary>
    public async Task WaitForLiveOutputAsync(int minimumLineCount = 1, int timeoutMs = 15_000)
    {
        await _page.WaitForFunctionAsync(
            @"(args) => {
                const cards = Array.from(document.querySelectorAll('.cockpit-card'));
                for (const card of cards) {
                    const h2 = card.querySelector('h2');
                    if (h2 && h2.textContent && h2.textContent.includes('Live output')) {
                        const pre = card.querySelector('pre.run-live-log');
                        if (!pre) return false;
                        const lines = pre.textContent.split('\n').filter(l => l.length > 0);
                        return lines.length >= args.min;
                    }
                }
                return false;
            }",
            new { min = minimumLineCount },
            new() { Timeout = timeoutMs });
    }

    /// <summary>Clicks the sidebar's "Cancel Pipeline" button (present only while the run is active).</summary>
    public async Task CancelAsync()
    {
        await CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await CancelButton.ClickAsync();
    }

    /// <summary>
    /// Returns true if the Run page shows an "Issue #&lt;issueIdentifier&gt;" chip linking to the issue.
    /// Uses the <c>cockpit-link-chip</c> anchor rendered by RunPage.razor when <c>run.IssueUrl</c>
    /// is non-null (added for issue #3095 coverage).
    /// </summary>
    public async Task<bool> HasIssueLinkAsync(string issueIdentifier) =>
        await _page.Locator($"a.cockpit-link-chip:has-text('Issue #{issueIdentifier}')").IsVisibleAsync();
}
