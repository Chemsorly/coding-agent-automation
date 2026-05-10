// Feature: 020-agent-feedback-loops, Property 7: ToSummary preserves Feedback
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Property-based tests verifying that ToSummary() preserves the Feedback property.
/// **Validates: Requirements 4.4**
/// </summary>
public class PipelineRunFeedbackTests
{
    /// <summary>
    /// Property 7: For any PipelineRun with non-null Feedback, calling ToSummary()
    /// produces a PipelineRunSummary whose Feedback is reference-equal to the original.
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PipelineRunWithFeedbackArbitraries) })]
    public void ToSummary_PreservesFeedback_ReferenceEqual(PipelineRun run)
    {
        // Precondition: run has non-null Feedback (guaranteed by generator)
        run.Feedback.Should().NotBeNull();

        var summary = run.ToSummary();

        // ToSummary assigns Feedback = Feedback directly, so reference equality holds
        summary.Feedback.Should().BeSameAs(run.Feedback);
    }

    /// <summary>
    /// Property 7 (structural): For any PipelineRun with non-null Feedback, calling ToSummary()
    /// produces a PipelineRunSummary whose Feedback has identical field values.
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PipelineRunWithFeedbackArbitraries) })]
    public void ToSummary_PreservesFeedback_StructurallyEqual(PipelineRun run)
    {
        var summary = run.ToSummary();

        summary.Feedback.Should().NotBeNull();
        summary.Feedback!.Outcome.Should().Be(run.Feedback!.Outcome);
        summary.Feedback.CollectedAtUtc.Should().Be(run.Feedback.CollectedAtUtc);
        summary.Feedback.Harness.Should().BeSameAs(run.Feedback.Harness);
        summary.Feedback.Issue.Should().BeSameAs(run.Feedback.Issue);
    }
}

public class PipelineRunWithFeedbackArbitraries
{
    private static readonly string[] RunIds = ["run-1", "run-2", "run-3", "run-abc", "run-xyz"];
    private static readonly string[] IssueIds = ["42", "100", "7", "999", "1"];
    private static readonly string[] IssueTitles = ["Fix bug", "Add feature", "Refactor code"];
    private static readonly string[] ConfigIds = ["config-1", "config-2", "config-3"];
    private static readonly string[] Categories = ["missing file context", "mcp tool timeout", "prompt instruction gap"];
    private static readonly string[] StuckReasons = ["Could not find the file", "Tool timed out", "Contradictory instructions"];
    private static readonly string[] ContextItems = ["src/main.cs", "README.md", "config.json", "tests/unit.cs"];
    private static readonly string[] CapabilityItems = ["file search", "web access", "database query"];
    private static readonly string[] PromptIssueItems = ["unclear instructions", "contradictory rules"];
    private static readonly string[] SuggestionItems = ["add file context", "increase timeout"];
    private static readonly string[] AffectedFileItems = ["src/broken.cs", "lib/old.cs"];

    public static Arbitrary<PipelineRun> PipelineRunArb()
    {
        var harnessFeedbackGen =
            from hasCategory in Gen.Elements(true, false)
            from category in Gen.Elements(Categories)
            from hasStuckReason in Gen.Elements(true, false)
            from stuckReason in Gen.Elements(StuckReasons)
            from missingContext in Gen.SubListOf(ContextItems)
            from missingCap in Gen.SubListOf(CapabilityItems)
            from promptIssues in Gen.SubListOf(PromptIssueItems)
            from suggestions in Gen.SubListOf(SuggestionItems)
            select new HarnessFeedback
            {
                Category = hasCategory ? category : null,
                StuckReason = hasStuckReason ? stuckReason : null,
                MissingContext = missingContext.ToList(),
                MissingCapabilities = missingCap.ToList(),
                PromptIssues = promptIssues.ToList(),
                Suggestions = suggestions.ToList()
            };

        var issueFeedbackGen =
            from hasCategory in Gen.Elements(true, false)
            from category in Gen.Elements(Categories)
            from description in Gen.Elements("Issue is unclear", "Missing component", "Contradictory criteria")
            from affectedFiles in Gen.SubListOf(AffectedFileItems)
            from hasHumanAction in Gen.Elements(true, false)
            from humanAction in Gen.Elements("Clarify acceptance criteria", "Add missing file")
            select new IssueFeedback
            {
                Category = hasCategory ? category : null,
                Description = description,
                AffectedFiles = affectedFiles.ToList(),
                HumanActionNeeded = hasHumanAction ? humanAction : null
            };

        var feedbackGen =
            from outcome in Gen.Elements(FeedbackOutcome.Success, FeedbackOutcome.Failure)
            from collectedAt in Gen.Elements(
                new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 6, 15, 8, 30, 0, DateTimeKind.Utc),
                new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc))
            from harness in harnessFeedbackGen
            from hasIssue in Gen.Elements(true, false)
            from issue in hasIssue
                ? issueFeedbackGen.Select(i => (IssueFeedback?)i)
                : Gen.Constant((IssueFeedback?)null)
            select new RunFeedback
            {
                Outcome = outcome,
                CollectedAtUtc = collectedAt,
                Harness = harness,
                Issue = issue
            };

        var runGen =
            from runId in Gen.Elements(RunIds)
            from issueId in Gen.Elements(IssueIds)
            from issueTitle in Gen.Elements(IssueTitles)
            from issueProviderConfigId in Gen.Elements(ConfigIds)
            from repoProviderConfigId in Gen.Elements(ConfigIds)
            from step in Gen.Elements(
                PipelineStep.Created,
                PipelineStep.GeneratingCode,
                PipelineStep.Completed,
                PipelineStep.Failed)
            from feedback in feedbackGen
            select new PipelineRun
            {
                RunId = runId,
                IssueIdentifier = issueId,
                IssueTitle = issueTitle,
                IssueProviderConfigId = issueProviderConfigId,
                RepoProviderConfigId = repoProviderConfigId,
                CurrentStep = step,
                StartedAt = DateTime.UtcNow,
                Feedback = feedback
            };

        return runGen.ToArbitrary();
    }
}
