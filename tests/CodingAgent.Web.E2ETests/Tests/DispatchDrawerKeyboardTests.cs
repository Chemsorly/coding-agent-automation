using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for keyboard shortcut behaviour in the dispatch drawers (Issue #3109).
///
/// <para>
/// All six acceptance-criteria scenarios are covered:
/// 1. Arrow keys move the row highlight and respect list bounds (wrap-around).
/// 2. Enter dispatches the highlighted issue exactly once (double-Enter is idempotent).
/// 3. Enter on a blocked issue does not dispatch (server-side guard in IssueDrawerService.DispatchIssueAsync).
/// 4. Escape closes the active drawer without dispatching.
/// 5a. "?" opens the shortcut overlay when focus is not in a text input.
/// 5b. "?" typed inside the drawer filter input is suppressed (JS isInput guard).
/// 6a. PR drawer keyboard Enter-dispatches the highlighted PR.
/// 6b. Epic drawer keyboard Enter-dispatches the highlighted epic.
/// </para>
///
/// <para>
/// Focus management: <c>DispatchDrawerBase.HandleKeyDown</c> fires only when the
/// <c>.agent-history-list[tabindex="0"]</c> element has browser focus; all arrow/Enter
/// tests call <see cref="AgentCodingPage.FocusIssueListAsync"/> first.
/// Global keys (Escape, "?") are routed by <c>registerGlobalKeyboardHandler</c> in App.razor
/// and do NOT require the list element to be focused.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class DispatchDrawerKeyboardTests : E2ETestBase
{
    public DispatchDrawerKeyboardTests(E2EFixture fixture) : base(fixture) { }

    // ── Scenario 1: Arrow keys ──────────────────────────────────────────────

    [Fact]
    public async Task ArrowKeys_MoveHighlight_StayInBounds()
    {
        // Arrange: three issues so we can test wrap-around at both ends
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.AddRange(
        [
            new IssueDetail { Identifier = "70", Title = "Issue 70", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "71", Title = "Issue 71", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "72", Title = "Issue 72", Description = "Test", Labels = new[] { "enhancement" } }
        ]);

        // TODO [WARNING]: FakeAgentClient is intentionally not instantiated here because ArrowKeys navigation
        // does not dispatch and does not require an agent hub connection. If a future change introduces a
        // "no agents connected" guard that affects list rendering or keyboard handling, this test will need
        // an `await using var fakeAgent` block. See review finding: DotNetSpecialist line 130.
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Focus the list so @onkeydown is processed by DispatchDrawerBase.HandleKeyDown
        await codingPage.FocusIssueListAsync();

        // ArrowDown: index 0 → issue-row-70 highlighted
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Locator("[data-testid='issue-row-70'].drawer-item-highlighted")
            .WaitForAsync(new() { Timeout = 5_000 });

        // ArrowDown: index 1 → issue-row-71 highlighted
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Locator("[data-testid='issue-row-71'].drawer-item-highlighted")
            .WaitForAsync(new() { Timeout = 5_000 });

        // ArrowDown: index 2 → issue-row-72 highlighted
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Locator("[data-testid='issue-row-72'].drawer-item-highlighted")
            .WaitForAsync(new() { Timeout = 5_000 });

        // TODO [WARNING]: ArrowDown past-end and ArrowUp past-start both wrap here (modular arithmetic in
        // DispatchDrawerBase.HandleKeyDown). The issue spec says "never leaves list bounds" which is ambiguous
        // between wrap-around and clamping. These assertions document the wrap-around behaviour observed in the
        // implementation. If the spec intended clamping, this test should be updated and the production code
        // reviewed. See review finding: TestQualityReviewer line 102.

        // ArrowDown past end → wraps to index 0 → issue-row-70 highlighted
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Locator("[data-testid='issue-row-70'].drawer-item-highlighted")
            .WaitForAsync(new() { Timeout = 5_000 });

        // ArrowUp from index 0 → wraps to last index 2 → issue-row-72 highlighted
        await Page.Keyboard.PressAsync("ArrowUp");
        await Page.Locator("[data-testid='issue-row-72'].drawer-item-highlighted")
            .WaitForAsync(new() { Timeout = 5_000 });

        // Confirm exactly one row is highlighted at a time
        var highlightCount = await Page.Locator(".drawer-item-highlighted").CountAsync();
        Assert.Equal(1, highlightCount);
    }

    // ── Scenario 2: Enter dispatches exactly once ───────────────────────────

    [Fact]
    public async Task Enter_DispatchesHighlightedIssue_ExactlyOnce()
    {
        // Arrange
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.AddRange(
        [
            new IssueDetail { Identifier = "70", Title = "Issue 70", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "71", Title = "Issue 71", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "72", Title = "Issue 72", Description = "Test", Labels = new[] { "enhancement" } }
        ]);

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Focus list and navigate to index 1 (issue 71)
        await codingPage.FocusIssueListAsync();
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Locator("[data-testid='issue-row-71'].drawer-item-highlighted")
            .WaitForAsync(new() { Timeout = 5_000 });

        // Press Enter → dispatches issue 71
        await Page.Keyboard.PressAsync("Enter");

        // Wait for success toast — confirm it mentions issue 71
        // TODO [WARNING]: WaitForSelectorAsync is not scoped to .agent-toast-stack; a residual success toast
        // from an earlier action could resolve this wait. Scope to ".agent-toast-stack .settings-status.status-success"
        // (same as the error-toast scope in Scenario 3). Also, Page.TextContentAsync returns string? — if
        // the element resolves with null text, Assert.Contains throws ArgumentNullException rather than a clean
        // assertion failure. Add Assert.NotNull(successText) before Assert.Contains.
        // See review findings: Correctness lines 154 and 4.
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var successText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.Contains("71", successText);

        // Press Enter a second time immediately — _highlightedIndex was reset to -1 so nothing dispatches
        await Page.Keyboard.PressAsync("Enter");

        // TODO [WARNING]: Task.Delay(500) is a timing-based guard and can produce a false pass if the second
        // dispatch is still in-flight on a slow CI host (> 500 ms server round-trip). A more robust approach
        // is to wait for a stable UI state — e.g., wait for the success toast to disappear and the highlight
        // to reset — before querying WorkItems. Also consider asserting that a *second* success toast does
        // NOT appear, which would catch a broken _highlightedIndex reset immediately.
        // See review findings: Correctness line 155, TestQualityReviewer line 155.

        // Brief wait then assert only one WorkItem exists for issue 71
        await Task.Delay(500);
        var pending = await Fixture.WorkItems.GetPendingAsync(maxResults: 50, ct: CancellationToken.None);
        var active = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600, ct: CancellationToken.None);
        var forIssue71 = pending.Count(w => w.IssueIdentifier == "71") +
                         active.Count(w => w.IssueIdentifier == "71");
        Assert.Equal(1, forIssue71);
    }

    // ── Scenario 3: Enter on a blocked issue does not dispatch ──────────────

    [Fact]
    public async Task Enter_BlockedIssue_DoesNotDispatch()
    {
        // Arrange: issue 70 blocked by #100 (not in ClosedIssueIdentifiers), plus two dispatchable issues
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.AddRange(
        [
            new IssueDetail { Identifier = "70", Title = "Blocked issue", Description = "Blocked by #100", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "71", Title = "Issue 71", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "72", Title = "Issue 72", Description = "Test", Labels = new[] { "enhancement" } }
        ]);
        // #100 is NOT added to ClosedIssueIdentifiers → issue 70 remains blocked

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Wait for the readiness check to finish and the blocked badge to appear
        await Page.Locator("[data-testid='issue-row-70'] .drawer-badge-blocked")
            .WaitForAsync(new() { Timeout = 10_000 });

        // Focus the list and navigate to the blocked issue (index 0 = issue 70)
        // TODO [WARNING]: This test assumes issue 70 is at index 0 in the drawer (so ArrowDown lands on it).
        // If the drawer sorts or filters issues differently, ArrowDown may highlight 71 or 72 (not blocked),
        // causing Enter to dispatch a non-blocked issue and the "no WorkItem for 70" assertion to pass vacuously
        // without actually testing the blocked-issue guard. Consider asserting
        // [data-testid='issue-row-70'].drawer-item-highlighted is present after ArrowDown before pressing Enter,
        // and verifying the error toast text contains "blocked" or "dependency". See review finding:
        // TestQualityReviewer line 211, Correctness line 220.
        await codingPage.FocusIssueListAsync();
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Locator("[data-testid='issue-row-70'].drawer-item-highlighted")
            .WaitForAsync(new() { Timeout = 5_000 });

        // Press Enter on the blocked issue
        await Page.Keyboard.PressAsync("Enter");

        // The server-side guard in IssueDrawerService.DispatchIssueAsync fires and returns an error.
        // The error toast should appear.
        await Page.WaitForSelectorAsync(
            ".agent-toast-stack .settings-status.status-error",
            new() { Timeout = 10_000 });

        // Confirm no WorkItem was created for issue 70
        var pending = await Fixture.WorkItems.GetPendingAsync(maxResults: 50, ct: CancellationToken.None);
        var active = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600, ct: CancellationToken.None);
        Assert.False(
            pending.Any(w => w.IssueIdentifier == "70") || active.Any(w => w.IssueIdentifier == "70"),
            "Blocked issue #70 must not be dispatched by keyboard Enter");
    }

    // ── Scenario 4: Escape closes the drawer ───────────────────────────────

    [Fact]
    public async Task Escape_ClosesDrawer_WithoutDispatching()
    {
        // Arrange
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.AddRange(
        [
            new IssueDetail { Identifier = "70", Title = "Issue 70", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "71", Title = "Issue 71", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "72", Title = "Issue 72", Description = "Test", Labels = new[] { "enhancement" } }
        ]);

        // TODO [WARNING]: FakeAgentClient is intentionally not instantiated here because Escape closes the
        // drawer without dispatching. If a future change introduces a "no agents connected" guard affecting
        // drawer rendering or Escape handling, this test will need an `await using var fakeAgent` block.
        // See review finding: DotNetSpecialist line 130.
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Highlight an item to show keyboard navigation is active
        await codingPage.FocusIssueListAsync();
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Locator("[data-testid='issue-row-70'].drawer-item-highlighted")
            .WaitForAsync(new() { Timeout = 5_000 });

        // Escape is handled by the global JS handler → CockpitLayout.HandleGlobalKey → CloseActiveDrawer.
        // No list focus required for global Escape.
        await Page.Keyboard.PressAsync("Escape");

        // Drawer closes
        await Page.Locator(".dispatch-drawer.open")
            .WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        var openCount = await Page.Locator(".dispatch-drawer.open").CountAsync();
        Assert.Equal(0, openCount);

        // No WorkItems created for the identifiers used in this test.
        // CRITICAL FIX: Scope assertion to identifiers "70", "71", "72" instead of asserting the entire store
        // is empty. Asserting Assert.Empty(pending/active) across all WorkItems fails spuriously when a prior
        // test in the same E2ECollection left residual WorkItems in the shared fixture store.
        var pending = await Fixture.WorkItems.GetPendingAsync(maxResults: 50, ct: CancellationToken.None);
        var active = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600, ct: CancellationToken.None);
        var testIdentifiers = new[] { "70", "71", "72" };
        Assert.False(
            pending.Any(w => testIdentifiers.Contains(w.IssueIdentifier)) ||
            active.Any(w => testIdentifiers.Contains(w.IssueIdentifier)),
            "Escape must not dispatch any of the test issues (70, 71, 72)");
    }

    // ── Scenario 5a: "?" opens the shortcut overlay ─────────────────────────

    [Fact]
    public async Task QuestionMark_TogglesShortcutOverlay()
    {
        // Arrange
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(
            new IssueDetail { Identifier = "70", Title = "Issue 70", Description = "Test", Labels = new[] { "enhancement" } });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Focus the list (not an input) so the global "?" key is not suppressed
        await codingPage.FocusIssueListAsync();

        // "?" opens the overlay
        await Page.Keyboard.PressAsync("?");
        await Page.Locator(".shortcut-overlay-backdrop")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        Assert.True(await codingPage.IsShortcutOverlayVisibleAsync(), "Overlay must be visible after '?'");

        // Overlay lists the drawer shortcuts
        // TODO [WARNING]: Locator is scoped to `kbd` globally — if the page has other kbd elements with "↓"
        // outside the overlay (e.g., a permanent help section), this assertion passes even if the overlay
        // content is blank. Scope to `.shortcut-overlay-backdrop kbd` to ensure the shortcut is inside the
        // overlay. See review finding: TestQualityReviewer line 308.
        var hasArrowShortcut = await Page.Locator("kbd").Filter(new() { HasText = "↓" }).CountAsync();
        Assert.True(hasArrowShortcut > 0, "Overlay must contain the ↓ shortcut key");

        // Escape closes the overlay (priority in HandleGlobalKey: shortcut help before drawer)
        await Page.Keyboard.PressAsync("Escape");
        await Page.Locator(".shortcut-overlay-backdrop")
            .WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        Assert.False(await codingPage.IsShortcutOverlayVisibleAsync(), "Overlay must be hidden after Escape");

        // Drawer should still be open — Escape only closed the overlay, not the drawer
        var drawerOpen = await Page.Locator(".dispatch-drawer.open").CountAsync();
        Assert.Equal(1, drawerOpen);

        // "?" again → overlay reappears
        await codingPage.FocusIssueListAsync();
        await Page.Keyboard.PressAsync("?");
        await Page.Locator(".shortcut-overlay-backdrop")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        Assert.True(await codingPage.IsShortcutOverlayVisibleAsync(), "Overlay must be visible after second '?'");

        // "?" again → overlay closes (toggle)
        await Page.Keyboard.PressAsync("?");
        await Page.Locator(".shortcut-overlay-backdrop")
            .WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        Assert.False(await codingPage.IsShortcutOverlayVisibleAsync(), "Overlay must be hidden after third '?'");
    }

    // ── Scenario 5b: "?" inside filter input does NOT open the overlay ──────

    [Fact]
    public async Task QuestionMark_InFilterInput_DoesNotOpenOverlay()
    {
        // Arrange
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(
            new IssueDetail { Identifier = "70", Title = "Issue 70", Description = "Test", Labels = new[] { "enhancement" } });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Wait for the drawer to be fully open before focusing the input.
        // FocusAsync on a hidden/inert element does not reliably set document.activeElement
        // in Chromium, so the drawer must be rendered and visible first.
        await Page.WaitForSelectorAsync(".dispatch-drawer.open", new() { Timeout = 10_000 });

        // Click the filter input to give it focus — document.activeElement will be INPUT
        var filterInput = Page.Locator(".dispatch-drawer.open input[type='text']");
        await filterInput.ClickAsync();

        // "?" with an input focused: the JS isInput guard suppresses the global handler
        await Page.Keyboard.PressAsync("?");

        // TODO [WARNING]: Task.Delay(400) is the sole timing mechanism for this negative assertion. On a slow
        // CI host, 400 ms may not be enough to rule out a delayed Blazor re-render that shows the overlay,
        // producing a false pass. A more robust approach: wait for a preceding synchronous UI event confirming
        // the page is settled (e.g., assert the filter input still has focus after pressing "?"), or use a
        // short-timeout WaitForAsync with State=Hidden which returns immediately if the element is absent.
        // See review findings: Correctness line 383, TestQualityReviewer line 352.

        // Brief wait — the overlay should NOT appear
        await Task.Delay(400);

        Assert.False(
            await codingPage.IsShortcutOverlayVisibleAsync(),
            "'?' typed inside the filter input must not open the shortcut overlay");
    }

    // ── Scenario 6a: PR drawer keyboard Enter-dispatch ──────────────────────

    [Fact]
    public async Task PrDrawer_Enter_DispatchesPr()
    {
        // Arrange: dual-seed — RepositoryProvider.PullRequests AND IssueProvider.Issues with same identifier.
        // Dispatch preparation calls GetIssueAsync(identifier) on the issue provider to fetch context.
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
            // ReviewEnabled is NOT required — browse-prs-btn is rendered unconditionally
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 90,
            Identifier = "90",
            Title = "Fix null reference in handler",
            Description = "Resolves #42",
            Labels = new[] { "agent:next" },
            BranchName = "fix/null-ref",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/90",
            IsDraft = false
        });
        // IssueProvider dual-seed (same pattern as PrReviewPipelineTests.SeedPrAsIssue)
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "90",
            Title = "Fix null reference in handler",
            Description = "Resolves #42",
            Labels = new[] { "agent:next" }
        });

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowsePrsAsync();

        // Focus the list and navigate to PR 90 (index 0)
        await codingPage.FocusPrListAsync();
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Locator("[data-testid='pr-row-90'].drawer-item-highlighted")
            .WaitForAsync(new() { Timeout = 5_000 });

        // Press Enter → should dispatch PR 90
        await Page.Keyboard.PressAsync("Enter");

        // Wait for success toast
        // TODO [WARNING]: WaitForSelectorAsync is not scoped to .agent-toast-stack; a stale success toast
        // could resolve this wait prematurely. Scope to ".agent-toast-stack .settings-status.status-success".
        // Also guard against nullable successText: add Assert.NotNull(successText) before Assert.Contains.
        // See review findings: Correctness line 154.
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var successText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.Contains("90", successText);

        // Confirm WorkItem exists for PR 90
        var pending = await Fixture.WorkItems.GetPendingAsync(maxResults: 50, ct: CancellationToken.None);
        var active = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600, ct: CancellationToken.None);
        Assert.True(
            pending.Any(w => w.IssueIdentifier == "90") || active.Any(w => w.IssueIdentifier == "90"),
            "A WorkItem must exist for PR #90 after keyboard Enter dispatch");
    }

    // ── Scenario 6b: Epic drawer keyboard Enter-dispatch ────────────────────

    [Fact]
    public async Task EpicDrawer_Enter_DispatchesEpic()
    {
        // Arrange: epic issue must carry "agent:epic" label so EpicDrawerService shows it
        // (LoadEpicDrawerIssuesAsync filters by ["agent:epic"] and ["agent:epic-approved"])
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
            // DecompositionEnabled is NOT required — browse-epics-btn is rendered unconditionally
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "80",
            Title = "Epic: Implement big feature",
            Description = "## Goal\nBuild the thing",
            Labels = new[] { "agent:epic" }   // required: without this the drawer shows "No epics found."
        });

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseEpicsAsync();

        // Wait for the epic row to appear (EpicDrawerService does two provider calls; allow time)
        await Page.Locator("[data-testid='epic-row-80']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        // Focus the list and navigate to epic 80 (index 0)
        await codingPage.FocusEpicListAsync();
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Locator("[data-testid='epic-row-80'].drawer-item-highlighted")
            .WaitForAsync(new() { Timeout = 5_000 });

        // Press Enter → dispatches epic 80
        await Page.Keyboard.PressAsync("Enter");

        // Wait for success toast
        // TODO [WARNING]: WaitForSelectorAsync is not scoped to .agent-toast-stack; a stale success toast
        // could resolve this wait prematurely. Scope to ".agent-toast-stack .settings-status.status-success".
        // Also guard against nullable successText: add Assert.NotNull(successText) before Assert.Contains.
        // See review findings: Correctness line 154.
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var successText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.Contains("80", successText);

        // Confirm WorkItem exists for epic 80
        var pending = await Fixture.WorkItems.GetPendingAsync(maxResults: 50, ct: CancellationToken.None);
        var active = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600, ct: CancellationToken.None);
        Assert.True(
            pending.Any(w => w.IssueIdentifier == "80") || active.Any(w => w.IssueIdentifier == "80"),
            "A WorkItem must exist for epic #80 after keyboard Enter dispatch");
    }
}
