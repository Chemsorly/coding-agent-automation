using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Web.Services;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests covering gaps in the Runs page (filter empty state, prev-page button, sort by Result
/// and Type), and Consolidation page states (running run disables button, read-only brain template,
/// refactoring modal project overrides, focus and auto-dispatch).
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RunsAndConsolidationDetailTests : E2ETestBase
{
    public RunsAndConsolidationDetailTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NewRunId() => Guid.NewGuid().ToString();

    /// <summary>
    /// Seeds a terminal <see cref="PipelineRunSummary"/> directly into the in-memory history service.
    /// CompletedAtOffset = started + 1 minute so the run satisfies IsTerminal() checks.
    /// </summary>
    private async Task SeedRunAsync(string issue, PipelineStep step, PipelineRunType type, DateTimeOffset started)
    {
        var started1 = started;
        var completed = started + TimeSpan.FromMinutes(1);
#pragma warning disable CS0618
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = NewRunId(),
            IssueIdentifier = new IssueIdentifier(issue),
            IssueTitle = $"Issue {issue}",
            FinalStep = step,
            RunType = type,
            StartedAtOffset = started1,
            StartedAt = started1.DateTime,
            CompletedAtOffset = completed,
            CompletedAt = completed.DateTime,
        });
#pragma warning restore CS0618
    }

    // ── Runs: filter empty state ─────────────────────────────────────────────

    [Fact]
    public async Task ClientFilterMatchesNothing_ShowsClear_AndClearRestores()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedRunAsync("9601", PipelineStep.Completed, PipelineRunType.Implementation, now.AddMinutes(-2));
        await SeedRunAsync("9602", PipelineStep.Completed, PipelineRunType.Implementation, now.AddMinutes(-3));

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        // Apply a type filter that matches no seeded run
        await runsPage.SelectTypeFilterAsync("Review");

        Assert.True(await runsPage.IsEmptyFilterStateVisibleAsync(),
            "Empty filter state should be visible when no runs match the type filter");

        // Clear filters via the button
        await runsPage.ClearClientFiltersViaButtonAsync();

        Assert.True(await runsPage.IsRunVisibleAsync("9601"), "Run 9601 must be visible after clearing filters");
        Assert.True(await runsPage.IsRunVisibleAsync("9602"), "Run 9602 must be visible after clearing filters");
        Assert.False(await runsPage.IsEmptyFilterStateVisibleAsync(),
            "Empty filter state should not be visible after clearing filters");
    }

    // ── Runs: prev-page button ────────────────────────────────────────────────

    [Fact]
    public async Task PrevPage_ReturnsToTheFirstPage()
    {
        // Seed all 26 before navigating (issue test setup pitfall).
        // Issue 9726 gets the newest StartedAtOffset and appears on page 1.
        // Issue 9701 gets the oldest and is the single entry on page 2.
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 26; i++)
        {
            var started = now - (26 - i) * TimeSpan.FromMinutes(1);
            await SeedRunAsync($"9{700 + i}", PipelineStep.Completed, PipelineRunType.Implementation, started);
        }

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        // Page 1
        Assert.True(await runsPage.IsPrevDisabledAsync(), "Prev must be disabled on page 1");

        await runsPage.GoToNextPageAsync();

        var pagerText = await runsPage.GetPagerInfoTextAsync();
        Assert.Contains("Page 2", pagerText);

        var page2Ids = await runsPage.GetVisibleIssueIdentifiersAsync();
        Assert.Single(page2Ids);

        await runsPage.GoToPrevPageAsync();

        pagerText = await runsPage.GetPagerInfoTextAsync();
        Assert.Contains("Page 1", pagerText);

        var page1Ids = await runsPage.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(25, page1Ids.Count);
        Assert.Contains("9726", page1Ids);
        Assert.True(await runsPage.IsPrevDisabledAsync(), "Prev must be disabled on page 1 after returning");
    }

    // ── Runs: sort by Result ──────────────────────────────────────────────────

    [Fact]
    public async Task ResultSort_OrdersByLabel_AndPersistsInTheUrl()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedRunAsync("9611", PipelineStep.Completed, PipelineRunType.Implementation, now.AddMinutes(-3));
        await SeedRunAsync("9612", PipelineStep.Failed,    PipelineRunType.Implementation, now.AddMinutes(-2));
        await SeedRunAsync("9613", PipelineStep.Cancelled, PipelineRunType.Implementation, now.AddMinutes(-1));

        // Compute expected ascending order: RunOutcomeDisplay.Label ordinal
        // "Cancelled" < "Completed" < "Failed"
        var expected = new[]
        {
            (id: "9613", step: PipelineStep.Cancelled),
            (id: "9611", step: PipelineStep.Completed),
            (id: "9612", step: PipelineStep.Failed),
        }
        .OrderBy(x => RunOutcomeDisplay.Label(x.step), StringComparer.Ordinal)
        .Select(x => x.id)
        .ToList();

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        await runsPage.ClickSortHeaderAsync("Result");

        // Poll until the first sorted row has settled — ClickSortHeaderAsync uses a fixed
        // 500ms sleep that can be insufficient under CI load (Blazor Server re-render via SignalR)
        await WaitUntilAsync(async () =>
        {
            var current = await runsPage.GetVisibleIssueIdentifiersAsync();
            return current.Where(id => id.StartsWith("961")).FirstOrDefault() == expected[0];
        });
        var ids = await runsPage.GetVisibleIssueIdentifiersAsync();
        var filtered = ids.Where(id => id.StartsWith("961")).ToList();
        Assert.Equal(expected, filtered);

        Assert.Contains("sort=result", Page.Url);
        Assert.Contains("sortDir=asc", Page.Url);

        // Second click reverses the sort
        var reversedExpected = ((IEnumerable<string>)expected).Reverse().ToList();
        await runsPage.ClickSortHeaderAsync("Result");
        await WaitUntilAsync(async () =>
        {
            var current = await runsPage.GetVisibleIssueIdentifiersAsync();
            return current.Where(id => id.StartsWith("961")).FirstOrDefault() == reversedExpected[0];
        });
        var idsReversed = await runsPage.GetVisibleIssueIdentifiersAsync();
        var filteredReversed = idsReversed.Where(id => id.StartsWith("961")).ToList();
        Assert.Equal(reversedExpected, filteredReversed);

        // Reload: reversed order persists; poll until the sort reconstructed from URL query
        // params has settled in Blazor Server
        await Page.ReloadCockpitPageAsync();
        await WaitUntilAsync(async () =>
        {
            var current = await runsPage.GetVisibleIssueIdentifiersAsync();
            return current.Where(id => id.StartsWith("961")).FirstOrDefault() == reversedExpected[0];
        });
        var idsAfterReload = await runsPage.GetVisibleIssueIdentifiersAsync();
        var filteredAfterReload = idsAfterReload.Where(id => id.StartsWith("961")).ToList();
        Assert.Equal(reversedExpected, filteredAfterReload);
    }

    // ── Runs: sort by Type ────────────────────────────────────────────────────

    [Fact]
    public async Task TypeSort_OrdersByType()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedRunAsync("9621", PipelineStep.Completed, PipelineRunType.Review,          now.AddMinutes(-2));
        await SeedRunAsync("9622", PipelineStep.Completed, PipelineRunType.Implementation,  now.AddMinutes(-1));

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        // First click: ascending — "Implementation" < "Review"
        await runsPage.ClickSortHeaderAsync("Type");
        var ids1 = await runsPage.GetVisibleIssueIdentifiersAsync();
        var filtered1 = ids1.Where(id => id is "9621" or "9622").ToList();
        Assert.Equal(new[] { "9622", "9621" }, filtered1);

        // Second click: descending — "Review" < "Implementation"
        await runsPage.ClickSortHeaderAsync("Type");
        var ids2 = await runsPage.GetVisibleIssueIdentifiersAsync();
        var filtered2 = ids2.Where(id => id is "9621" or "9622").ToList();
        Assert.Equal(new[] { "9621", "9622" }, filtered2);
    }

    // ── Consolidation: running brain consolidation disables the button ─────────

    [Fact]
    public async Task RunningBrainConsolidation_DisablesItsButton()
    {
        const string templateName = "Disable-Brain-Template";

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-disable-brain",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-disable-brain",
            DisplayName = "Disable Brain Profile",
            JobTemplate = E2ERouting.Key([]),
            Enabled = true
        });

        await using var agent = new FakeAgentClient("agent-disable-brain");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var consolPage = new ConsolidationPage(Page, BaseUrl);
        await consolPage.NavigateAsync();

        // Start a brain consolidation run from the UI (not from a seeded summary)
        await consolPage.ClickBrainConsolidationAsync(templateName);

        // Wait for FakeJobController to dispatch and the agent to receive the job
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(25));
        var jobId = assignment.JobId;

        // Accept the job so the run is active
        await agent.AcceptJobAsync(jobId);

        // Poll until the run appears in active runs
        await WaitUntilAsync(() =>
            Fixture.RunService.GetActiveRuns().Any(r => r.RunId == jobId));

        // Navigate again — the button should now be disabled because the run has no CompletedAtOffset
        await consolPage.NavigateAsync();
        Assert.True(await consolPage.IsBrainButtonDisabledAsync(templateName),
            "Brain Consolidation button must be disabled while a consolidation run is active");

        // Finish the run
        await agent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
        {
            JobId = jobId,
            Success = true,
            Summary = "Brain consolidated"
        });

        var workItemGuid = Guid.Parse(jobId);
        await WaitUntilAsync(async () =>
        {
            var status = await Fixture.WorkItems.GetStatusAsync(workItemGuid);
            return status == WorkItemStatus.Succeeded || status == WorkItemStatus.Failed;
        });
    }

    // ── Consolidation: read-only brain template disables the button ────────────

    [Fact]
    public async Task ReadOnlyBrainTemplate_DisablesTheButtonAndSaysWhy()
    {
        const string templateName = "ReadOnly-Brain-Template";
        const string expectedTitle =
            "The brain is read-only for this template. Trigger brain consolidation from a template that writes to this brain.";

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-readonly-brain",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            BrainReadOnly = true,
            Enabled = true
        }, CancellationToken.None);

        var consolPage = new ConsolidationPage(Page, BaseUrl);
        await consolPage.NavigateAsync();

        Assert.True(await consolPage.IsBrainButtonDisabledAsync(templateName),
            "Brain Consolidation button must be disabled for a read-only brain template");

        // Assert the button title tooltip
        var button = Page.Locator(
            $".consolidation-card:has(.consolidation-card-title:has-text('{templateName}')) button:has-text('Brain Consolidation')");
        var title = await button.GetAttributeAsync("title");
        Assert.Equal(expectedTitle, title);

        // Assert the card body contains the read-only indicator text
        var card = Page.Locator($".consolidation-card:has(.consolidation-card-title:has-text('{templateName}'))");
        await Assertions.Expect(card).ToContainTextAsync("read-only for this template");
    }

    // ── Consolidation: refactoring modal shows project overrides ───────────────

    [Fact]
    public async Task RefactoringModal_ShowsProjectOverrides()
    {
        const string projectId = "u08project-0000-0000-0000-000000000001";
        const string templateName = "U08-Override-Template";

        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = projectId,
            Name = "U08 Project",
            MaxRefactoringProposals = 7,
            RefactoringReviewEnabled = false,
            Enabled = true,
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(projectId, new PipelineJobTemplate
        {
            Id = "template-u08-override",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        var consolPage = new ConsolidationPage(Page, BaseUrl);
        await consolPage.NavigateAsync();

        await consolPage.ClickRefactoringScanAsync(templateName);
        Assert.True(await consolPage.WaitForRefactoringModalAsync(),
            "Refactoring modal should appear after clicking Refactoring Scan");

        // Max issues: shows 7 and project override chip
        var maxIssuesRow = Page.Locator(".refactoring-modal-param:has-text('Max issues:')");
        await Assertions.Expect(maxIssuesRow).ToContainTextAsync("7");
        await Assertions.Expect(maxIssuesRow).ToContainTextAsync("project override");

        // Adversarial review: shows Disabled and project override chip
        var adversarialRow = Page.Locator(".refactoring-modal-param:has-text('Adversarial review:')");
        await Assertions.Expect(adversarialRow).ToContainTextAsync("Disabled");
        await Assertions.Expect(adversarialRow).ToContainTextAsync("project override");

        // Hotspot lookback: shows days (no override — default value)
        var hotspotRow = Page.Locator(".refactoring-modal-param:has-text('Hotspot lookback:')");
        await Assertions.Expect(hotspotRow).ToContainTextAsync("days");
    }

    // ── Consolidation: refactoring modal no override hides the chip ────────────

    [Fact]
    public async Task RefactoringModal_NoOverride_HidesTheChip()
    {
        const string templateName = "NoOverride-Template";

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-no-override",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        var consolPage = new ConsolidationPage(Page, BaseUrl);
        await consolPage.NavigateAsync();

        await consolPage.ClickRefactoringScanAsync(templateName);
        Assert.True(await consolPage.WaitForRefactoringModalAsync(),
            "Refactoring modal should appear after clicking Refactoring Scan");

        Assert.Equal(0, await Page.Locator(".refactoring-modal-param-override").CountAsync());
    }

    // ── Consolidation: refactoring modal sends focus and auto-dispatch ─────────

    [Fact]
    public async Task RefactoringModal_SendsFocusAndAutoDispatch()
    {
        const string templateName = "Focus-AutoDispatch-Template";

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-focus-autodispatch",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-focus-autodispatch",
            DisplayName = "Focus AutoDispatch Profile",
            JobTemplate = E2ERouting.Key([]),
            Enabled = true
        });

        await using var agent = new FakeAgentClient("agent-focus-autodispatch");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var consolPage = new ConsolidationPage(Page, BaseUrl);
        await consolPage.NavigateAsync();

        // Open the refactoring scan modal
        await consolPage.ClickRefactoringScanAsync(templateName);
        Assert.True(await consolPage.WaitForRefactoringModalAsync());

        // Select Correctness focus
        await Page.SelectOptionAsync("[data-testid='refactoring-focus']", "2"); // RefactoringScanFocus.Correctness = 2

        // Check the agent:next checkbox
        await Page.CheckAsync(".modal-card input[type='checkbox']");

        // Confirm
        await consolPage.ConfirmRefactoringModalAsync();

        // Wait for FakeJobController to dispatch and agent to receive the assignment
        // FakeAgentClient.JobAssigned returns JobAssignmentMessage
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(25));
        var jobId = assignment.JobId;

        Assert.Equal(RefactoringScanFocus.Correctness, assignment.RefactoringFocus);
        Assert.True(assignment.AutoDispatch);

        // Finish the job
        await agent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
        {
            JobId = jobId,
            Success = true,
            CreatedIssues = []
        });

        var workItemGuid = Guid.Parse(jobId);
        await WaitUntilAsync(async () =>
        {
            var status = await Fixture.WorkItems.GetStatusAsync(workItemGuid);
            return status == WorkItemStatus.Succeeded || status == WorkItemStatus.Failed;
        });
    }
}
