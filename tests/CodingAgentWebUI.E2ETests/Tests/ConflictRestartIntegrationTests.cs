using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Integration tests for the conflict-restart pipeline path (issue #2359).
///
/// These tests verify the end-to-end chain that the unit tests in
/// <c>QualityGateExecutorConflictRestartTests</c> cannot exercise:
/// <list type="bullet">
///   <item><c>JobCompletionMapper</c> copies <c>FinalLabel = agent:next</c> from the payload to the run</item>
///   <item><c>AgentJobLifecycleService.PostCompletionBookkeepingAsync</c> swaps the issue label to <c>agent:next</c></item>
///   <item>History is persisted with <c>FinalStep = ConflictRestart</c></item>
///   <item><c>WorkItem</c> is marked <c>Succeeded</c> (conflict-restart is a clean termination, not a crash)</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "DbMode")]
[Trait("Feature", "ConflictRestart")]
[Collection(E2ECollection.Name)]
public sealed class ConflictRestartIntegrationTests : HeadlessE2ETestBase
{
    public ConflictRestartIntegrationTests(E2EFixture fixture) : base(fixture) { }

    private async Task SeedIssueAndProfileAsync(string issueId, string title = "Conflict restart test issue")
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = title,
            Description = "## Requirements\nDo the thing\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:in-progress" }
        });

        var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
        if (!templates.Any(t => t.Id == "template-conflict-e2e"))
        {
            await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
            {
                Id = "template-conflict-e2e",
                Name = "Conflict Restart Template",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true
            }, CancellationToken.None);
        }

        var profiles = await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None);
        if (!profiles.Any(p => p.Id == "profile-conflict-e2e"))
        {
            await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
            {
                Id = "profile-conflict-e2e",
                DisplayName = "Conflict Restart Agent Profile",
                MatchLabels = new[] { "conflict-e2e" },
                AgentProviderConfigId = "agent-e2e",
                Enabled = true
            }, CancellationToken.None);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C1: Agent reports ConflictRestart → FinalStep=ConflictRestart in history
    //     + issue label swapped to agent:next
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Full end-to-end chain for the conflict-restart path (#2359):
    /// <list type="number">
    ///   <item>Issue dispatched → agent receives job</item>
    ///   <item>Agent reports <c>FinalStep = ConflictRestart</c> with <c>FinalLabel = agent:next</c></item>
    ///   <item>History records <c>FinalStep = ConflictRestart</c>, <c>FailureReason</c> identifies the conflict</item>
    ///   <item><c>WorkItem</c> transitions to <c>Succeeded</c> (clean termination, not a crash)</item>
    ///   <item><c>InMemoryIssueProvider.LabelChanges</c> records the <c>agent:next</c> swap
    ///         (agent:in-progress removed, agent:next added)</item>
    /// </list>
    /// This test validates the orchestration chain that unit tests cannot reach:
    /// <c>JobCompletionMapper</c> → <c>AgentJobLifecycleService.PostCompletionBookkeepingAsync</c>
    /// → <see cref="IIssueProvider"/>.
    /// </summary>
    [Fact]
    public async Task ConflictRestart_FullChain_HistoryRecordsConflictRestartStep_IssueRelabelledAgentNext()
    {
        // Arrange: seed issue with agent:in-progress label (the label the pipeline swaps away from)
        await SeedIssueAndProfileAsync("2359", "PR conflict restart test");
        await using var agent = new FakeAgentClient("conflict-agent-1", "conflict-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: dispatch issue
        var result = await DispatchIssueAsync("2359");
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");
        Assert.NotNull(result.WorkItemId);
        var workItemId = Guid.Parse(result.WorkItemId);

        // Wait for agent to receive the job
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("2359", assignment.IssueIdentifier);

        // Agent completes the job as ConflictRestart:
        // FinalStep = ConflictRestart, FinalLabel = agent:next, RetryCount unchanged (0)
        await agent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.ConflictRestart,
            FinalLabel = AgentLabels.Next,
            FailureReason = "PR conflicted with main — restarting pipeline",
            CompletedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,          // ConflictRestart must NOT increment RetryCount
            IsDraftPr = false,       // No draft PR created for ConflictRestart
            PullRequestUrl = null,   // No PR URL — the PR already exists but is conflicted
            FilesChangedCount = 5,
            LinesAdded = 80,
            LinesRemoved = 20,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0
        });

        // Assert 1: WorkItem transitions to Succeeded
        // ConflictRestart is a clean, intentional termination — not a crash.
        // AgentJobLifecycleService uses FinalStep.IsTerminal() to decide the WorkItem outcome;
        // PipelineStep.ConflictRestart.IsTerminal() returns true.
        var workItem = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkItemStatus.Succeeded, workItem.Status);
        Assert.NotNull(workItem.CompletedAt);

        // Assert 2: History persists FinalStep = ConflictRestart and FailureReason
        var history = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "2359" && r.FinalStep == PipelineStep.ConflictRestart,
            TimeSpan.FromSeconds(10));
        Assert.NotNull(history);
        Assert.Equal(PipelineStep.ConflictRestart, history.FinalStep);
        Assert.Equal(0, history.RetryCount);
        // PipelineRunSummary does not have IsDraftPr — the absence of a PullRequestUrl combined
        // with FinalStep = ConflictRestart is the observable signal that no draft PR was created.
        Assert.Null(history.PullRequestUrl);
        Assert.Contains("conflict", history.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);

        // Assert 3: PostCompletionBookkeepingAsync swapped the issue label to agent:next.
        // FinalLabel = agent:next in the payload takes precedence over the FinalStep-derived label
        // (which for ConflictRestart would default to agent:error). This is the critical re-queue
        // mechanic: the closed-loop poll will pick up agent:next and re-dispatch the run into
        // RunMode.Rework, where it rebases before re-entering CI.
        //
        // InMemoryIssueProvider records each label add/remove as (issueIdentifier, label, wasAdded).
        var labelAdds = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "2359" && lc.Added)
            .Select(lc => lc.Label)
            .ToList();
        Assert.True(labelAdds.Contains(AgentLabels.Next),
            $"agent:next must have been added to issue #2359. Actual label changes: " +
            $"{string.Join(", ", Fixture.IssueProvider.LabelChanges.Select(lc => $"{(lc.Added ? "+" : "-")}{lc.Label}"))}");

        // agent:error must NOT have been added — ConflictRestart is not an error condition
        Assert.False(labelAdds.Contains(AgentLabels.Error),
            "agent:error must NOT be applied for ConflictRestart (it is not an error condition)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C2: Agent reports ConflictRestart → second dispatch re-queues as agent:next
    //     The second dispatch simulates what closed-loop mode does after the label swap.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that after a ConflictRestart termination, the issue can be re-dispatched
    /// as <c>agent:next</c>, and the second job runs independently of the first.
    /// This validates the re-queue → second dispatch segment of the closed-loop cycle.
    /// </summary>
    [Fact]
    public async Task ConflictRestart_IssueReDispatchedAsAgentNext_SecondJobCompletesSuccessfully()
    {
        // Arrange
        await SeedIssueAndProfileAsync("2360", "PR conflict restart — second dispatch test");
        await using var agent = new FakeAgentClient("conflict-agent-2", "conflict-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // First dispatch
        var firstResult = await DispatchIssueAsync("2360");
        Assert.True(firstResult.Success, $"First dispatch failed: {firstResult.ErrorMessage}");

        var firstAssignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("2360", firstAssignment.IssueIdentifier);

        // First run terminates as ConflictRestart
        await agent.AcceptAndCompleteJobWithPayloadAsync(firstAssignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.ConflictRestart,
            FinalLabel = AgentLabels.Next,
            FailureReason = "PR conflicted with main — restarting pipeline",
            CompletedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            IsDraftPr = false,
            FilesChangedCount = 3,
            LinesAdded = 40,
            LinesRemoved = 10,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        });

        // Wait for first run to appear in history
        await WaitForHistoryAsync(
            r => r.IssueIdentifier == "2360" && r.FinalStep == PipelineStep.ConflictRestart,
            TimeSpan.FromSeconds(10));

        // Simulate the closed-loop re-dispatch after the agent:next label swap:
        // reset the agent's JobAssigned TCS so we can wait for the second assignment
        agent.ResetJobAssigned();

        // Second dispatch (as if closed-loop poll found agent:next on the issue)
        var secondResult = await DispatchIssueAsync("2360");
        Assert.True(secondResult.Success, $"Second dispatch failed: {secondResult.ErrorMessage}");
        Assert.NotEqual(firstResult.WorkItemId, secondResult.WorkItemId);

        var secondAssignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("2360", secondAssignment.IssueIdentifier);

        // Second run completes successfully (after rebase resolves the conflict)
        await agent.AcceptAndCompleteJobAsync(secondAssignment.JobId);

        // Assert: second run completed successfully
        var completedHistory = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "2360" && r.FinalStep == PipelineStep.Completed,
            TimeSpan.FromSeconds(15));
        Assert.NotNull(completedHistory);
        Assert.Equal(PipelineStep.Completed, completedHistory.FinalStep);

        // Assert: both runs appear in history
        var allRuns = (await Fixture.HistoryService.GetRunHistoryAsync())
            .Where(r => r.IssueIdentifier == "2360")
            .ToList();
        Assert.True(allRuns.Count >= 2,
            $"Expected at least 2 runs for issue #2360, got {allRuns.Count}");
        Assert.Contains(allRuns, r => r.FinalStep == PipelineStep.ConflictRestart);
        Assert.Contains(allRuns, r => r.FinalStep == PipelineStep.Completed);
    }
}
