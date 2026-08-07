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
}
