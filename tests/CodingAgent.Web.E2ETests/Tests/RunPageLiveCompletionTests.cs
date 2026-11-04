using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests that the Run page flips to its terminal state through the live hub event
/// (<c>OnCompleted</c> in RunPage.razor), not through a reload.
///
/// <para>
/// Each test opens a live run, marks the document with <c>window.__noReload</c>, finishes the run
/// and then asserts the terminal state with no navigation in between. A reload or a re-navigation
/// would discard the marker, so a marker that is still set proves the flip came from the hub
/// subscription. The existing cancel tests re-navigate before they assert and cannot prove that.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RunPageLiveCompletionTests : E2ETestBase
{
    private static readonly string[] s_e2eMatchLabels = ["e2e"];
    private static readonly string[] s_enhancementLabels = ["enhancement"];

    private const string SetMarkerScript = "() => { window.__noReload = true; }";
    private const string CheckMarkerScript = "() => window.__noReload === true";

    public RunPageLiveCompletionTests(E2EFixture fixture) : base(fixture) { }

    // ── Seed helpers ──────────────────────────────────────────────────────

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

    /// <summary>
    /// Dispatches the issue, opens its live Run page, checks the live panel and the cancel button
    /// are shown and marks the document. Navigation happens before the marker, never after it.
    /// </summary>
    private async Task<(RunDetailPage RunPage, JobAssignmentMessage Assignment)> OpenMarkedLiveRunAsync(
        FakeAgentClient agent, string issueId)
    {
        await SeedDefaultsAsync(issueId);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (runId, assignment) = await DispatchAndActivateAsync(agent, issueId);

        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        Assert.True(await runPage.HasLiveOutputPanelAsync(), "Live output card should be shown for an active run");
        await Assertions.Expect(runPage.CancelButton).ToBeVisibleAsync();

        await Page.EvaluateAsync(SetMarkerScript);
        return (runPage, assignment);
    }

    private async Task AssertNotReloadedAsync()
    {
        Assert.True(await Page.EvaluateAsync<bool>(CheckMarkerScript),
            "The page was reloaded: the terminal state must come from the live hub event, not from a navigation");
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunCompletes_PageFlipsToCompleted_WithoutReload()
    {
        await using var agent = new FakeAgentClient("live-complete-agent-1", "e2e");
        var (runPage, assignment) = await OpenMarkedLiveRunAsync(agent, "9511");

        await agent.AcceptAndCompleteJobAsync(assignment.JobId);

        await Assertions.Expect(Page.Locator(".step-badge"))
            .ToHaveTextAsync(RunOutcomeDisplay.Label(PipelineStep.Completed), new() { Timeout = 15_000 });
        await Assertions.Expect(runPage.CancelButton).ToHaveCountAsync(0);
        await Assertions.Expect(Page.Locator(".cockpit-card:has(h2:has-text('Live output'))")).ToHaveCountAsync(0);
        await AssertNotReloadedAsync();
    }

    [Fact]
    public async Task RunFails_PageShowsDiagnosisAndRedispatch_WithoutReload()
    {
        await using var agent = new FakeAgentClient("live-complete-agent-2", "e2e");
        var (runPage, assignment) = await OpenMarkedLiveRunAsync(agent, "9512");

        await agent.AcceptAndCompleteJobAsync(assignment.JobId, PipelineStep.Failed);

        await Assertions.Expect(Page.Locator(".step-badge"))
            .ToHaveTextAsync(RunOutcomeDisplay.Label(PipelineStep.Failed), new() { Timeout = 15_000 });
        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-card']")).ToBeVisibleAsync();
        await Assertions.Expect(runPage.RedispatchButton).ToBeVisibleAsync();
        await AssertNotReloadedAsync();
    }
}
