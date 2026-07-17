using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for LocalPipelineExecutor error and cancellation paths.
/// Covers BuildFailurePayload, BuildCompletionPayload, and BuildStepMetadata
/// to ensure error state is correctly propagated in completion messages.
/// These internal static methods are the boundary where run state becomes wire payload —
/// errors here mean the orchestrator receives wrong status, causing ghost runs.
/// </summary>
public class LocalPipelineExecutorErrorPathTests
{
    // ── BuildFailurePayload ─────────────────────────────────────────────

    [Fact]
    public void BuildFailurePayload_SetsFailedStep_AndReason()
    {
        var run = CreateRunAtStep(PipelineStep.GeneratingCode);

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Agent process crashed with exit code 1");

        payload.FinalStep.Should().Be(PipelineStep.Failed);
        payload.FailureReason.Should().Be("Agent process crashed with exit code 1");
    }

    [Fact]
    public void BuildFailurePayload_PreservesRetryCount()
    {
        var run = CreateRunAtStep(PipelineStep.RunningQualityGates);
        run.RetryCount = 2;

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Quality gate timeout");

        payload.RetryCount.Should().Be(2);
    }

    [Fact]
    public void BuildFailurePayload_PreservesFileChangeStats()
    {
        var run = CreateRunAtStep(PipelineStep.CreatingPullRequest);
        run.FilesChangedCount = 12;
        run.LinesAdded = 350;
        run.LinesRemoved = 80;

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "PR creation failed");

