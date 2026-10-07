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
public sealed class AgentCodingPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public AgentCodingPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/agent-coding");

        // Wait for the page to render (prerendered HTML appears immediately)
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        await _page.WaitForCockpitPageReadyAsync();

        // Wait for AgentCoding.OnInitializedAsync to complete. The component sets
        // data-page-ready="true" on its root div at the end of OnInitializedAsync, after
        // PageService.InitializeAsync() has loaded templates, providers, and other config.
        // Without this wait, assertions on template-row content (e.g. Remove buttons) may
        // see an empty template table from the transient render before initialization finishes.
        await _page.WaitForSelectorAsync(
            ".cockpit-page[data-page-ready='true']",
            new() { Timeout = 15_000 });
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

    /// <summary>Clicks the "Browse Epics" button to open the epic dispatch drawer.</summary>
    public async Task ClickBrowseEpicsAsync()
    {
        // TODO [WARNING]: WaitForFunctionAsync + separate ClickAsync has a TOCTOU window: the
        // button could be re-disabled between the two calls if a template de-selection triggers
        // a re-render. The existing page-object helpers use WaitForSelectorAsync with
        // :not([disabled]) or rely on ClickAsync's built-in wait to avoid this gap. This is low
        // risk (only relevant under extreme re-render races) but is inconsistent with the rest
        // of the page object.

        // Wait for the button to become enabled (depends on template selection triggering re-render)
        await _page.WaitForFunctionAsync(
            @"() => {
                const btn = document.querySelector('[data-testid=""browse-epics-btn""]');
                return btn && !btn.disabled;
            }",
            null,
            new() { Timeout = 10_000 });

        await _page.ClickAsync("[data-testid='browse-epics-btn']");

        // TODO [WARNING]: ".dispatch-drawer.open" matches any open dispatch drawer (Issue, PR, or
        // Epic). If a prior test's state leaked and left another drawer open, this wait would
        // succeed for the wrong drawer. Consider asserting on a drawer-specific element (e.g. the
        // "Select Epic for Decomposition" header) to confirm the Epic drawer specifically opened.
        await _page.WaitForSelectorAsync(".dispatch-drawer.open", new() { Timeout = 10_000 });
    }

    /// <summary>Selects an epic from the epic drawer by its identifier.</summary>
    public async Task SelectEpicAsync(string identifier)
    {
        // TODO [WARNING]: identifier is interpolated directly into a CSS attribute selector. If
        // identifier contains a single-quote character, the selector becomes malformed and
        // Playwright throws a cryptic PlaywrightException rather than a clear "element not found"
        // message. All current callers pass numeric identifiers, so this is safe today, but a
        // future test using a GitLab-style prefixed identifier (e.g. "GL-42") or any identifier
        // with special CSS characters would fail here with a misleading error.
        await _page.ClickAsync($"[data-testid='epic-row-{identifier}']");
    }

    /// <summary>Clicks the "Start Decomposition on #X" button in the epic drawer.</summary>
    public async Task ClickDispatchEpicAsync()
    {
        await _page.ClickAsync("[data-testid='dispatch-epic-btn']");
    }
}
