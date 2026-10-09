using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /knowledge page.
/// Encapsulates DOM queries for the stat strip tiles and the brain comparison section.
///
/// Key DOM structure:
/// - Loading state: div.cockpit-card > div.cockpit-empty containing "Loading…"
/// - Empty state: div.cockpit-card > div.cockpit-empty (no runs)
/// - Stat strip: div.cockpit-stat-strip, each tile is div.cockpit-stat with
///   span.cockpit-stat-l (label) and span.cockpit-stat-v (value)
/// - Brain comparison section: div.cockpit-card:has(h2:has-text("Does the brain help?"))
///   Present only when both _brainRuns > 0 and _nonBrainRuns > 0.
/// </summary>
public sealed class KnowledgePage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    private const int DefaultTimeout = 15_000;

    public KnowledgePage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>
    /// Navigates to /knowledge, waits for the interactive circuit's first render, and waits for
    /// the loading state to clear. After this method returns, either the stat strip or a
    /// non-loading empty state is present.
    /// </summary>
    public async Task NavigateAsync()
    {
        await _page.GotoCockpitPageAsync($"{_baseUrl}/knowledge");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = DefaultTimeout });
        await WaitForLoadCompleteAsync();
    }

    /// <summary>
    /// Waits until the page shows a settled state: no "Loading…" card, and either the stat strip
    /// or an empty/error card is present.
    /// </summary>
    public Task WaitForLoadCompleteAsync()
        => _page.WaitForFunctionAsync(
            "() => { " +
            "  const empties = [...document.querySelectorAll('.cockpit-page .cockpit-card .cockpit-empty')]; " +
            "  if (empties.some(e => e.textContent.trim() === 'Loading\u2026')) return false; " +
            "  return !!document.querySelector('.cockpit-page .cockpit-stat') || empties.length > 0; " +
            "}",
            null,
            new() { Timeout = DefaultTimeout });

    /// <summary>
    /// Returns the value text of the stat tile matching the given label, or null if the tile does
    /// not appear within <see cref="DefaultTimeout"/>.
    /// </summary>
    public async Task<string?> GetStatTileValueAsync(string label)
    {
        // TODO [WARNING]: label is interpolated directly into the CSS selector string.
        // If label ever contains a single-quote or selector metacharacter (e.g. from dynamic data),
        // the selector would be malformed or match unintended elements. Prefer Playwright's
        // GetByText/filter API over raw string interpolation to make this safe for future callers.
        var valueLocator = _page.Locator(
            $".cockpit-stat:has(.cockpit-stat-l:has-text('{label}')) .cockpit-stat-v");
        try
        {
            await valueLocator.WaitForAsync(new() { Timeout = DefaultTimeout });
        }
        catch (TimeoutException)
        {
            return null;
        }
        return (await valueLocator.TextContentAsync())?.Trim();
    }

    /// <summary>
    /// Returns true if the "Does the brain help?" comparison card is visible.
    /// Only shown when there are runs both with and without the brain.
    /// </summary>
    public async Task<bool> IsBrainComparisonVisibleAsync()
        => await _page.Locator(".cockpit-card:has(h2:has-text('Does the brain help?'))").CountAsync() > 0;

    /// <summary>
    /// Returns the text of the delta span inside the brain comparison section
    /// (e.g. "+20 pts" or "-5 pts"), or null if the comparison is not visible.
    /// </summary>
    public async Task<string?> GetBrainComparisonDeltaTextAsync()
    {
        var card = _page.Locator(".cockpit-card:has(h2:has-text('Does the brain help?'))");
        if (await card.CountAsync() == 0)
            return null;

        // The delta is in a span with font-weight:700 inside the card's pad section
        // We identify it by looking for text that ends with "pts"
        // TODO [WARNING]: Iterating all spans and returning the first whose text ends with "pts" is
        // a fragile heuristic. Any span in this card whose copy ends in "pts" (e.g. a tooltip or
        // future label) would shadow the intended delta span. Prefer scoping to the specific
        // structural element (e.g. a CSS class or data-* attribute on the delta span) rather than
        // a text-suffix scan.
        var spans = card.Locator("span");
        var count = await spans.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var text = (await spans.Nth(i).TextContentAsync())?.Trim() ?? "";
            if (text.EndsWith("pts"))
                return text;
        }
        return null;
    }
}
