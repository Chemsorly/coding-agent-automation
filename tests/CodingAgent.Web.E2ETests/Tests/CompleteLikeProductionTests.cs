using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Headless E2E tests verifying that <see cref="FakeAgentClient.CompleteLikeProductionAsync"/>
/// exercises the same two-channel completion path as a real agent pod:
/// HTTP POST <c>/api/work-items/{id}/status</c> (primary) followed by SignalR
/// <c>ReportJobCompleted</c> (secondary).
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "CompleteLikeProduction")]
[Collection(E2ECollection.Name)]
public sealed class CompleteLikeProductionTests : HeadlessE2ETestBase
{
    public CompleteLikeProductionTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Seeds the issue, job template, and agent profile required for dispatch.
    /// Uses the "db-e2e" selector which is registered in E2ETestDefaults.InstallJobTemplates.
    /// </summary>
    private async Task SeedTestDataAsync(string issueId, string issueTitle = "HTTP completion test")
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = issueTitle,
            Description = "## Requirements\nTest HTTP completion path.\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        // TODO: [WARNING] This template is saved but DispatchIssueAsync (called without an explicit
        // templateId) resolves providers via the default fallback path (IssueProviderId="issue-e2e",
        // RepoProviderId="repo-e2e") and does not look up the saved template by ID. The template
        // seeding is therefore dead code — the dispatched run never consults it. Either remove the
        // SaveTemplateAsync call or update DispatchIssueAsync to accept a templateId so the saved
        // template is actually used. Until resolved, readers may incorrectly assume the run uses
        // "template-http-completion-e2e".
        // See review-findings.md [WARNING] TestQualityReviewer — CompleteLikeProductionTests.cs:37.
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-http-completion-e2e",
            Name = "HTTP Completion E2E Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        // AgentProfile with MatchLabels = ["db-e2e"] is required — without it, DispatchIssueAsync
        // finds no matching profile and returns a failed DistributionResult.
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-http-completion-e2e",
            DisplayName = "HTTP Completion E2E Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    /// <summary>
    /// A run completed via <see cref="FakeAgentClient.CompleteLikeProductionAsync"/> with
    /// <see cref="PipelineStep.Completed"/> should:
    /// <list type="bullet">
    ///   <item>Transition the WorkItem to <see cref="WorkItemStatus.Succeeded"/></item>
    ///   <item>Persist a history entry with <see cref="PipelineStep.Completed"/></item>
    ///   <item>Apply the <c>agent:done</c> label to the issue</item>
    /// </list>
    /// The <c>agent:done</c> label is applied by the hub path
    /// (<c>HandleJobCompletedAsync → SwapLabelAndPostCommentAsync</c>), so
    /// <see cref="HeadlessE2ETestBase.WaitForHistoryAsync"/> is used as the timing anchor to avoid
    /// asserting on labels before the SignalR handler has finished.
    /// </summary>
    [Fact]
    public async Task CompleteLikeProduction_Succeeded_WorkItemSucceeded_LabelDone()
    {
        // Arrange
        const string issueId = "3084-succ";
        await SeedTestDataAsync(issueId, "HTTP completion test — Succeeded path");

        await using var agent = new FakeAgentClient("http-agent-succ", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var dispatchResult = await DispatchIssueAsync(issueId);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.ErrorMessage}");
        var workItemId = Guid.Parse(dispatchResult.WorkItemId!);

        // Wait for the job to be assigned and accept it (sets AssignedAgentId on the WorkItem
        // so AuthorizeAgentForWorkItemAsync allows the HTTP POST).
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(issueId, assignment.IssueIdentifier);
        await agent.AcceptJobAsync(assignment.JobId);

        // Act — complete using the production two-channel path (HTTP first, then hub).
        await agent.CompleteLikeProductionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/1",
            RetryCount = 0,
            FilesChangedCount = 3,
            LinesAdded = 50,
            LinesRemoved = 10,
            BrainUpdatesPushed = false,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        });

        // Assert: WorkItem status transitions to Succeeded.
        var workItem = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkItemStatus.Succeeded, workItem.Status);
        Assert.NotNull(workItem.CompletedAt);

        // Assert: history entry exists with FinalStep == Completed.
        // IMPORTANT: WaitForHistoryAsync is the correct timing anchor here because agent:done
        // is applied by the hub path (HandleJobCompletedAsync → SwapLabelAndPostCommentAsync),
        // which runs inline inside the SignalR handler. History is persisted before the label
        // swap, so once history is visible the swap has also completed. Asserting on LabelChanges
        // immediately after WaitForWorkItemStatusAsync would be racy since the DB transitions on
        // the HTTP response, before the hub call has applied the label.
        var history = await WaitForHistoryAsync(
            r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Completed,
            TimeSpan.FromSeconds(10));
        Assert.NotNull(history);
        Assert.Equal(PipelineStep.Completed, history.FinalStep);

        // Assert: issue received the agent:done label (safe to check after WaitForHistoryAsync).
        // TODO: [WARNING] WaitForHistoryAsync as a timing anchor assumes history is persisted
        // before the label swap in HandleJobCompletedAsync. If the ordering is reversed (label swap
        // precedes history write), the label assertion below could run before AddLabelAsync has
        // been called. Verify against HandleJobCompletedAsync's actual implementation to confirm
        // history write precedes label swap; add a targeted poll/wait if the order is reversed.
        // See review-findings.md [WARNING] TestQualityReviewer/Correctness — CompleteLikeProductionTests.cs:105.
        var labelAdds = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == issueId && lc.Added)
            .Select(lc => lc.Label)
            .ToList();
        Assert.True(labelAdds.Contains(AgentLabels.Done),
            $"Expected agent:done label to be added. Actual adds: {string.Join(", ", labelAdds)}");
        // TODO: [WARNING] Also assert that AgentLabels.Error was NOT added for a successful run.
        // Without this, a bug that applies agent:error in addition to agent:done would go undetected.
        // See review-findings.md [WARNING] TestQualityReviewer — CompleteLikeProductionTests.cs:75.
    }

    /// <summary>
    /// A run completed via <see cref="FakeAgentClient.CompleteLikeProductionAsync"/> with
    /// <see cref="PipelineStep.Failed"/> should:
    /// <list type="bullet">
    ///   <item>Transition the WorkItem to <see cref="WorkItemStatus.Failed"/></item>
    ///   <item>Apply the <c>agent:error</c> label to the issue</item>
    /// </list>
    /// The <c>agent:error</c> label is applied synchronously by <c>FailRunWithLabelAsync</c>
    /// inside the HTTP POST handler (before the HTTP response is returned). It exercises
    /// <c>WorkItemStatusTransitionService.ResolveFailedFinalLabel</c>: the payload's
    /// <c>Result</c> JSON is parsed, and because no <c>FinalLabel = agent:needs-refinement</c>
    /// is set, the fallback <c>agent:error</c> is used.
    /// </summary>
    [Fact]
    public async Task CompleteLikeProduction_Failed_WorkItemFailed_LabelError()
    {
        // Arrange
        const string issueId = "3084-fail";
        await SeedTestDataAsync(issueId, "HTTP completion test — Failed path");

        await using var agent = new FakeAgentClient("http-agent-fail", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var dispatchResult = await DispatchIssueAsync(issueId);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.ErrorMessage}");
        var workItemId = Guid.Parse(dispatchResult.WorkItemId!);

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(issueId, assignment.IssueIdentifier);
        await agent.AcceptJobAsync(assignment.JobId);

        // Act — complete with Failed via the production two-channel path.
        // The Result JSON in the HTTP POST body must be parseable by ResolveFailedFinalLabel
        // (it is: CompleteLikeProductionAsync serializes the payload with PipelineJsonOptions.Default).
        // Because no FinalLabel = agent:needs-refinement is set, the fallback agent:error is applied.
        await agent.CompleteLikeProductionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FailureReason = "Intentional test failure",
            FailureCategory = FailureReason.AgentError,
            CompletedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            FilesChangedCount = 0,
            LinesAdded = 0,
            LinesRemoved = 0,
            BrainUpdatesPushed = false,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        });

        // Assert: WorkItem status transitions to Failed.
        var workItem = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkItemStatus.Failed, workItem.Status);
        // TODO: [WARNING] Also assert Assert.NotNull(workItem.CompletedAt) here, matching the
        // Succeeded test. If FailRunWithLabelAsync fails to set CompletedAt for failed runs,
        // the omission here would silently hide that regression.
        // See review-findings.md [WARNING] TestQualityReviewer — CompleteLikeProductionTests.cs:140.

        // Assert: issue received the agent:error label.
        // For the Failed case, agent:error is applied synchronously inside the HTTP POST handler
        // (via FailRunWithLabelAsync), so it is safe to assert immediately after
        // WaitForWorkItemStatusAsync(Failed) without an additional polling anchor.
        // TODO: [WARNING] This assertion cannot distinguish whether agent:error was applied by the
        // HTTP POST handler (via FailRunWithLabelAsync/ResolveFailedFinalLabel) or by the hub path
        // (ReportJobCompleted → HandleJobCompletedAsync). If the HTTP channel silently fails,
        // the hub path would still apply agent:error, making the test pass without the HTTP
        // channel having executed. Now that PostStatusHttpAsync throws on non-2xx responses, this
        // risk is mitigated — but a positive assertion that the label was set *before* the hub
        // call (e.g. checking label state between the two channel calls) would fully close the gap.
        // See review-findings.md [WARNING] TestQualityReviewer — CompleteLikeProductionTests.cs:165.
        var labelAdds = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == issueId && lc.Added)
            .Select(lc => lc.Label)
            .ToList();
        Assert.True(labelAdds.Contains(AgentLabels.Error),
            $"Expected agent:error label to be added. Actual adds: {string.Join(", ", labelAdds)}");
    }
}
