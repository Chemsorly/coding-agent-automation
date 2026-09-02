using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using Xunit;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="LocalPipelineExecutor"/> pure-logic payload builders.
/// </summary>
public class LocalPipelineExecutorPayloadTests
{
    private static readonly string[] s_SecretsJsonEnv = new[] { "secrets.json", ".env" };
    private static readonly string[] s_ConcernAB = new[] { "concern-a", "concern-b" };
    private static readonly string[] s_BlockerAB = new[] { "blocker-1" };
    private static readonly string[] s_FailConcern = new[] { "fail-concern" };
    private static readonly string[] s_BlockAB = new[] { "block-a", "block-b" };

    // ── BuildCompletionPayload ────────────────────────────────────────────────

    [Fact]
    public void BuildCompletionPayload_SetsCurrentStepAsFinalStep()
    {
        var run = MakeRun();
        run.CurrentStep = PipelineStep.Completed;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public void BuildCompletionPayload_CopiesRetryCount()
    {
        var run = MakeRun();
        run.RetryCount = 3;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.RetryCount.Should().Be(3);
    }

    [Fact]
    public void BuildCompletionPayload_RunModeRework_WhenLinkedPullRequestSet()
    {
        var run = MakeRun();
        run.LinkedPullRequest = new LinkedPullRequest { Number = 99, Url = "https://github.com/org/repo/pull/99", BranchName = "fix/99", IsDraft = false };
        run.RunMode = RunMode.Rework;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.RunMode.Should().Be(RunMode.Rework);
    }

    [Fact]
    public void BuildCompletionPayload_RunModeNew_WhenNoLinkedPullRequest()
    {
        var run = MakeRun();
        // LinkedPullRequest defaults to null, RunMode defaults to New

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.RunMode.Should().Be(RunMode.New);
    }

    [Fact]
    public void BuildCompletionPayload_CopiesPullRequestUrl()
    {
        var run = MakeRun();
        run.PullRequestUrl = "https://github.com/org/repo/pull/42";

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/42");
    }

    [Fact]
    public void BuildCompletionPayload_CopiesPullRequestNumber()
    {
        var run = MakeRun();
        run.PullRequestNumber = "42";

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.PullRequestNumber.Should().Be("42");
    }

    [Fact]
    public void BuildCompletionPayload_CopiesFailureReason()
    {
        var run = MakeRun();
        run.FailureReason = "Agent timed out";

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.FailureReason.Should().Be("Agent timed out");
    }

    [Fact]
    public void BuildCompletionPayload_CopiesCodeReviewCounts()
    {
        var run = MakeRun();
        run.SetCodeReviewCounts(critical: 2, warning: 3, suggestion: 5);

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.CodeReviewCriticalCount.Should().Be(2);
        payload.CodeReviewWarningCount.Should().Be(3);
        payload.CodeReviewSuggestionCount.Should().Be(5);
    }

    [Fact]
    public void BuildCompletionPayload_CopiesTotalTokens()
    {
        var run = MakeRun();
        run.TotalTokens = 12345;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.TotalTokens.Should().Be(12345);
    }

    [Fact]
    public void BuildCompletionPayload_CopiesFinalLabel()
    {
        var run = MakeRun();
        run.FinalLabel = AgentLabels.Done;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.FinalLabel.Should().Be(AgentLabels.Done);
    }

    // ── BuildFailurePayload ───────────────────────────────────────────────────

    [Fact]
    public void BuildFailurePayload_SetsFinalStepToFailed()
    {
        var run = MakeRun();
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Build exploded");

        payload.FinalStep.Should().Be(PipelineStep.Failed);
    }

    [Fact]
    public void BuildFailurePayload_SetsFailureReason()
    {
        var run = MakeRun();
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Build exploded");

        payload.FailureReason.Should().Be("Build exploded");
    }

    [Fact]
    public void BuildFailurePayload_SetsFailureCategory_WhenProvided()
    {
        var run = MakeRun();
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Agent crashed", FailureReason.AgentError);

        payload.FailureCategory.Should().Be(FailureReason.AgentError);
    }

    [Fact]
    public void BuildFailurePayload_FailureCategoryIsNull_WhenNotProvided()
    {
        var run = MakeRun();
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Error");

        payload.FailureCategory.Should().BeNull();
    }

    [Fact]
    public void WhenRunHasFailureCategorySet_FailedOutcomePath_PayloadContainsFailureCategory()
    {
        // TODO: This test is tautological — it calls BuildFailurePayload directly with run.FailureCategory
        // as the third argument, then asserts the returned payload contains that value. It verifies the
        // BuildFailurePayload method stores its argument correctly, but does NOT verify that the call site
        // in LocalPipelineExecutor's exception-handling path (FailedOutcome case) actually passes
        // run.FailureCategory as the third argument. If that call site were reverted to
        // BuildFailurePayload(run, ex.Message) (dropping the third arg), this test would still pass.
        // A proper regression test should drive the full FailedOutcome path through LocalPipelineExecutor
        // and assert the emitted payload carries the correct FailureCategory.
        // (Issue #2202 review, TestQualityReviewer)
        // Regression test for issue #2202 Fix C (secondary).
        // Verifies that when run.FailureCategory is set (e.g. by ReconciliationService for Timeout),
        // BuildFailurePayload is called with run.FailureCategory so the metric tag reflects the
        // actual failure reason instead of null/"unknown".
        var run = MakeRun();
        run.FailureCategory = FailureReason.QualityGateExhausted;

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Quality gate retries exhausted", run.FailureCategory);

        payload.FailureCategory.Should().Be(FailureReason.QualityGateExhausted,
            "FailureCategory set on run must be forwarded to the payload so metric tags are accurate");
    }

    [Fact]
    public void BuildFailurePayload_CopiesRetryCount()
    {
        var run = MakeRun();
        run.RetryCount = 2;

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Error");

        payload.RetryCount.Should().Be(2);
    }

    [Fact]
    public void BuildFailurePayload_RunModeRework_WhenLinkedPullRequestSet()
    {
        var run = MakeRun();
        run.LinkedPullRequest = new LinkedPullRequest { Number = 55, Url = "https://github.com/org/repo/pull/55", BranchName = "fix/55", IsDraft = false };
        run.RunMode = RunMode.Rework;

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Error");

        payload.RunMode.Should().Be(RunMode.Rework);
    }

    [Fact]
    public void BuildFailurePayload_CopiesBlacklistedFilesDetected()
    {
        var run = MakeRun();
        run.BlacklistedFilesDetected = new[] { "secrets.json", ".env" };

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Error");

        payload.BlacklistedFilesDetected.Should().BeEquivalentTo(s_SecretsJsonEnv);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineRun MakeRun() => new()
    {
        RunId = "test-run-id",
        IssueIdentifier = "owner/repo#1",
        IssueTitle = "Test issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1"
    };

    // ── BuildCompletionPayload — additional field coverage ────────────────────

    [Fact]
    public void BuildCompletionPayload_IsDraftPr_Copied()
    {
        var run = MakeRun();
        run.IsDraftPr = true;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.IsDraftPr.Should().BeTrue();
    }

    [Fact]
    public void BuildCompletionPayload_BrainUpdatesPushed_Copied()
    {
        var run = MakeRun();
        run.BrainUpdatesPushed = true;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.BrainUpdatesPushed.Should().BeTrue();
    }

    [Fact]
    public void BuildCompletionPayload_AnalysisRecommendation_Copied()
    {
        var run = MakeRun();
        run.AnalysisRecommendation = CodingAgentWebUI.Pipeline.Models.AnalysisGateResult.Ready;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.AnalysisRecommendation.Should().Be(CodingAgentWebUI.Pipeline.Models.AnalysisGateResult.Ready);
    }

    [Fact]
    public void BuildCompletionPayload_FilesChangedCount_Copied()
    {
        var run = MakeRun();
        run.FilesChangedCount = 7;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.FilesChangedCount.Should().Be(7);
    }

    [Fact]
    public void BuildCompletionPayload_LinesAdded_Copied()
    {
        var run = MakeRun();
        run.LinesAdded = 120;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.LinesAdded.Should().Be(120);
    }

    [Fact]
    public void BuildCompletionPayload_LinesRemoved_Copied()
    {
        var run = MakeRun();
        run.LinesRemoved = 30;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.LinesRemoved.Should().Be(30);
    }

    [Fact]
    public void BuildCompletionPayload_TotalCost_Copied()
    {
        var run = MakeRun();
        run.TotalCost = 2.50m;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.TotalCost.Should().Be(2.50m);
    }

    [Fact]
    public void BuildCompletionPayload_CodeReviewAgentsRun_Copied()
    {
        var run = MakeRun();
        run.CodeReviewAgentsRun = ["agent-a", "agent-b"];

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.CodeReviewAgentsRun.Should().BeEquivalentTo(["agent-a", "agent-b"]);
    }

    [Fact]
    public void BuildCompletionPayload_CompletedAtOffset_UsedWhenSet()
    {
        var run = MakeRun();
        var expectedTime = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        run.CompletedAtOffset = expectedTime;

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.CompletedAt.Should().Be(expectedTime);
    }

    // ── BuildFailurePayload — additional field coverage ───────────────────────

    [Fact]
    public void BuildFailurePayload_FinalLabel_NotSet()
    {
        var run = MakeRun();
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "error");

        // BuildFailurePayload does not set FinalLabel (no run.FinalLabel passed through)
        payload.FinalLabel.Should().BeNull();
    }

    [Fact]
    public void BuildFailurePayload_FilesChangedCount_Copied()
    {
        var run = MakeRun();
        run.FilesChangedCount = 3;
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "err");
        payload.FilesChangedCount.Should().Be(3);
    }

