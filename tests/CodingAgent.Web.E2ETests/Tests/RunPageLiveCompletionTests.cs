using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Web.Services;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests that prove the Run page flips to its terminal state through the live
/// <c>OnCompleted</c> hub event (RunPage.razor:860) without a page navigation.
///
/// <para>
/// Existing tests in <see cref="RunDetailCancelAndRedispatchTests"/> re-navigate to
/// /runs/{id} before asserting the terminal state, so a broken hub subscription would
/// still pass. These tests use a <c>window.__noReload</c> JavaScript sentinel: the marker
/// is set before the terminal event arrives and checked after the DOM has flipped to
/// the terminal state. If the page were reloaded at any point between setting the marker
/// and checking it, the flag would be gone and the assertion would fail.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RunPageLiveCompletionTests : E2ETestBase
{
    public RunPageLiveCompletionTests(E2EFixture fixture) : base(fixture) { }

    private static readonly string[] s_e2eMatchLabels = ["e2e"];
    private static readonly string[] s_enhancementLabels = ["enhancement"];

    // ── Seed helpers (copied verbatim from RunDetailCancelAndRedispatchTests) ──

    /// <summary>
    /// Seeds the standard template, agent profile, and issue used by tests in this class.
    /// Verbatim copy of <c>RunDetailCancelAndRedispatchTests.SeedDefaultsAsync</c>.
    /// </summary>
    private async Task SeedDefaultsAsync(string issueId, string templateName = "Run Page Template")
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "run-page-template",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "run-page-profile",
            DisplayName = "Run Page E2E Profile",
            JobTemplate = E2ERouting.Key(s_e2eMatchLabels),
            Enabled = true
        });

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Issue {issueId} run page test",
            Description = "Test",
            Labels = s_enhancementLabels
        });
    }

    /// <summary>
    /// Dispatches an issue via the UI and activates the agent job at the given step.
    /// Returns (runId, assignment) once the server reflects the active step.
    /// Verbatim copy of <c>RunDetailCancelAndRedispatchTests.DispatchAndActivateAsync</c>.
    /// </summary>
    private async Task<(string RunId, JobAssignmentMessage Assignment)> DispatchAndActivateAsync(
        FakeAgentClient agent,
        string issueId,
        string templateName = "Run Page Template",
        PipelineStep step = PipelineStep.GeneratingCode)
    {
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync(templateName);
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, step);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() =>
            runService.GetActiveRuns().Any(r => r.IssueIdentifier == issueId && r.CurrentStep == step));

        var runId = runService.GetActiveRuns().First(r => r.IssueIdentifier == issueId).RunId;
        return (runId, assignment);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the agent completes the job successfully, the Run page flips to the "Completed"
    /// terminal state via the live <c>OnCompleted</c> hub event — without a page navigation.
    /// The <c>window.__noReload</c> sentinel proves no reload occurred.
    ///
    /// Mutation check: removing <c>await ReloadCompletedRunAsync();</c> from <c>OnCompleted</c>
    /// in RunPage.razor makes this test fail (the badge stays "Running" because _run is stale).
    /// </summary>
    [Fact]
    public async Task RunCompletes_PageFlipsToCompleted_WithoutReload()
    {
        await SeedDefaultsAsync("9511");

        await using var agent = new FakeAgentClient("run-completion-agent-1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (runId, assignment) = await DispatchAndActivateAsync(agent, "9511");

        // Navigate to the run page and wait for hub subscription
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        // Pre-conditions: live panel and cancel button are visible
        Assert.True(await runPage.HasLiveOutputPanelAsync(),
            "Live output panel must be visible before completion");
        Assert.True(await runPage.IsCancelButtonVisibleAsync(),
            "Cancel button must be visible before completion");

        // Set the no-reload sentinel BEFORE triggering the hub event
        await Page.EvaluateAsync("() => { window.__noReload = true; }");

        // Act: complete the job — AcceptAndCompleteJobAsync sends JobAccepted a second time;
        // the hub tolerates the duplicate (documented in RunDetailCancelAndRedispatchTests.cs:111-118)
        await agent.AcceptAndCompleteJobAsync(assignment.JobId);

        // Assert: the step badge flips to "Completed" via the hub event (not a reload)
        await Assertions.Expect(Page.Locator(".step-badge"))
            .ToHaveTextAsync(RunOutcomeDisplay.Label(PipelineStep.Completed), new() { Timeout = 15_000 });

        // Assert: live-only elements have disappeared
        await Assertions.Expect(Page.Locator("[data-testid='cancel-pipeline-btn']"))
            .ToHaveCountAsync(0, new() { Timeout = 5_000 });
        await Assertions.Expect(Page.Locator(".cockpit-card:has(h2:has-text('Live output'))"))
            .ToHaveCountAsync(0, new() { Timeout = 5_000 });

        // Assert: no page reload happened (sentinel is still set)
        var markerStillSet = await Page.EvaluateAsync<bool>("() => window.__noReload === true");
        Assert.True(markerStillSet, "window.__noReload must still be true — a page reload would have cleared it");
    }

    /// <summary>
    /// When the agent fails the job, the Run page flips to the "Failed" terminal state and shows
    /// the diagnosis card and re-dispatch button — without a page navigation.
    /// </summary>
    [Fact]
    public async Task RunFails_PageShowsDiagnosisAndRedispatch_WithoutReload()
    {
        await SeedDefaultsAsync("9512");

        await using var agent = new FakeAgentClient("run-completion-agent-2", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (runId, assignment) = await DispatchAndActivateAsync(agent, "9512");

        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        Assert.True(await runPage.HasLiveOutputPanelAsync(),
            "Live output panel must be visible before failure");

        // Set the no-reload sentinel BEFORE triggering the hub event
        await Page.EvaluateAsync("() => { window.__noReload = true; }");

        // Act: fail the job
        await agent.AcceptAndCompleteJobAsync(assignment.JobId, PipelineStep.Failed);

        // Assert: the step badge flips to "Failed"
        await Assertions.Expect(Page.Locator(".step-badge"))
            .ToHaveTextAsync(RunOutcomeDisplay.Label(PipelineStep.Failed), new() { Timeout = 15_000 });

        // Assert: diagnosis card and re-dispatch button are visible
        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-card']"))
            .ToBeVisibleAsync(new() { Timeout = 5_000 });
        await Assertions.Expect(Page.Locator("[data-testid='redispatch-btn']"))
            .ToBeVisibleAsync(new() { Timeout = 5_000 });

        // Assert: no page reload happened
        var markerStillSet = await Page.EvaluateAsync<bool>("() => window.__noReload === true");
        Assert.True(markerStillSet, "window.__noReload must still be true — a page reload would have cleared it");
    }

    /// <summary>
    /// When the user cancels the run from the Run page, the page flips to the "Cancelled"
    /// terminal state via the live <c>OnRunCompleted</c> hub event — without a page navigation.
    ///
    /// The comment at RunPage.razor:931-932 states: "The live OnRunCompleted event will flip the
    /// view to its terminal state." The existing cancel test in RunDetailCancelAndRedispatchTests
    /// re-navigates first, so it does not prove this live flip.
    /// </summary>
    [Fact]
    public async Task RunCancelledOnThePage_FlipsToCancelled_WithoutReload()
    {
        await SeedDefaultsAsync("9513");

        await using var agent = new FakeAgentClient("run-completion-agent-3", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (runId, _) = await DispatchAndActivateAsync(agent, "9513");

        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        Assert.True(await runPage.HasLiveOutputPanelAsync(),
            "Live output panel must be visible before cancel");
        Assert.True(await runPage.IsCancelButtonVisibleAsync(),
            "Cancel button must be visible before cancel");

        // Set the no-reload sentinel BEFORE triggering the cancel (which drives the hub event)
        await Page.EvaluateAsync("() => { window.__noReload = true; }");

        // Act: cancel from the Run page UI (two-step confirm flow)
        await runPage.CancelAsync(confirm: true);

        // Assert: the page flips to the Cancelled terminal state via the hub event
        await runPage.WaitForCancelledStateAsync(TimeSpan.FromSeconds(15));

        // Assert: no page reload happened
        var markerStillSet = await Page.EvaluateAsync<bool>("() => window.__noReload === true");
        Assert.True(markerStillSet, "window.__noReload must still be true — a page reload would have cleared it");
    }
}
