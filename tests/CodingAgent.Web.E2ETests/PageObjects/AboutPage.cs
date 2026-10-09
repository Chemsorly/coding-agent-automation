using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /about page.
/// Encapsulates DOM queries for build info, version info, and pipeline stats sections.
///
/// Key DOM structure:
/// - Sections: div.about-section, each with div.cockpit-card-header h2 for heading
/// - Each section body: div.about-section-body
/// - Info grids: div.about-info-grid with alternating span.about-label and span.about-value
/// - Pipeline stats section renders "No pipeline runs yet." (p.about-muted) when _totalRuns == 0
/// </summary>
public sealed class AboutPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    private const int DefaultTimeout = 15_000;

    public AboutPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to /about and waits for the Blazor circuit and page header to render.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoCockpitPageAsync($"{_baseUrl}/about");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = DefaultTimeout });
    }

    /// <summary>
    /// Returns the text value from the info grid within the given section, for the given label,
    /// or null if the value does not appear within <see cref="DefaultTimeout"/>.
    /// Uses adjacent sibling selector: the value span immediately follows the label span.
    /// </summary>
    /// <param name="sectionHeading">Text of the h2 heading (e.g., "Version Info", "Build Info", "Pipeline Stats").</param>
    /// <param name="label">Label text (e.g., "App Version", "Commit", "Total Runs").</param>
    public async Task<string?> GetInfoGridValueAsync(string sectionHeading, string label)
    {
        // Scope to the section containing the specified h2, then find the value cell after the label
        // TODO [WARNING]: sectionHeading and label are interpolated directly into the CSS selector
        // string. If either ever contains a single-quote or selector metacharacter (e.g. from
        // dynamic data), the selector would be malformed or match unintended elements. Prefer
        // Playwright's GetByText/filter API over raw string interpolation to make this safe for
        // future callers with dynamic input.
        var section = _page.Locator($".about-section:has(h2:has-text('{sectionHeading}'))");
        var valueLocator = section
            .Locator(".about-info-grid")
            .Locator($".about-label:has-text('{label}') + .about-value");
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
    /// Returns true if the Pipeline Stats section shows the data grid (i.e., at least one run exists).
    /// Returns false when it shows "No pipeline runs yet."
    /// </summary>
    public async Task<bool> IsPipelineStatsVisibleAsync()
    {
        var section = _page.Locator(".about-section:has(h2:has-text('Pipeline Stats'))");
        // The grid is present when _totalRuns > 0
        return await section.Locator(".about-info-grid").CountAsync() > 0;
    }
}
