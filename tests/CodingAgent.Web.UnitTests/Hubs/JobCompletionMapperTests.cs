using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Hubs;

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

        run.CodeReviewCriticalCount.Should().Be(2);
        run.CodeReviewWarningCount.Should().Be(5);
        run.CodeReviewSuggestionCount.Should().Be(8);
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

    [Fact]
    public void Apply_PropagatesFinalLabel()
    {
        // FinalLabel must be copied from payload to run so CompleteRunAsync can read it
        // and produce the correct label (agent:needs-refinement, agent:wont-do, etc.)
        // rather than always falling back to a terminalStatus-derived label.
        var run = CreateRun();
        run.FinalLabel = null;
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = AgentLabels.NeedsRefinement
        };

        JobCompletionMapper.Apply(run, payload);

        run.FinalLabel.Should().Be(AgentLabels.NeedsRefinement);
    }

    [Fact]
    public void Apply_PropagatesFinalLabel_WhenNull()
    {
        // Null FinalLabel in payload should clear any existing value on the run
        var run = CreateRun();
        run.FinalLabel = AgentLabels.Done; // pre-existing (shouldn't happen in production, but guard it)
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = null
        };

        JobCompletionMapper.Apply(run, payload);

        run.FinalLabel.Should().BeNull();
    }

    /// <summary>
    /// When the completion payload has a null PullRequestUrl (e.g. ConflictRestart — no new PR
    /// was created in this run), any PR URL already set on the run by PullRequestOrchestrator must
    /// be preserved, not overwritten with null.
    /// Regression lock for issue #2947: previously Apply() unconditionally assigned
    /// run.PullRequestUrl = payload.PullRequestUrl, erasing a pre-existing URL.
    /// </summary>
    [Fact]
    public void Apply_NullPullRequestUrlInPayload_PreservesExistingRunUrl()
    {
        const string existingPrUrl = "https://github.com/org/repo/pull/42";
        var run = CreateRun();
        run.PullRequestUrl = existingPrUrl; // set by PullRequestOrchestrator before agent reports completion

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.ConflictRestart,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = null  // ConflictRestart: agent didn't create a PR in this run
        };

        JobCompletionMapper.Apply(run, payload);

        // The pre-existing URL must survive: it's the conflicted PR that still exists.
        run.PullRequestUrl.Should().Be(existingPrUrl,
            "a null PullRequestUrl in the payload must not erase an existing URL on the run");
        // TODO: [WARNING] The symmetric overwrite case is not tested: run has URL-A, payload carries
        // URL-B (both non-null) — verify URL-B wins. Also not tested: run has a non-zero
        // PullRequestNumber that is silently reset to 0 by a ConflictRestart payload. Both are
        // unspecified by existing tests and could hide a too-narrow or incorrectly applied guard.
        // (TestQualityReviewer, issue #2947)
    }

    /// <summary>
    /// When the completion payload has a non-null PullRequestUrl, it must be applied to the run
    /// (normal success path).
    /// </summary>
    [Fact]
    public void Apply_NonNullPullRequestUrlInPayload_UpdatesRunUrl()
    {
        var run = CreateRun();
        run.PullRequestUrl = null; // no prior URL

        const string newPrUrl = "https://github.com/org/repo/pull/7";
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = newPrUrl
        };

        JobCompletionMapper.Apply(run, payload);

        run.PullRequestUrl.Should().Be(newPrUrl,
            "a non-null PullRequestUrl in the payload must overwrite the run's existing (null) URL");
        // TODO: [WARNING] PullRequestNumber is guarded by string.IsNullOrEmpty in the same Apply()
        // method, but neither test here asserts PullRequestNumber behaviour. A bug in that branch
        // (e.g. the guard preventing a valid PullRequestNumber from being written, or failing to
        // preserve an existing number when the payload sends null/empty) would not be caught.
        // Add assertions for PullRequestNumber in both the null-preservation test and this update
        // test to fully lock in the symmetric guard behaviour. (TestQualityReviewer, issue #2947)
    }

    [Fact]
    public void Apply_CopiesFailureCategory()
    {
        var run = CreateRun();

        // Payload with a FailureCategory → run.FailureCategory is set
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureCategory = FailureReason.GateRejected
        };
        JobCompletionMapper.Apply(run, payload);
        run.FailureCategory.Should().Be(FailureReason.GateRejected,
            "Apply must copy a non-null payload.FailureCategory to run.FailureCategory");

        // Null FailureCategory in payload must not overwrite an existing value
        var run2 = CreateRun();
        run2.FailureCategory = FailureReason.ExitCodeFailure;
        var payloadNullCat = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureCategory = null
        };
        JobCompletionMapper.Apply(run2, payloadNullCat);
        run2.FailureCategory.Should().Be(FailureReason.ExitCodeFailure,
            "Apply must not overwrite run.FailureCategory when payload.FailureCategory is null");
    }

    /// <summary>
    /// Regression test for issue #3730: when the API-side run already has a CompletedAtOffset
    /// (e.g. deserialized from Redis with an earlier stamp), Apply must overwrite it with the
    /// agent's authoritative completion time from the payload.
    /// </summary>
    [Fact]
    public void Apply_UsesThePayloadCompletionTime_EvenWhenTheRunWasStampedEarlier()
    {
        var t1 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero);
        var run = CreateRun();
        run.MarkCompleted(t1);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = t2
        };

        JobCompletionMapper.Apply(run, payload);

        run.CompletedAtOffset.Should().Be(t2,
            "Apply must overwrite an earlier stamp with the agent's authoritative completion time");
    }

    [Fact]
    public void Apply_CopiesPhaseBreakdown_AllFields()
    {
        var run = CreateRun();
        var phaseEntry = new PhaseUsagePayload
        {
            Tokens = 12345,
            Cost = 1.23m,
            SessionCount = 3,
            AgentTimeSeconds = 42.5,
            Provider = "kiro",
            Model = "claude-sonnet-4-5",
            InputTokens = 5000,
            OutputTokens = 7000,
            ReasoningTokens = 300,
            CacheReadTokens = 100,
            CacheWriteTokens = 200,
            Turns = 7,
            WebSearchRequests = 2,
            BillingMode = "subscription"
        };
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload> { ["codegen"] = phaseEntry }
        };

        JobCompletionMapper.Apply(run, payload);

        run.Metrics.PhaseBreakdown.Should().HaveCount(1);
        var result = run.Metrics.PhaseBreakdown["codegen"];
        result.Tokens.Should().Be(12345);
        result.Cost.Should().Be(1.23m);
        result.SessionCount.Should().Be(3);
        result.AgentTimeSeconds.Should().Be(42.5);
        result.Provider.Should().Be("kiro");
        result.Model.Should().Be("claude-sonnet-4-5");
        result.InputTokens.Should().Be(5000);
        result.OutputTokens.Should().Be(7000);
        result.ReasoningTokens.Should().Be(300);
        result.CacheReadTokens.Should().Be(100);
        result.CacheWriteTokens.Should().Be(200);
        result.Turns.Should().Be(7);
        result.WebSearchRequests.Should().Be(2);
        result.BillingMode.Should().Be("subscription");
    }

    [Fact]
    public void Apply_PhaseBreakdownReplacesExistingEntries()
    {
        var run = CreateRun();
        // Pre-populate with two entries
        run.Metrics.PhaseBreakdown["codegen"] = new PhaseUsage(Tokens: 999, Cost: 9.99m);
        run.Metrics.PhaseBreakdown["analysis"] = new PhaseUsage(Tokens: 111, Cost: 1.11m);

        var newCodegen = new PhaseUsagePayload { Tokens = 500, Cost = 0.50m, SessionCount = 1 };
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload> { ["codegen"] = newCodegen }
        };

        JobCompletionMapper.Apply(run, payload);

        run.Metrics.PhaseBreakdown.Should().HaveCount(1,
            "payload is the authoritative final breakdown — existing entries must be replaced");
        run.Metrics.PhaseBreakdown.Should().ContainKey("codegen");
        run.Metrics.PhaseBreakdown["codegen"].Tokens.Should().Be(500);
        run.Metrics.PhaseBreakdown["codegen"].Cost.Should().Be(0.50m,
            "the whole PhaseUsage object must be replaced, not merged with the old entry");
        run.Metrics.PhaseBreakdown["codegen"].SessionCount.Should().Be(1,
            "the whole PhaseUsage object must be replaced, not merged with the old entry");
        run.Metrics.PhaseBreakdown.Should().NotContainKey("analysis",
            "analysis key was absent from the payload and must be removed");
    }

    [Fact]
    public void Apply_NullPhaseBreakdown_LeavesExistingEntries()
    {
        var run = CreateRun();
        run.Metrics.PhaseBreakdown["analysis"] = new PhaseUsage(Tokens: 777, Cost: 0.77m);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = null
        };

        JobCompletionMapper.Apply(run, payload);

        run.Metrics.PhaseBreakdown.Should().HaveCount(1,
            "a null PhaseBreakdown in the payload must leave existing entries unchanged");
        run.Metrics.PhaseBreakdown["analysis"].Tokens.Should().Be(777);
    }

    [Fact]
    public void Apply_EmptyPhaseBreakdown_ClearsExistingEntries()
    {
        // A non-null but empty dictionary means the agent completed with zero phases —
        // existing stale entries must be cleared, not preserved (unlike the null case).
        var run = CreateRun();
        run.Metrics.PhaseBreakdown["analysis"] = new PhaseUsage(Tokens: 500, Cost: 0.50m);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>()
        };

        JobCompletionMapper.Apply(run, payload);

        run.Metrics.PhaseBreakdown.Should().BeEmpty(
            "a non-null empty PhaseBreakdown must clear existing entries, not leave them (unlike null)");
    }
}
