using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.PageObjects;

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

        // Allow time for the Blazor Server circuit to connect via SignalR
        // and for event handlers to be attached to DOM elements.
        await _page.WaitForTimeoutAsync(3000);
    }

    /// <summary>Selects a template from the Manual Dispatch dropdown by its display text.</summary>
    public async Task SelectTemplateAsync(string templateName)
    {
        var select = _page.Locator("[data-testid='template-select']");

        // Wait for the template option to appear in the DOM
        await _page.WaitForFunctionAsync(
            @"(name) => {
                const select = document.querySelector('[data-testid=""template-select""]');
                if (!select) return false;
                return Array.from(select.options).some(o => o.text === name);
            }",
            templateName,
            new() { Timeout = 10_000 });

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
}
