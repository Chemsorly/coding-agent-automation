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
    public void BuildCompletionPayload_IsRework_WhenLinkedPullRequestSet()
    {
        var run = MakeRun();
        run.LinkedPullRequest = new LinkedPullRequest { Number = 99, Url = "https://github.com/org/repo/pull/99", BranchName = "fix/99", IsDraft = false };

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.IsRework.Should().BeTrue();
    }

    [Fact]
    public void BuildCompletionPayload_IsNotRework_WhenNoLinkedPullRequest()
    {
        var run = MakeRun();
        // LinkedPullRequest defaults to null

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.IsRework.Should().BeFalse();
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
    public void BuildFailurePayload_CopiesRetryCount()
    {
        var run = MakeRun();
        run.RetryCount = 2;

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Error");

        payload.RetryCount.Should().Be(2);
    }

    [Fact]
    public void BuildFailurePayload_IsRework_WhenLinkedPullRequestSet()
    {
        var run = MakeRun();
        run.LinkedPullRequest = new LinkedPullRequest { Number = 55, Url = "https://github.com/org/repo/pull/55", BranchName = "fix/55", IsDraft = false };

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Error");

        payload.IsRework.Should().BeTrue();
    }

    [Fact]
    public void BuildFailurePayload_CopiesBlacklistedFilesDetected()
    {
        var run = MakeRun();
        run.BlacklistedFilesDetected = new[] { "secrets.json", ".env" };

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Error");

        payload.BlacklistedFilesDetected.Should().BeEquivalentTo(new[] { "secrets.json", ".env" });
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

        payload.AnalysisConcerns.Should().BeEquivalentTo(new[] { "concern-a", "concern-b" });
    }

    [Fact]
    public void BuildCompletionPayload_AnalysisBlockingIssues_Copied()
    {
        var run = MakeRun();
        run.AnalysisBlockingIssues = ["blocker-1"];

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.AnalysisBlockingIssues.Should().BeEquivalentTo(new[] { "blocker-1" });
    }

    [Fact]
    public void BuildFailurePayload_AnalysisConcerns_Copied()
    {
        var run = MakeRun();
        run.AnalysisConcerns = ["fail-concern"];

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "err");

        payload.AnalysisConcerns.Should().BeEquivalentTo(new[] { "fail-concern" });
    }

    [Fact]
    public void BuildFailurePayload_AnalysisBlockingIssues_Copied()
    {
        var run = MakeRun();
        run.AnalysisBlockingIssues = ["block-a", "block-b"];

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "err");

        payload.AnalysisBlockingIssues.Should().BeEquivalentTo(new[] { "block-a", "block-b" });
    }
}
