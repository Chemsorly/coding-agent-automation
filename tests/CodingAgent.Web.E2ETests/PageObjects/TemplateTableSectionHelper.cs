using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object helper for the label preview feature in the Pipeline Job Templates table.
/// The template table is rendered on /agent-coding, not under a Settings tree node.
///
/// Label preview button visibility:
/// The <c>btn-label-preview</c> button only renders when the resolved labels for the template's
/// repo provider are non-empty. Labels are resolved in this order:
///   1. <c>ProviderConfig.RequiredLabels</c> (if non-empty)
///   2. <c>PipelineConfiguration.DefaultRequiredAgentLabels</c> (if non-empty)
///   3. Empty → button is hidden
///
/// CSS selector notes:
/// - Only one label preview can be open at a time (<c>_expandedPreviewTemplateId</c> is a single
///   field in TemplateTableSection), so label preview content can be queried globally within
///   <c>div.label-preview-inline</c> rather than scoping to the template row.
/// - Do NOT use <c>tr:has-text('{templateName}')</c> to scope to the label-preview-row — that
///   row is a sibling <c>&lt;tr&gt;</c> of the template row, not a child, so Playwright's
///   :has-text() scoping does not apply across sibling relationships.
/// </summary>
public sealed class TemplateTableSectionHelper
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public TemplateTableSectionHelper(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    // ── Navigation ────────────────────────────────────────────────────────

    /// <summary>Navigates to /agent-coding and waits for the page to be interactive.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/agent-coding");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await _page.WaitForCockpitPageReadyAsync();
        // Wait for the template select to be interactive: confirms AgentCoding.OnInitializedAsync
        // (PageService.InitializeAsync) has completed and the template rows have rendered.
        await _page.WaitForInteractiveAsync("[data-testid='template-select']");
    }

    // ── Label preview helpers ─────────────────────────────────────────────

    /// <summary>
    /// Returns the locator for the label preview toggle button in the row for the given template name.
    /// </summary>
    private ILocator LabelPreviewButton(string templateName) =>
        _page.Locator($"tr:has-text('{templateName}') button.btn-label-preview").First;

    /// <summary>
    /// Returns true if the label preview toggle button is visible for the given template.
    /// The button is only rendered when the resolved repo labels are non-empty.
    /// </summary>
    public async Task<bool> IsLabelPreviewButtonVisibleAsync(string templateName)
    {
        try
        {
            await LabelPreviewButton(templateName).WaitForAsync(new() { Timeout = 3_000, State = WaitForSelectorState.Visible });
            return true;
        }
        catch (PlaywrightException e) when (e.Message.Contains("Timeout") || e.Message.Contains("timeout"))
        {
            // Only treat a genuine wait-timeout as "not found". Other PlaywrightException causes
            // (navigation failure, browser crash, selector engine error) should propagate so that
            // the test fails with a meaningful error rather than silently returning false.
            // TODO [WARNING]: A bare catch (PlaywrightException) was replaced with a timeout-only filter
            // to avoid masking real page errors. If this proves too narrow in practice, filter on
            // TimeoutException instead of the message string.
            return false;
        }
    }

    /// <summary>
    /// Toggles the label preview row for the given template (click to expand or collapse).
    /// </summary>
    public async Task ToggleLabelPreviewAsync(string templateName)
    {
        await LabelPreviewButton(templateName).ClickAsync();
        await _page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Returns true if the label preview is currently expanded (aria-expanded="true") for the given template.
    /// </summary>
    public async Task<bool> IsLabelPreviewExpandedAsync(string templateName)
    {
        var ariaExpanded = await LabelPreviewButton(templateName).GetAttributeAsync("aria-expanded");
        return ariaExpanded == "true";
    }

    /// <summary>
    /// Returns the display names of all Quality Gate Configurations listed in the currently-open
    /// label preview. Uses global scope since only one preview can be open at a time.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPreviewQualityGatesAsync()
    {
        // Global scope is correct: label-preview-row is a sibling <tr>, not a child of the
        // template row. Only one preview is open at a time, so global is unambiguous.
        var items = _page.Locator("div.label-preview-inline span.label-preview-qg");
        var count = await items.CountAsync();
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var text = await items.Nth(i).TextContentAsync();
            if (text is not null)
                names.Add(text.Trim());
        }
        return names;
    }

    /// <summary>
    /// Waits until the label preview button becomes visible for the given template.
    /// Use this after seeding data that should cause the button to appear.
    /// </summary>
    public async Task WaitForLabelPreviewButtonAsync(string templateName, int timeoutMs = 10_000)
    {
        await LabelPreviewButton(templateName).WaitForAsync(new() { Timeout = timeoutMs, State = WaitForSelectorState.Visible });
    }
}
