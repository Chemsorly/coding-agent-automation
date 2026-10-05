using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for keyboard shortcuts on the dispatch drawers.
///
/// Scenarios covered:
/// 1. Arrow keys move highlight within list bounds (ArrowDown / ArrowUp).
/// 2. Enter dispatches the highlighted item exactly once; a fast second Enter does not double-dispatch.
/// 3. Enter on a blocked (dependency-gated) issue is a no-op at the WorkItem level.
/// 4. Escape closes the drawer without dispatching.
/// 5. "?" opens the shortcut help overlay; Esc and a second "?" close it;
///    "?" typed in the filter input does not open the overlay.
/// 6. Enter-dispatch on the PR drawer and the Epic drawer (both share DispatchDrawerBase).
///
/// The page re-renders the open drawer whenever something else on it changes (loop status,
/// readiness checks, dispatch state), so a key press can meet a parent re-render;
/// DispatchDrawerBase.OnParametersSet keeps the highlight across those.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class DispatchDrawerKeyboardShortcutTests : E2ETestBase
{
    public DispatchDrawerKeyboardShortcutTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Seeds the minimal template + agent profile used by most scenarios in this class.</summary>
    private async Task SeedDefaultTemplateAsync(string templateId = "template-kbd")
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Keyboard Template",
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
    }

    /// <summary>
    /// Focuses the item list inside the open drawer so subsequent keyboard events land on it.
    /// The list is the element with [tabindex="0"] that has the @onkeydown handler.
    /// </summary>
    private async Task FocusDrawerListAsync()
    {
        // Scoped to the open drawer: all three drawers stay in the DOM, and a hidden one keeps its
        // list while it has a template. Focus the element directly instead of clicking it — a click
        // lands on whatever is under the pointer, and a click on a row selects it (the issue drawer
        // toggles selection, so a later Enter on that row would deselect it instead of dispatching).
        // Then wait until document.activeElement confirms the list has focus before sending keys.
        const string listSelector = ".dispatch-drawer.open .agent-history-list";
        var list = Page.Locator(listSelector);
        await list.WaitForAsync(new() { Timeout = 10_000 });
        await list.FocusAsync();
        await Page.WaitForFunctionAsync(
            "(selector) => document.activeElement?.matches(selector)",
            listSelector,
            new PageWaitForFunctionOptions { Timeout = 5_000 });
    }

    /// <summary>
    /// Counts the distinct work items for an issue across the pending and active lists. The connected
    /// fake agent can pick an item up between the two reads, so pending is read first: the item then shows
    /// up in both lists and is counted once, instead of in neither.
    /// </summary>
    private async Task<int> CountWorkItemsForIssueAsync(string issueIdentifier)
    {
        var pending = await Fixture.WorkItems.GetPendingAsync(maxResults: 50, ct: CancellationToken.None);
        var active = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600, ct: CancellationToken.None);
        return pending.Where(w => w.IssueIdentifier == issueIdentifier).Select(w => w.Id)
            .Union(active.Where(w => w.IssueIdentifier == issueIdentifier).Select(w => w.Id))
            .Count();
    }

    // ── Scenario 1: Arrow key navigation ────────────────────────────────────────

    [Fact]
    public async Task ArrowKeys_MoveHighlightWithinBounds()
    {
        // Arrange: 3 dispatchable issues
        await SeedDefaultTemplateAsync();
        Fixture.IssueProvider.Issues.AddRange(new[]
        {
            new IssueDetail { Identifier = "10", Title = "Issue 10", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "11", Title = "Issue 11", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "12", Title = "Issue 12", Description = "Test", Labels = new[] { "enhancement" } },
        });

        await using var fakeAgent = new FakeAgentClient("kbd-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Keyboard Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Confirm all three rows are visible
        await Page.WaitForSelectorAsync("[data-testid='issue-row-10']", new() { Timeout = 10_000 });
        await Page.WaitForSelectorAsync("[data-testid='issue-row-11']", new() { Timeout = 5_000 });
        await Page.WaitForSelectorAsync("[data-testid='issue-row-12']", new() { Timeout = 5_000 });

        // TODO [WARNING]: These ArrowDown assertions assume the fake provider returns issues in insertion
        // order (10, 11, 12). If the provider sorts issues by identifier or title the row-index mapping
        // will be wrong and all three position assertions below will be fragile.
        // Verify that FakeIssueProvider preserves insertion order, or make the assertions order-independent.
        await FocusDrawerListAsync();

        // Press ArrowDown once → row 0 (issue-row-10) gets the highlight class
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('[data-testid=\"issue-row-10\"]')?.classList.contains('drawer-item-highlighted')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // ArrowDown again → row 1 (issue-row-11)
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('[data-testid=\"issue-row-11\"]')?.classList.contains('drawer-item-highlighted')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // ArrowDown once more → row 2 (issue-row-12)
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('[data-testid=\"issue-row-12\"]')?.classList.contains('drawer-item-highlighted')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // ArrowDown on the last row → wraps back to row 0 (modular wrap)
        // TODO [WARNING]: The acceptance criterion says "never leaves the list bounds", which is
        // satisfied by both modular wrap and clamping. These assertions encode the specific
        // modular-wrap behaviour implemented in DispatchDrawerBase.HandleKeyDown
        // ((_highlightedIndex + 1) % Count). If the implementation is ever changed to clamp at
        // the boundary instead of wrapping, these WaitForFunctionAsync calls will fail even though
        // the stated requirement is still met. Update these assertions if the boundary strategy changes.
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('[data-testid=\"issue-row-10\"]')?.classList.contains('drawer-item-highlighted')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // ArrowUp from row 0 → wraps to row 2 (last item)
        await Page.Keyboard.PressAsync("ArrowUp");
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('[data-testid=\"issue-row-12\"]')?.classList.contains('drawer-item-highlighted')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // ArrowUp again → row 1
        await Page.Keyboard.PressAsync("ArrowUp");
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('[data-testid=\"issue-row-11\"]')?.classList.contains('drawer-item-highlighted')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // The highlight never leaves the list: exactly one row carries it.
        Assert.Equal(1, await Page.Locator(".drawer-item-highlighted").CountAsync());
    }

    // ── Scenario 2: Enter dispatches once ───────────────────────────────────────

    [Fact]
    public async Task Enter_DispatchesHighlightedItemOnce()
    {
        // Arrange
        await SeedDefaultTemplateAsync();
        Fixture.IssueProvider.Issues.AddRange(new[]
        {
            new IssueDetail { Identifier = "20", Title = "Issue 20", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "21", Title = "Issue 21", Description = "Test", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "22", Title = "Issue 22", Description = "Test", Labels = new[] { "enhancement" } },
        });

        await using var fakeAgent = new FakeAgentClient("kbd-agent-2", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Keyboard Template");
        await codingPage.ClickBrowseIssuesAsync();

        await Page.WaitForSelectorAsync("[data-testid='issue-row-21']", new() { Timeout = 10_000 });
        await FocusDrawerListAsync();

        // Move to row 1 (issue #21: index=1, two ArrowDowns from -1)
        await Page.Keyboard.PressAsync("ArrowDown"); // index 0 → issue-row-20
        await Page.Keyboard.PressAsync("ArrowDown"); // index 1 → issue-row-21
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('[data-testid=\"issue-row-21\"]')?.classList.contains('drawer-item-highlighted')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Press Enter — should dispatch issue #21
        await Page.Keyboard.PressAsync("Enter");

        // Wait for success toast
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var toastText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.NotNull(toastText);
        Assert.Contains("#21", toastText);

        // Verify exactly one work item was created for issue #21
        // TODO [WARNING]: GetActiveAsync(olderThanSeconds: -3600) uses a negative value; verify the
        // fixture interprets this as "items created within the last hour" (absolute age < 3600s).
        // If the implementation treats it literally as "older than -3600 seconds" the result set may
        // always be empty and the assertion below would fail for the wrong reason.
        Assert.Equal(1, await CountWorkItemsForIssueAsync("21"));

        // Press Enter a second time quickly (self-disabling after first dispatch) — no second item
        await Page.Keyboard.PressAsync("Enter");

        // Poll for up to 2 seconds to confirm no second work item is created for issue #21.
        // Using WaitUntilAsync instead of Task.Delay so that a genuine double-dispatch that
        // completes at any point in the window is caught rather than raced past by a fixed sleep.
        // The condition stays true the whole window only if the count remains at 1; it throws
        // TimeoutException if a second item arrives, which is the failure we want to detect.
        // NOTE: WaitUntilAsync returns as soon as condition() returns false, i.e. it does NOT
        // poll until timeout when the assertion is expected to hold. We instead use a stable-count
        // approach: wait the full 2 s while continuously asserting count == 1, so any late-arriving
        // second item within that window triggers a test failure.
        var stableDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < stableDeadline)
        {
            Assert.Equal(1, await CountWorkItemsForIssueAsync("21"));
            await Task.Delay(50);
        }

        // Final snapshot after the stability window
        Assert.Equal(1, await CountWorkItemsForIssueAsync("21"));
    }

    // ── Scenario 3: Enter on a blocked issue is a no-op ─────────────────────────

    [Fact]
    public async Task Enter_BlockedIssue_DoesNotDispatch()
    {
        // Arrange: issue #30 is blocked by open dependency #100
        await SeedDefaultTemplateAsync();
        Fixture.IssueProvider.Issues.AddRange(new[]
        {
            new IssueDetail { Identifier = "30", Title = "Blocked issue", Description = "Blocked by #100", Labels = new[] { "enhancement" } },
            new IssueDetail { Identifier = "31", Title = "Normal issue",  Description = "Test", Labels = new[] { "enhancement" } },
        });
        // Do NOT add #100 to ClosedIssueIdentifiers → it stays open → #30 is blocked

        await using var fakeAgent = new FakeAgentClient("kbd-agent-3", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Keyboard Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Wait for the readiness check to complete and show the blocked badge on row #30
        var issueRow30 = Page.Locator("[data-testid='issue-row-30']");
        await issueRow30.Locator(".drawer-badge-blocked").WaitForAsync(new() { Timeout = 10_000 });

        await FocusDrawerListAsync();

        // Move highlight to row #30 (index 0)
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('[data-testid=\"issue-row-30\"]')?.classList.contains('drawer-item-highlighted')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Press Enter — DispatchDrawerBase.HandleKeyDown calls SelectItem then DispatchSelected.
        // DispatchIssueAsync server-side re-checks the dependency and returns an error for blocked issues.
        await Page.Keyboard.PressAsync("Enter");

        // The rejection toast is the signal that the dispatch attempt has finished, so the
        // work-item query below cannot run ahead of it. It also pins that the user is told why.
        await Page.Locator(".settings-status.status-error", new() { HasText = "blocked by open dependencies" })
            .WaitForAsync(new() { Timeout = 10_000 });
        Assert.Equal(0, await Page.Locator(".settings-status.status-success").CountAsync());

        var active = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600, ct: CancellationToken.None);
        var pending = await Fixture.WorkItems.GetPendingAsync(maxResults: 50, ct: CancellationToken.None);
        Assert.False(
            active.Any(w => w.IssueIdentifier == "30") || pending.Any(w => w.IssueIdentifier == "30"),
            "Blocked issue #30 must not produce a work item when Enter is pressed");

        // Verify the blocked badge is still visible (no side-effects)
        var badgeCount = await issueRow30.Locator(".drawer-badge-blocked").CountAsync();
        Assert.True(badgeCount > 0, "Blocked badge should still be visible after Enter on a blocked issue");
    }

    // ── Scenario 4: Escape closes the drawer ────────────────────────────────────

    [Fact]
    public async Task Escape_ClosesDrawerWithoutDispatching()
    {
        // Arrange
        await SeedDefaultTemplateAsync();
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "40",
            Title = "Escape test issue",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("kbd-agent-4", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Keyboard Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Verify drawer is open
        await Page.WaitForSelectorAsync(".dispatch-drawer.open", new() { Timeout = 10_000 });

        // Sub-scenario 4a: Press Escape via the global handler (document-level keydown) — focus is NOT
        // on the list, so this exercises the CockpitLayout.HandleGlobalKey → CloseActiveDrawer path.
        await Page.Keyboard.PressAsync("Escape");

        // Verify drawer closes (loses the "open" class or becomes hidden)
        await Page.WaitForFunctionAsync(
            "() => !document.querySelector('.dispatch-drawer.open')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Verify no dispatch happened
        var active = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600, ct: CancellationToken.None);
        var pending = await Fixture.WorkItems.GetPendingAsync(maxResults: 50, ct: CancellationToken.None);
        Assert.False(
            active.Any(w => w.IssueIdentifier == "40") || pending.Any(w => w.IssueIdentifier == "40"),
            "Issue #40 must not be dispatched after pressing Escape (global path)");

        // Sub-scenario 4b: Reopen the drawer and press Escape while the list has focus.
        // This exercises the DispatchDrawerBase.HandleKeyDown Escape branch, which is separate
        // from the global CockpitLayout handler and was not previously covered.
        await codingPage.ClickBrowseIssuesAsync();
        await Page.WaitForSelectorAsync(".dispatch-drawer.open", new() { Timeout = 10_000 });
        await FocusDrawerListAsync();

        // Press Escape from within the focused list → DispatchDrawerBase.HandleKeyDown fires
        await Page.Keyboard.PressAsync("Escape");

        // Verify drawer closes via the drawer-internal Escape handler
        await Page.WaitForFunctionAsync(
            "() => !document.querySelector('.dispatch-drawer.open')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Confirm still no dispatch after drawer-internal Escape
        active = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600, ct: CancellationToken.None);
        pending = await Fixture.WorkItems.GetPendingAsync(maxResults: 50, ct: CancellationToken.None);
        Assert.False(
            active.Any(w => w.IssueIdentifier == "40") || pending.Any(w => w.IssueIdentifier == "40"),
            "Issue #40 must not be dispatched after pressing Escape from within the focused list (drawer-internal path)");
    }

    // ── Scenario 5: Shortcut help overlay ──────────────────────────────────────

    [Fact]
    public async Task QuestionMark_TogglesShortcutOverlay_AndFilterInputDoesNotOpen()
    {
        // Arrange: just need the page to load
        await SeedDefaultTemplateAsync();
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "50",
            Title = "Overlay test issue",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Keyboard Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Scenario 5a: "?" with focus outside an input opens the overlay.
        // Close the drawer first so Escape is available.
        // TODO [WARNING]: Scenario 5 depends on Escape-to-close working correctly (same path as
        // Scenario 4). If the global Escape handler is broken, this WaitForFunctionAsync will hang
        // for 5 s before failing, making the root cause ambiguous. Consider closing the drawer via
        // the UI button instead to decouple this scenario from the Escape-close path.
        await Page.Keyboard.PressAsync("Escape");
        await Page.WaitForFunctionAsync(
            "() => !document.querySelector('.dispatch-drawer.open')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Click a non-input area to ensure focus is not in any input
        // TODO [WARNING]: The "?" global shortcut is handled by registerGlobalKeyboardHandler in
        // JavaScript. If that JS function fails to register (e.g. throws on init), the
        // WaitForSelectorAsync below will hang for its full timeout rather than giving a clean
        // assertion failure. Consider adding a short-circuit check for JS handler registration.
        await Page.ClickAsync("h1");

        await Page.Keyboard.PressAsync("?");

        // Overlay should appear
        await Page.WaitForSelectorAsync(".shortcut-overlay-backdrop", new() { Timeout = 5_000 });
        var overlayVisible = await Page.IsVisibleAsync(".shortcut-overlay-backdrop");
        Assert.True(overlayVisible, "Shortcut overlay should open when '?' is pressed outside an input");

        // Verify overlay lists drawer shortcuts
        var overlayText = await Page.TextContentAsync(".shortcut-overlay");
        Assert.NotNull(overlayText);
        // TODO [WARNING]: These content assertions are minimal — any overlay containing "↓", "↑",
        // and "Enter" for unrelated reasons would satisfy them. Consider asserting on a more specific
        // string such as "Drawer Navigation" (the section header in ShortcutHelpOverlay.razor) or
        // a full shortcut description like "Move to next item" to pin the drawer-specific content.
        Assert.Contains("↓", overlayText);
        Assert.Contains("↑", overlayText);
        Assert.Contains("Enter", overlayText);

        // Scenario 5b: Escape closes the overlay
        await Page.Keyboard.PressAsync("Escape");
        await Page.WaitForFunctionAsync(
            "() => !document.querySelector('.shortcut-overlay-backdrop')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Scenario 5c: Second "?" (toggle) also closes the overlay
        await Page.Keyboard.PressAsync("?");
        await Page.WaitForSelectorAsync(".shortcut-overlay-backdrop", new() { Timeout = 5_000 });
        await Page.Keyboard.PressAsync("?");
        await Page.WaitForFunctionAsync(
            "() => !document.querySelector('.shortcut-overlay-backdrop')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Scenario 5d: "?" typed inside the filter input does NOT open the overlay.
        // Reopen the drawer.
        await codingPage.ClickBrowseIssuesAsync();
        await Page.WaitForSelectorAsync(".dispatch-drawer.open", new() { Timeout = 10_000 });

        // Focus the filter input (placeholder = "Filter by number or title...")
        await Page.FocusAsync("input[placeholder='Filter by number or title...']");

        // Type "?" — since focus is on an input, the global keyboard handler skips it
        // TODO [WARNING]: Keyboard.PressAsync("?") sends the key by name; on non-US keyboard
        // locales in CI the "?" character may not be produced in the input, causing the
        // Assert.Contains("?", filterValue) check below to fail even when overlay suppression
        // is correct. Consider using Page.Keyboard.TypeAsync("?") which is layout-independent.
        await Page.Keyboard.PressAsync("?");

        // Overlay must NOT appear
        var overlayCount = await Page.Locator(".shortcut-overlay-backdrop").CountAsync();
        Assert.Equal(0, overlayCount);

        // The filter should contain the literal "?" character typed into the input
        var filterValue = await Page.InputValueAsync("input[placeholder='Filter by number or title...']");
        Assert.Contains("?", filterValue);
    }

    // ── Scenario 6: PR and Epic drawer Enter-dispatch ───────────────────────────

    [Fact]
    public async Task Enter_PrDrawer_DispatchesPrReview()
    {
        // Arrange: a PR review-enabled template and one pull request
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-pr-kbd",
            Name = "PR Keyboard Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            ReviewEnabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // TODO [WARNING]: SaveAgentProfileAsync("profile-e2e") is called here and in SeedDefaultTemplateAsync.
        // If Enter_PrDrawer_DispatchesPrReview and Enter_EpicDrawer_DispatchesDecomposition ever run
        // concurrently (xUnit parallel collections), two concurrent upserts on the same profile ID can
        // race on the config store. Confirm that the E2ECollection disables parallelism, or use
        // profile IDs unique to each test.
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 200,
            Identifier = "200",
            Title = "PR keyboard test",
            Description = "PR keyboard shortcut test",
            BranchName = "feat/keyboard",
            TargetBranch = "main",
            Labels = new[] { "enhancement" },
            Url = "https://github.com/test/repo/pull/200",
            IsDraft = false
        });

        await using var fakeAgent = new FakeAgentClient("kbd-agent-pr", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("PR Keyboard Template");
        await codingPage.ClickBrowsePrsAsync();

        // Wait for the PR row to appear
        await Page.WaitForSelectorAsync("[data-testid='pr-row-200']", new() { Timeout = 10_000 });

        await FocusDrawerListAsync();

        // ArrowDown to highlight PR #200 (index 0)
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('[data-testid=\"pr-row-200\"]')?.classList.contains('drawer-item-highlighted')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Press Enter → dispatches PR review
        await Page.Keyboard.PressAsync("Enter");

        // Wait for success toast
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var toastText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.NotNull(toastText);
        Assert.Contains("200", toastText);

        // Confirm one work item was created
        // TODO [WARNING]: w.IssueIdentifier == "200" assumes DispatchFromPrDrawerAsync stores the
        // work item with IssueIdentifier set to the PR's Identifier field ("200"). If it uses a
        // different field (e.g. PrNumber, BranchName) the assertion will always be false and the
        // test will fail for the wrong reason. Verify against the WorkItem model.
        Assert.True(await CountWorkItemsForIssueAsync("200") > 0,
            "Work item for PR #200 must exist after keyboard dispatch");
    }

    [Fact]
    public async Task Enter_EpicDrawer_DispatchesDecomposition()
    {
        // Arrange: a decomposition-enabled template and one epic issue
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-epic-kbd",
            Name = "Epic Keyboard Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
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
            Identifier = "300",
            Title = "Epic keyboard test",
            Description = "## Goal\nTest keyboard dispatch",
            // TODO [WARNING]: The epic drawer appearance depends on both the "agent:epic" label and
            // the DecompositionEnabled flag. If the fake provider filters by label AND uses a
            // different label than "agent:epic" (e.g. "agent:epic-approved"), issue #300 will not
            // appear. Verify that the fake issue provider and EpicDispatchDrawer agree on the label
            // used to identify epics, and that label-based filtering in the fake is consistent.
            Labels = new[] { "agent:epic" }
        });

        await using var fakeAgent = new FakeAgentClient("kbd-agent-epic", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Epic Keyboard Template");
        await codingPage.ClickBrowseEpicsAsync();

        // Wait for the epic row to appear
        await Page.WaitForSelectorAsync("[data-testid='epic-row-300']", new() { Timeout = 10_000 });

        await FocusDrawerListAsync();

        // ArrowDown to highlight epic #300 (index 0)
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('[data-testid=\"epic-row-300\"]')?.classList.contains('drawer-item-highlighted')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Press Enter → dispatches decomposition
        await Page.Keyboard.PressAsync("Enter");

        // Wait for success toast
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var toastText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.NotNull(toastText);
        Assert.Contains("300", toastText);

        // Confirm one work item was created
        Assert.True(await CountWorkItemsForIssueAsync("300") > 0,
            "Work item for epic #300 must exist after keyboard dispatch");
    }
}
