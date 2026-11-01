using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the triage detail page at <c>/triage/{id}</c>.
/// Exposes locators and actions; asserts nothing.
/// </summary>
public sealed class TriageDetailPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public TriageDetailPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to <c>/triage/{id}</c> and waits for the page to be ready.</summary>
    public async Task NavigateAsync(Guid id) =>
        await _page.GotoCockpitPageAsync($"{_baseUrl}/triage/{id}");

    /// <summary>
    /// Waits until the detail container has <c>data-initialized="true"</c>, indicating
    /// that the initial data load has completed.
    /// </summary>
    public async Task WaitForLoadedAsync() =>
        await Assertions.Expect(_page.Locator("[data-testid=triage-detail][data-initialized=true]"))
                        .ToBeVisibleAsync();

    // ── Status / running ─────────────────────────────────────────────────

    /// <summary>The status pill (<c>data-testid=triage-status</c>).</summary>
    public ILocator Status => _page.Locator("[data-testid=triage-status]");

    /// <summary>The "Attempt N is investigating" running card (<c>data-testid=triage-running</c>).</summary>
    public ILocator Running => _page.Locator("[data-testid=triage-running]");

    /// <summary>The failed-attempt notice (<c>data-testid=triage-failed</c>).</summary>
    public ILocator Failed => _page.Locator("[data-testid=triage-failed]");

    // ── Request / attempts ───────────────────────────────────────────────

    /// <summary>The request side-card (<c>data-testid=triage-request</c>).</summary>
    public ILocator Request => _page.Locator("[data-testid=triage-request]");

    /// <summary>The attempts section (<c>data-testid=triage-attempts</c>).</summary>
    public ILocator Attempts => _page.Locator("[data-testid=triage-attempts]");

    // ── Actions bar ──────────────────────────────────────────────────────

    /// <summary>The actions bar region (<c>data-testid=triage-actions</c>).</summary>
    public ILocator Actions => _page.Locator("[data-testid=triage-actions]");

    /// <summary>The dismiss trigger button (<c>data-testid=triage-dismiss-open</c>).</summary>
    public ILocator DismissOpen => _page.Locator("[data-testid=triage-dismiss-open]");

    /// <summary>The re-run trigger button (<c>data-testid=triage-rerun-open</c>).</summary>
    public ILocator RerunOpen => _page.Locator("[data-testid=triage-rerun-open]");

    /// <summary>
    /// Clicks the dismiss button, fills the reason, then confirms.
    /// Mirrors the TriagePage.razor Panel.Dismiss flow.
    /// </summary>
    public async Task DismissAsync(string reason)
    {
        await _page.Locator("[data-testid=triage-dismiss-open]").ClickAsync();
        await _page.Locator("[data-testid=triage-dismiss-reason]").FillAsync(reason);
        // TODO: triage-dismiss-reason binds @bind (onchange). The reason commits when the input
        // blurs, which happens as a side-effect of the click below moving focus to the button.
        // Blazor serialises blur before the click handler on the circuit so this is generally
        // reliable, but if flakiness appears add an explicit Tab-press here to force the commit.
        await _page.Locator("[data-testid=triage-dismiss]").ClickAsync();
    }

    // ── Dismissed banner ─────────────────────────────────────────────────

    /// <summary>The dismissed warnbox (<c>data-testid=triage-dismissed</c>).</summary>
    public ILocator Dismissed => _page.Locator("[data-testid=triage-dismissed]");

    // ── Remaining test ids from TriagePage.razor ─────────────────────────

    /// <summary>The root detail container (<c>data-testid=triage-detail</c>).</summary>
    public ILocator Detail => _page.Locator("[data-testid=triage-detail]");

    /// <summary>The not-found element (<c>data-testid=triage-not-found</c>).</summary>
    public ILocator NotFound => _page.Locator("[data-testid=triage-not-found]");

    /// <summary>The run-link element (<c>data-testid=triage-run-link</c>).</summary>
    public ILocator RunLink => _page.Locator("[data-testid=triage-run-link]");

    /// <summary>The notice element (<c>data-testid=triage-notice</c>).</summary>
    public ILocator Notice => _page.Locator("[data-testid=triage-notice]");

    /// <summary>The error element (<c>data-testid=triage-error</c>).</summary>
    public ILocator Error => _page.Locator("[data-testid=triage-error]");

    /// <summary>The questions section (<c>data-testid=triage-questions</c>).</summary>
    public ILocator Questions => _page.Locator("[data-testid=triage-questions]");

    /// <summary>The re-run-with-answers button (<c>data-testid=triage-rerun-answers</c>).</summary>
    public ILocator RerunAnswers => _page.Locator("[data-testid=triage-rerun-answers]");

    /// <summary>The drafts section (<c>data-testid=triage-drafts</c>).</summary>
    public ILocator Drafts => _page.Locator("[data-testid=triage-drafts]");

    /// <summary>The issue source element (<c>data-testid=triage-issue-source</c>).</summary>
    public ILocator IssueSource => _page.Locator("[data-testid=triage-issue-source]");

    /// <summary>The created-issues section (<c>data-testid=triage-created-issues</c>).</summary>
    public ILocator CreatedIssues => _page.Locator("[data-testid=triage-created-issues]");

    /// <summary>The create-issues button (<c>data-testid=triage-create</c>).</summary>
    public ILocator Create => _page.Locator("[data-testid=triage-create]");

    // ── Draft cards ──────────────────────────────────────────────────────

    /// <summary>All individual draft cards (<c>data-testid=triage-draft</c>).</summary>
    public ILocator Draft => _page.Locator("[data-testid=triage-draft]");

    /// <summary>Draft selection checkboxes (<c>data-testid=triage-draft-select</c>).</summary>
    public ILocator DraftSelect => _page.Locator("[data-testid=triage-draft-select]");

    /// <summary>Draft edit buttons (<c>data-testid=triage-draft-edit</c>).</summary>
    public ILocator DraftEdit => _page.Locator("[data-testid=triage-draft-edit]");

    // ── Draft editor (TriageDraftEditor.razor) ───────────────────────────

    /// <summary>The draft editor drawer (<c>data-testid=triage-draft-editor</c>).</summary>
    public ILocator DraftEditor => _page.Locator("[data-testid=triage-draft-editor]");

    /// <summary>The repository selector in the editor (<c>data-testid=triage-draft-repo</c>).</summary>
    public ILocator DraftRepo => _page.Locator("[data-testid=triage-draft-repo]");

    /// <summary>The title input in the editor (<c>data-testid=triage-draft-title</c>).</summary>
    public ILocator DraftTitle => _page.Locator("[data-testid=triage-draft-title]");

    /// <summary>The body textarea in the editor (<c>data-testid=triage-draft-body</c>).</summary>
    public ILocator DraftBody => _page.Locator("[data-testid=triage-draft-body]");

    /// <summary>The reset button in the editor (<c>data-testid=triage-draft-reset</c>).</summary>
    public ILocator DraftReset => _page.Locator("[data-testid=triage-draft-reset]");

    /// <summary>The save button in the editor (<c>data-testid=triage-draft-save</c>).</summary>
    public ILocator DraftSave => _page.Locator("[data-testid=triage-draft-save]");

    // ── Created issues ───────────────────────────────────────────────────

    /// <summary>Links to created issues (<c>data-testid=triage-created-link</c>).</summary>
    public ILocator CreatedLink => _page.Locator("[data-testid=triage-created-link]");

    // ── Queue ────────────────────────────────────────────────────────────

    /// <summary>The queue checkbox (<c>data-testid=triage-queue</c>).</summary>
    public ILocator Queue => _page.Locator("[data-testid=triage-queue]");

    // ── Verdict / report (TriageReport.razor) ────────────────────────────

    /// <summary>The verdict summary text (<c>data-testid=triage-verdict</c>).</summary>
    public ILocator Verdict => _page.Locator("[data-testid=triage-verdict]");

    /// <summary>The verdict pill (<c>data-testid=triage-verdict-pill</c>).</summary>
    public ILocator VerdictPill => _page.Locator("[data-testid=triage-verdict-pill]");

    /// <summary>The investigated-checks section (<c>data-testid=triage-investigated</c>).</summary>
    public ILocator Investigated => _page.Locator("[data-testid=triage-investigated]");

    /// <summary>The evidence section (<c>data-testid=triage-evidence</c>).</summary>
    public ILocator Evidence => _page.Locator("[data-testid=triage-evidence]");

    /// <summary>
    /// The read-only questions section rendered by TriageReport when the user cannot act
    /// (<c>data-testid=triage-report-questions</c>).
    /// </summary>
    public ILocator ReportQuestions => _page.Locator("[data-testid=triage-report-questions]");
}
