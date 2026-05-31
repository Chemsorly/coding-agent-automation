using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>Unit tests for <see cref="JobCompletionMapper"/>.</summary>
public sealed class JobCompletionMapperTests
{
    private static PipelineRun CreateRun() => new()
    {
        RunId = "job-1",
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "cfg-1",
        RepoProviderConfigId = "cfg-2"
    };

    [Fact]
    public void Apply_MapsAllScalarProperties()
    {
        var run = CreateRun();
        var now = new DateTimeOffset(2026, 5, 28, 12, 0, 0, TimeSpan.Zero);
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = now,
            FailureReason = "some failure",
            PullRequestUrl = "https://github.com/org/repo/pull/7",
            PullRequestNumber = "7",
            IsDraftPr = true,
            RetryCount = 3,
            FilesChangedCount = 12,
            LinesAdded = 300,
            LinesRemoved = 45,
            BrainUpdatesPushed = true,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            TotalTokens = 50000,
            TotalCost = 1.23m
        };

        JobCompletionMapper.Apply(run, payload);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.CompletedAt.Should().Be(now.UtcDateTime);
        run.FailureReason.Should().Be("some failure");
        run.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/7");
        run.PullRequestNumber.Should().Be("7");
        run.IsDraftPr.Should().BeTrue();
        run.RetryCount.Should().Be(3);
        run.FilesChangedCount.Should().Be(12);
        run.LinesAdded.Should().Be(300);
        run.LinesRemoved.Should().Be(45);
        run.BrainUpdatesPushed.Should().BeTrue();
        run.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
        run.TotalTokens.Should().Be(50000);
        run.TotalCost.Should().Be(1.23m);
    }

    [Fact]
    public void Apply_MapsCollectionProperties()
    {
        var run = CreateRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            AnalysisConcerns = ["concern1", "concern2"],
            AnalysisBlockingIssues = ["blocker1"],
            BlacklistedFilesDetected = ["secret.env"],
            CodeReviewAgentsRun = ["Correctness", "Security"]
        };

        JobCompletionMapper.Apply(run, payload);

        run.AnalysisConcerns.Should().BeEquivalentTo(["concern1", "concern2"]);
        run.AnalysisBlockingIssues.Should().BeEquivalentTo(["blocker1"]);
        run.BlacklistedFilesDetected.Should().BeEquivalentTo(["secret.env"]);
        run.CodeReviewAgentsRun.Should().BeEquivalentTo(["Correctness", "Security"]);
    }

    [Fact]
    public void Apply_MapsInterlockedFields()
    {
        var run = CreateRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            CodeReviewCriticalCount = 2,
            CodeReviewWarningCount = 5,
            CodeReviewSuggestionCount = 8
        };

        JobCompletionMapper.Apply(run, payload);

        Volatile.Read(ref run.CodeReviewCriticalCount).Should().Be(2);
        Volatile.Read(ref run.CodeReviewWarningCount).Should().Be(5);
        Volatile.Read(ref run.CodeReviewSuggestionCount).Should().Be(8);
    }

    [Fact]
    public void Apply_ConvertsCompletedAtToUtcDateTime()
    {
        var run = CreateRun();
        // Use a non-UTC offset to verify conversion
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = new DateTimeOffset(2026, 5, 28, 14, 30, 0, TimeSpan.FromHours(2))
        };

        JobCompletionMapper.Apply(run, payload);

        run.CompletedAt.Should().Be(new DateTime(2026, 5, 28, 12, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Apply_MapsFeedback()
    {
        var run = CreateRun();
        var feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback()
        };
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            Feedback = feedback
        };

        JobCompletionMapper.Apply(run, payload);

        run.Feedback.Should().BeSameAs(feedback);
    }

    [Fact]
    public void Apply_NullRunThrows()
    {
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        var act = () => JobCompletionMapper.Apply(null!, payload);

        act.Should().Throw<ArgumentNullException>().WithParameterName("run");
    }

    [Fact]
    public void Apply_NullPayloadThrows()
    {
        var run = CreateRun();

        var act = () => JobCompletionMapper.Apply(run, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("payload");
    }
}
