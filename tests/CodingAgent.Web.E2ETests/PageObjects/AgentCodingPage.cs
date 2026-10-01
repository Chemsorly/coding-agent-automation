using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline.Models;
using Microsoft.Playwright;
using Xunit;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /agent-coding page.
/// Encapsulates the manual dispatch flow: select template → browse issues → select issue → start pipeline.
/// Uses the official ASP.NET Core Blazor E2E testing patterns:
/// - WaitForBlazorAsync: confirms the Blazor JS framework is loaded (SignalR circuit established)
/// - WaitForInteractiveAsync: confirms event handlers are attached to DOM elements
/// See: https://github.com/dotnet/aspnetcore/blob/main/src/Components/Testing/src/Infrastructure/PlaywrightExtensions.cs
/// </summary>
public sealed class AgentCodingPage(IPage page, string baseUrl)
{
    private readonly IPage _page = page;
    private readonly string _baseUrl = baseUrl;

    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/agent-coding");

        // Wait for the page to render (prerendered HTML appears immediately)
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        // Allow time for the Blazor Server circuit to connect via SignalR
        // and for event handlers to be attached to DOM elements.
        await _page.WaitForTimeoutAsync(3000);
    }

    /// <summary>Selects a template from the Manual Dispatch dropdown by its display text.</summary>
    public async Task SelectTemplateAsync(string templateName)
    {
        var select = _page.Locator("[data-testid='template-select']");

        // Wait for the template option to appear in the DOM.
        // Generous timeout for slow ARM CI runners where Blazor circuit establishment is slow.
        await _page.WaitForFunctionAsync(
            @"(name) => {
                const select = document.querySelector('[data-testid=""template-select""]');
                if (!select) return false;
                return Array.from(select.options).some(o => o.text === name);
            }",
            templateName,
            new() { Timeout = 20_000 });

        // Use Playwright's native selectOption which triggers the change event.
        // The component uses explicit @onchange handler (not @bind) for reliable event capture.
        await select.SelectOptionAsync(new SelectOptionValue { Label = templateName });

        // Wait for Blazor Server to process the change event via SignalR round-trip
        await _page.WaitForTimeoutAsync(1000);
    }

    /// <summary>Clicks the "Browse Issues" button to open the drawer.</summary>
    public async Task ClickBrowseIssuesAsync()
    {
        // Wait for the button to become enabled (depends on template selection triggering re-render)
        await _page.WaitForFunctionAsync(
            @"() => {
                const btn = document.querySelector('[data-testid=""browse-issues-btn""]');
                return btn && !btn.disabled;
            }",
            null,
            new() { Timeout = 10_000 });

        await _page.ClickAsync("[data-testid='browse-issues-btn']");
        // Wait for drawer to open and issues to load
        await _page.WaitForSelectorAsync(".dispatch-drawer.open", new() { Timeout = 10_000 });
    }

    /// <summary>Selects an issue from the drawer by its identifier.</summary>
    public async Task SelectIssueAsync(string identifier)
    {
        await _page.ClickAsync($"[data-testid='issue-row-{identifier}']");
    }

    /// <summary>Clicks the "Start Pipeline on #X" button in the drawer.</summary>
    public async Task ClickStartPipelineAsync()
    {
        await _page.ClickAsync("[data-testid='dispatch-issue-btn']");
    }

    /// <summary>Clicks the cancel button in the sidebar.</summary>
    public async Task ClickCancelAsync()
    {
        await _page.ClickAsync("[data-testid='cancel-pipeline-btn']");
    }

    /// <summary>Waits for a specific pipeline step to become active in the sidebar.</summary>
    public async Task WaitForStepAsync(PipelineStep step, int timeoutMs = 15_000)
    {
        await _page.WaitForSelectorAsync(
            $"[data-testid='pipeline-step-{step}'][data-step-state='active']",
            new() { Timeout = timeoutMs });
    }

    /// <summary>Waits for the pipeline to reach a terminal state (Completed, Failed, or Cancelled).</summary>
    public async Task WaitForCompletionAsync(int timeoutMs = 30_000)
    {
        await _page.WaitForSelectorAsync(
            "[data-testid='pipeline-step-Completed'][data-step-state='active'], " +
            "[data-testid='pipeline-step-Failed'][data-step-state='failed'], " +
            "[data-testid='pipeline-step-Cancelled'][data-step-state='cancelled']",
            new() { Timeout = timeoutMs });
    }

    /// <summary>Gets the PR link text from the summary, or null if not visible.</summary>
    public async Task<string?> GetPrLinkAsync()
    {
        var element = await _page.QuerySelectorAsync("[data-testid='pr-link']");
        return element is not null ? await element.TextContentAsync() : null;
    }

    /// <summary>Gets the failure reason text, or null if not visible.</summary>
    public async Task<string?> GetFailureReasonAsync()
    {
        var element = await _page.QuerySelectorAsync("[data-testid='failure-reason']");
        return element is not null ? await element.TextContentAsync() : null;
    }

    /// <summary>Gets all visible output lines from the output panel.</summary>
    public async Task<IReadOnlyList<string>> GetOutputLinesAsync()
    {
        var elements = await _page.QuerySelectorAllAsync("[data-testid='output-line']");
        var lines = new List<string>();
        foreach (var el in elements)
            lines.Add(await el.TextContentAsync() ?? "");
        return lines;
    }

    /// <summary>Checks if the output panel is visible.</summary>
    public async Task<bool> IsOutputPanelVisibleAsync()
    {
        var element = await _page.QuerySelectorAsync("[data-testid='output-panel']");
        return element is not null;
    }

    /// <summary>Clicks the "Browse Pull Requests" button to open the PR drawer.</summary>
    public async Task ClickBrowsePrsAsync()
    {
        await _page.WaitForFunctionAsync(
            @"() => {
                const btn = document.querySelector('[data-testid=""browse-prs-btn""]');
                return btn && !btn.disabled;
            }",
            null,
            new() { Timeout = 10_000 });

        await _page.ClickAsync("[data-testid='browse-prs-btn']");
        await _page.WaitForSelectorAsync(".dispatch-drawer.open", new() { Timeout = 10_000 });
    }

    /// <summary>
    /// Focuses the issue list element inside the currently-open drawer so that
    /// <c>@onkeydown</c> events reach <c>DispatchDrawerBase.HandleKeyDown</c>.
    /// Must be called after the drawer is open.
    /// </summary>
    public async Task FocusIssueListAsync()
    {
        var list = _page.Locator(".dispatch-drawer.open .agent-history-list");
        await list.WaitForAsync(new() { Timeout = 5_000 });
        await list.FocusAsync();
    }

    /// <summary>
    /// Focuses the list element inside the currently-open PR drawer so that
    /// <c>@onkeydown</c> events reach <c>DispatchDrawerBase.HandleKeyDown</c>.
    /// </summary>
    // TODO [WARNING]: FocusPrListAsync, FocusEpicListAsync, and FocusIssueListAsync are byte-for-byte identical.
    // They should be consolidated into a single FocusDrawerListAsync() method to avoid silent divergence if the
    // PR or epic drawer ever uses a different CSS class for its list element. See review findings:
    // Correctness line 178, TestQualityReviewer line 164.
    public async Task FocusPrListAsync()
    {
        var list = _page.Locator(".dispatch-drawer.open .agent-history-list");
        await list.WaitForAsync(new() { Timeout = 5_000 });
        await list.FocusAsync();
    }

    /// <summary>
    /// Focuses the list element inside the currently-open epic drawer.
    /// </summary>
    // TODO [WARNING]: See FocusPrListAsync — same duplication issue applies here.
    public async Task FocusEpicListAsync()
    {
        var list = _page.Locator(".dispatch-drawer.open .agent-history-list");
        await list.WaitForAsync(new() { Timeout = 5_000 });
        await list.FocusAsync();
    }

    /// <summary>
    /// Returns true when the shortcut help overlay backdrop is visible in the DOM.
    /// </summary>
    public async Task<bool> IsShortcutOverlayVisibleAsync()
    {
        var count = await _page.Locator(".shortcut-overlay-backdrop").CountAsync();
        return count > 0;
    }

    /// <summary>Clicks the "Browse Epics" button to open the epic drawer.</summary>
    public async Task ClickBrowseEpicsAsync()
    {
        await _page.WaitForFunctionAsync(
            @"() => {
                const btn = document.querySelector('[data-testid=""browse-epics-btn""]');
                return btn && !btn.disabled;
            }",
            null,
            new() { Timeout = 10_000 });

        await _page.ClickAsync("[data-testid='browse-epics-btn']");
        await _page.WaitForSelectorAsync(".dispatch-drawer.open", new() { Timeout = 10_000 });
    }

    /// <summary>Selects a PR from the drawer by its identifier.</summary>
    public async Task SelectPrAsync(string identifier)
    {
        await _page.ClickAsync($"[data-testid='pr-row-{identifier}']");
    }

    /// <summary>Clicks the "Start Review on PR #X" button in the PR drawer.</summary>
    public async Task ClickDispatchPrReviewAsync()
    {
        await _page.ClickAsync("[data-testid='dispatch-pr-btn']");
    }

    /// <summary>
    /// Asserts that the DRAFT badge is visible on the PR row for the given identifier.
    ///
    /// <para>
    /// The badge is rendered as <c>&lt;span class="badge-default"&gt;DRAFT&lt;/span&gt;</c> inside the
    /// <c>data-testid='pr-row-{identifier}'</c> element in <c>PrDispatchDrawer.razor</c>.
    /// CSS class <c>badge-default</c> is the correct class — there is no <c>draft-badge</c> class.
    /// </para>
    /// </summary>
    public async Task AssertDraftBadgeVisibleAsync(string identifier)
    {
        var badgeLocator = _page.Locator($"[data-testid='pr-row-{identifier}'] .badge-default");
        await badgeLocator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        var text = await badgeLocator.TextContentAsync();
        Assert.Contains("DRAFT", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asserts that the draft warning banner is visible in the selected-item section of the PR drawer.
    ///
    /// <para>
    /// The warning is rendered as:
    /// <c>&lt;div class="settings-status status-error"&gt;...This PR is a draft. Review will still be dispatched.&lt;/div&gt;</c>
    /// in <c>PrDispatchDrawer.razor</c> when the selected PR has <c>IsDraft = true</c>.
    /// </para>
    /// </summary>
    public async Task AssertDraftWarningVisibleAsync()
    {
        await _page.WaitForSelectorAsync(
            ".settings-status.status-error",
            new() { Timeout = 10_000 });
        var warningText = await _page.TextContentAsync(".settings-status.status-error");
        Assert.Contains("This PR is a draft", warningText, StringComparison.OrdinalIgnoreCase);
    }
}