    [Fact]
    public void BuildFailurePayload_LinesAdded_Copied()
    {
        var run = MakeRun();
        run.LinesAdded = 50;
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "err");
        payload.LinesAdded.Should().Be(50);
    }

    [Fact]
    public void BuildFailurePayload_LinesRemoved_Copied()
    {
        var run = MakeRun();
        run.LinesRemoved = 10;
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "err");
        payload.LinesRemoved.Should().Be(10);
    }

    [Fact]
    public void BuildFailurePayload_TotalTokens_Copied()
    {
        var run = MakeRun();
        run.TotalTokens = 7777L;
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "err");
        payload.TotalTokens.Should().Be(7777L);
    }

    [Fact]
    public void BuildFailurePayload_TotalCost_Copied()
    {
        var run = MakeRun();
        run.TotalCost = 0.75m;
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "err");
        payload.TotalCost.Should().Be(0.75m);
    }

    [Fact]
    public void BuildFailurePayload_CodeReviewAgentsRun_Copied()
    {
        var run = MakeRun();
        run.CodeReviewAgentsRun = ["sec-agent"];
        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "err");
        payload.CodeReviewAgentsRun.Should().BeEquivalentTo(["sec-agent"]);
    }

    // ── BuildPayloadBase — Feedback/AnalysisConcerns/AnalysisBlockingIssues ──

    [Fact]
    public void BuildCompletionPayload_Feedback_CopiedWhenSet()
    {
        var run = MakeRun();
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback()
        };

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.Feedback.Should().BeSameAs(run.Feedback);
    }

    [Fact]
    public void BuildCompletionPayload_Feedback_NullWhenNotSet()
    {
        var run = MakeRun();
        // Feedback defaults to null

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.Feedback.Should().BeNull();
    }

    [Fact]
    public void BuildFailurePayload_Feedback_CopiedWhenSet()
    {
        var run = MakeRun();
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Failure,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback()
        };

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "error");

        payload.Feedback.Should().BeSameAs(run.Feedback);
    }

    [Fact]
    public void BuildCompletionPayload_AnalysisConcerns_Copied()
    {
        var run = MakeRun();
        run.AnalysisConcerns = ["concern-a", "concern-b"];

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.AnalysisConcerns.Should().BeEquivalentTo(s_ConcernAB);
    }

    [Fact]
    public void BuildCompletionPayload_AnalysisBlockingIssues_Copied()
    {
        var run = MakeRun();
        run.AnalysisBlockingIssues = ["blocker-1"];

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.AnalysisBlockingIssues.Should().BeEquivalentTo(s_BlockerAB);
    }

    [Fact]
    public void BuildFailurePayload_AnalysisConcerns_Copied()
    {
        var run = MakeRun();
        run.AnalysisConcerns = ["fail-concern"];

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "err");

        payload.AnalysisConcerns.Should().BeEquivalentTo(s_FailConcern);
    }

    [Fact]
    public void BuildFailurePayload_AnalysisBlockingIssues_Copied()
    {
        var run = MakeRun();
        run.AnalysisBlockingIssues = ["block-a", "block-b"];

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "err");

        payload.AnalysisBlockingIssues.Should().BeEquivalentTo(s_BlockAB);
    }
}