        payload.FilesChangedCount.Should().Be(12);
        payload.LinesAdded.Should().Be(350);
        payload.LinesRemoved.Should().Be(80);
    }

    [Fact]
    public void BuildFailurePayload_PreservesIsRework_WhenLinkedPrExists()
    {
        var run = CreateRunAtStep(PipelineStep.GeneratingCode);
        run.LinkedPullRequest = new LinkedPullRequest { Number = 42, BranchName = "fix/thing", Url = "http://pr/42", IsDraft = false };

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "crash");

        payload.IsRework.Should().BeTrue();
    }

    [Fact]
    public void BuildFailurePayload_IsReworkFalse_WhenNoLinkedPr()
    {
        var run = CreateRunAtStep(PipelineStep.GeneratingCode);

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "crash");

        payload.IsRework.Should().BeFalse();
    }

    [Fact]
    public void BuildFailurePayload_PreservesTokenAndCostMetrics()
    {
        var run = CreateRunAtStep(PipelineStep.GeneratingCode);
        run.TotalTokens = 85000;
        run.TotalCost = 1.75m;

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "timeout");

        payload.TotalTokens.Should().Be(85000);
        payload.TotalCost.Should().Be(1.75m);
    }

    [Fact]
    public void BuildFailurePayload_PreservesFeedback()
    {
        var run = CreateRunAtStep(PipelineStep.GeneratingCode);
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Failure,
            CollectedAtUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            Harness = new HarnessFeedback { Category = "missing context" }
        };

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "agent stuck");

        payload.Feedback.Should().NotBeNull();
        payload.Feedback!.Outcome.Should().Be(FeedbackOutcome.Failure);
        payload.Feedback.Harness.Category.Should().Be("missing context");
    }

    [Fact]
    public void BuildFailurePayload_SetsCompletedAt_ToCurrentTime()
    {
        var before = DateTimeOffset.UtcNow;
        var run = CreateRunAtStep(PipelineStep.AnalyzingCode);

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "error");

        payload.CompletedAt.Should().BeOnOrAfter(before);
        payload.CompletedAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    // ── BuildCompletionPayload ──────────────────────────────────────────

    [Fact]
    public void BuildCompletionPayload_UsesFinalStep_FromRun()
    {
        var run = CreateRunAtStep(PipelineStep.Completed);
        run.MarkCompleted();

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public void BuildCompletionPayload_PreservesPullRequestInfo()
    {
        var run = CreateRunAtStep(PipelineStep.Completed);
        run.PullRequestUrl = "https://github.com/org/repo/pull/99";
        run.PullRequestNumber = "99";
        run.IsDraftPr = true;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/99");
        payload.PullRequestNumber.Should().Be("99");
        payload.IsDraftPr.Should().BeTrue();
    }

    [Fact]
    public void BuildCompletionPayload_PreservesAnalysisRecommendation()
    {
        var run = CreateRunAtStep(PipelineStep.Completed);
        run.AnalysisRecommendation = AnalysisGateResult.WontDo;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.AnalysisRecommendation.Should().Be(AnalysisGateResult.WontDo);
    }

    [Fact]
    public void BuildCompletionPayload_PreservesCodeReviewMetrics()
    {
        var run = CreateRunAtStep(PipelineStep.Completed);
        run.CodeReviewAgentsRun = new List<string> { "Security", "Correctness" };
        run.SetCodeReviewCounts(critical: 2, warning: 5, suggestion: 10);

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.CodeReviewAgentsRun.Should().BeEquivalentTo(new[] { "Security", "Correctness" });
        payload.CodeReviewCriticalCount.Should().Be(2);
        payload.CodeReviewWarningCount.Should().Be(5);
        payload.CodeReviewSuggestionCount.Should().Be(10);
    }

    [Fact]
    public void BuildCompletionPayload_PreservesBrainUpdateStatus()
    {
        var run = CreateRunAtStep(PipelineStep.Completed);
        run.BrainUpdatesPushed = true;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.BrainUpdatesPushed.Should().BeTrue();
    }

    [Fact]
    public void BuildCompletionPayload_PreservesFinalLabel()
    {
        var run = CreateRunAtStep(PipelineStep.Completed);
        run.FinalLabel = AgentLabels.Done;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.FinalLabel.Should().Be(AgentLabels.Done);
    }

    // ── BuildStepMetadata ───────────────────────────────────────────────

    [Fact]
    public void BuildStepMetadata_IncludesBranchName_AfterCreatingBranch()
    {
        var run = CreateRunAtStep(PipelineStep.AnalyzingCode);
        run.BranchName = "feature/auto-123-fix-bug";

        var metadata = PipelineSignalRReporter.BuildStepMetadata(run, PipelineStep.AnalyzingCode);

        metadata.Should().NotBeNull();
        metadata!["BranchName"].Should().Be("feature/auto-123-fix-bug");
    }

    [Fact]
    public void BuildStepMetadata_IncludesRetryCount_WhenNonZero()
    {
        var run = CreateRunAtStep(PipelineStep.GeneratingCode);
        run.RetryCount = 3;

        var metadata = PipelineSignalRReporter.BuildStepMetadata(run, PipelineStep.RunningQualityGates);

        metadata.Should().NotBeNull();
        metadata!["RetryCount"].Should().Be("3");
    }

    [Fact]
    public void BuildStepMetadata_IncludesFileStats_AfterGeneratingCode()
    {
        var run = CreateRunAtStep(PipelineStep.GeneratingCode);
        run.FilesChangedCount = 7;
        run.LinesAdded = 200;
        run.LinesRemoved = 45;

        var metadata = PipelineSignalRReporter.BuildStepMetadata(run, PipelineStep.RunningQualityGates);

        metadata.Should().NotBeNull();
        metadata!["FilesChangedCount"].Should().Be("7");
        metadata["LinesAdded"].Should().Be("200");
        metadata["LinesRemoved"].Should().Be("45");
    }

    [Fact]
    public void BuildStepMetadata_ReturnsNull_WhenNoRelevantDataPresent()
    {
        var run = CreateRunAtStep(PipelineStep.Created);

        var metadata = PipelineSignalRReporter.BuildStepMetadata(run, PipelineStep.CloningRepository);

        metadata.Should().BeNull();
    }

    [Fact]
    public void BuildStepMetadata_IncludesTokenAndCost_WhenNonZero()
    {
        var run = CreateRunAtStep(PipelineStep.GeneratingCode);
        run.TotalTokens = 50000;
        run.TotalCost = 0.95m;

        var metadata = PipelineSignalRReporter.BuildStepMetadata(run, PipelineStep.RunningQualityGates);

        metadata.Should().NotBeNull();
        metadata!["TotalTokens"].Should().Be("50000");
        metadata["TotalCost"].Should().Be("0.95");
    }

    [Fact]
    public void BuildStepMetadata_IncludesCodeReviewData_DuringReview()
    {
        var run = CreateRunAtStep(PipelineStep.ReviewingCode);
        run.CodeReviewIterationsTotal = 2;
        run.CodeReviewIterationsCompleted = 1;
        run.SetCodeReviewCounts(critical: 3, warning: 0, suggestion: 0);
        run.CodeReviewAgentsRun = new List<string> { "Security" };

        var metadata = PipelineSignalRReporter.BuildStepMetadata(run, PipelineStep.ReviewingCode);

        metadata.Should().NotBeNull();
        metadata!["CodeReviewIterationsTotal"].Should().Be("2");
        metadata["CodeReviewIterationsCompleted"].Should().Be("1");
        metadata["CodeReviewCriticalCount"].Should().Be("3");
    }

    [Fact]
    public void BuildStepMetadata_IncludesDecompositionResults_AfterCreatingIssues()
    {
        var run = CreateRunAtStep(PipelineStep.CreatingIssues);
        run.DecompositionSubIssuesCreated = 4;
        run.DecompositionSubIssuesAttempted = 5;

        var metadata = PipelineSignalRReporter.BuildStepMetadata(run, PipelineStep.PostingSummary);

        metadata.Should().NotBeNull();
        metadata!["DecompositionSubIssuesCreated"].Should().Be("4");
        metadata["DecompositionSubIssuesAttempted"].Should().Be("5");
    }

    // ── Helper ──────────────────────────────────────────────────────────

    private static PipelineRun CreateRunAtStep(PipelineStep step)
    {
        var run = PipelineRun.Create(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: "org/repo#42",
            issueTitle: "Test Issue",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            runType: PipelineRunType.Implementation,
            initiatedBy: "test",
            agentId: "agent-1");
        run.CurrentStep = step;
        return run;
    }
}
