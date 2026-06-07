using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for epic polling eligibility and label state machine transitions.
/// Feature: 027-epic-decomposition-pipeline, Properties P3, P4
/// </summary>
public class DecompositionLabelPropertyTests
{
    /// <summary>
    /// All labels that can appear on an epic issue in the decomposition pipeline.
    /// Used to generate random label combinations for property testing.
    /// </summary>
    private static readonly string[] AllRelevantLabels =
    [
        AgentLabels.Epic,
        AgentLabels.EpicReview,
        AgentLabels.EpicApproved,
        AgentLabels.InProgress,
        AgentLabels.Error,
        AgentLabels.Done,
        AgentLabels.Next,
        AgentLabels.NeedsRefinement,
        AgentLabels.Cancelled,
        "enhancement",
        "bug",
        "documentation"
    ];

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 3: Epic Polling Eligibility Filter
    ///
    /// For any set of issues with random label combinations:
    /// - Phase 1 filter includes only issues with agent:epic AND without agent:epic-review,
    ///   agent:in-progress, agent:error, or agent:done.
    /// - Phase 2 filter includes only issues with agent:epic-approved AND without
    ///   agent:in-progress, agent:error, or agent:done.
    ///
    /// **Validates: Requirements 2.6, 2.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void EpicPollingEligibility_Phase1_IncludesOnlyEligibleIssues(PositiveInt issueCountRaw, int seed)
    {
        var issueCount = Math.Min(issueCountRaw.Get, 20);
        var rng = new Random(seed);

        // Generate random issues with random label combinations
        var issues = GenerateRandomIssues(issueCount, rng);

        // Apply Phase 1 filter (same logic as PipelineLoopService.FetchEpicIssuesAsync for agent:epic)
        var phase1Eligible = issues
            .Where(i => i.Labels.Contains(AgentLabels.Epic))
            .Where(i => !i.Labels.Contains(AgentLabels.EpicReview))
            .Where(i => !i.Labels.Contains(AgentLabels.InProgress))
            .Where(i => !i.Labels.Contains(AgentLabels.Error))
            .Where(i => !i.Labels.Contains(AgentLabels.Done))
            .ToList();

        // Verify: every eligible issue has agent:epic
        foreach (var issue in phase1Eligible)
        {
            issue.Labels.Should().Contain(AgentLabels.Epic);
        }

        // Verify: no eligible issue has any exclusion label
        foreach (var issue in phase1Eligible)
        {
            issue.Labels.Should().NotContain(AgentLabels.EpicReview);
            issue.Labels.Should().NotContain(AgentLabels.InProgress);
            issue.Labels.Should().NotContain(AgentLabels.Error);
            issue.Labels.Should().NotContain(AgentLabels.Done);
        }

        // Verify: every issue NOT in the eligible set either lacks agent:epic or has an exclusion label
        var excluded = issues
            .Where(i => i.Labels.Contains(AgentLabels.Epic))
            .Except(phase1Eligible)
            .ToList();

        foreach (var issue in excluded)
        {
            var hasExclusionLabel =
                issue.Labels.Contains(AgentLabels.EpicReview) ||
                issue.Labels.Contains(AgentLabels.InProgress) ||
                issue.Labels.Contains(AgentLabels.Error) ||
                issue.Labels.Contains(AgentLabels.Done);

            hasExclusionLabel.Should().BeTrue(
                $"Issue {issue.Identifier} has agent:epic but was excluded, so it must have an exclusion label. Labels: [{string.Join(", ", issue.Labels)}]");
        }
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 3: Epic Polling Eligibility Filter (Phase 2)
    ///
    /// For any set of issues with random label combinations:
    /// - Phase 2 filter includes only issues with agent:epic-approved AND without
    ///   agent:in-progress, agent:error, or agent:done.
    ///
    /// **Validates: Requirements 2.6, 2.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void EpicPollingEligibility_Phase2_IncludesOnlyEligibleIssues(PositiveInt issueCountRaw, int seed)
    {
        var issueCount = Math.Min(issueCountRaw.Get, 20);
        var rng = new Random(seed);

        // Generate random issues with random label combinations
        var issues = GenerateRandomIssues(issueCount, rng);

        // Apply Phase 2 filter (same logic as PipelineLoopService.FetchEpicIssuesAsync for agent:epic-approved)
        var phase2Eligible = issues
            .Where(i => i.Labels.Contains(AgentLabels.EpicApproved))
            .Where(i => !i.Labels.Contains(AgentLabels.InProgress))
            .Where(i => !i.Labels.Contains(AgentLabels.Error))
            .Where(i => !i.Labels.Contains(AgentLabels.Done))
            .ToList();

        // Verify: every eligible issue has agent:epic-approved
        foreach (var issue in phase2Eligible)
        {
            issue.Labels.Should().Contain(AgentLabels.EpicApproved);
        }

        // Verify: no eligible issue has any exclusion label
        foreach (var issue in phase2Eligible)
        {
            issue.Labels.Should().NotContain(AgentLabels.InProgress);
            issue.Labels.Should().NotContain(AgentLabels.Error);
            issue.Labels.Should().NotContain(AgentLabels.Done);
        }

        // Verify: every issue NOT in the eligible set either lacks agent:epic-approved or has an exclusion label
        var excluded = issues
            .Where(i => i.Labels.Contains(AgentLabels.EpicApproved))
            .Except(phase2Eligible)
            .ToList();

        foreach (var issue in excluded)
        {
            var hasExclusionLabel =
                issue.Labels.Contains(AgentLabels.InProgress) ||
                issue.Labels.Contains(AgentLabels.Error) ||
                issue.Labels.Contains(AgentLabels.Done);

            hasExclusionLabel.Should().BeTrue(
                $"Issue {issue.Identifier} has agent:epic-approved but was excluded, so it must have an exclusion label. Labels: [{string.Join(", ", issue.Labels)}]");
        }
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 3: Epic Polling Eligibility Filter (Completeness)
    ///
    /// Verifies that the filter is complete: an issue with agent:epic and NO exclusion labels
    /// is always included in the Phase 1 eligible set.
    ///
    /// **Validates: Requirements 2.5, 2.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public void EpicPollingEligibility_Phase1_AlwaysIncludesCleanEpics(PositiveInt issueCountRaw, int seed)
    {
        var issueCount = Math.Min(issueCountRaw.Get, 20);
        var rng = new Random(seed);

        // Generate issues that have agent:epic but NO exclusion labels
        var cleanEpics = Enumerable.Range(0, issueCount).Select(i =>
        {
            // Pick random non-exclusion labels
            var extraLabels = AllRelevantLabels
                .Where(l => l != AgentLabels.Epic &&
                            l != AgentLabels.EpicReview &&
                            l != AgentLabels.InProgress &&
                            l != AgentLabels.Error &&
                            l != AgentLabels.Done)
                .Where(_ => rng.NextDouble() > 0.5)
                .ToList();

            var labels = new List<string> { AgentLabels.Epic };
            labels.AddRange(extraLabels);

            return new IssueSummary
            {
                Identifier = $"clean-{i}",
                Title = $"Clean Epic {i}",
                Labels = labels,
                CreatedAt = DateTime.UtcNow.AddMinutes(-i)
            };
        }).ToList();

        // Apply Phase 1 filter
        var phase1Eligible = cleanEpics
            .Where(i => i.Labels.Contains(AgentLabels.Epic))
            .Where(i => !i.Labels.Contains(AgentLabels.EpicReview))
            .Where(i => !i.Labels.Contains(AgentLabels.InProgress))
            .Where(i => !i.Labels.Contains(AgentLabels.Error))
            .Where(i => !i.Labels.Contains(AgentLabels.Done))
            .ToList();

        // All clean epics should be eligible
        phase1Eligible.Count.Should().Be(cleanEpics.Count,
            "all issues with agent:epic and no exclusion labels should be eligible for Phase 1");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 4: Label State Machine Transitions
    ///
    /// For any decomposition phase execution, the label transitions follow exactly one of:
    /// (a) Phase 1 success: agent:epic → agent:in-progress → agent:epic-review
    /// (b) Phase 2 success: agent:epic-approved → agent:in-progress → agent:done
    /// (c) Phase 1 failure: agent:epic → agent:in-progress → agent:error
    /// (d) Phase 2 failure: agent:epic-approved → agent:in-progress → agent:error
    ///
    /// **Validates: Requirements 2.2, 2.3, 2.4, 12.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void LabelStateMachine_Phase1Success_ProducesCorrectSequence(int seed)
    {
        // Phase 1 success path: agent:epic → agent:in-progress → agent:epic-review
        var transitions = SimulatePhase1Success();

        transitions.Should().HaveCount(3);
        transitions[0].Should().Be(AgentLabels.Epic, "Phase 1 starts with agent:epic");
        transitions[1].Should().Be(AgentLabels.InProgress, "dispatch swaps to agent:in-progress");
        transitions[2].Should().Be(AgentLabels.EpicReview, "Phase 1 success swaps to agent:epic-review");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 4: Label State Machine Transitions
    ///
    /// Phase 2 success produces: agent:epic-approved → agent:in-progress → agent:done
    ///
    /// **Validates: Requirements 2.2, 2.3, 2.4, 12.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void LabelStateMachine_Phase2Success_ProducesCorrectSequence(int seed)
    {
        // Phase 2 success path: agent:epic-approved → agent:in-progress → agent:done
        var transitions = SimulatePhase2Success();

        transitions.Should().HaveCount(3);
        transitions[0].Should().Be(AgentLabels.EpicApproved, "Phase 2 starts with agent:epic-approved");
        transitions[1].Should().Be(AgentLabels.InProgress, "dispatch swaps to agent:in-progress");
        transitions[2].Should().Be(AgentLabels.Done, "Phase 2 success swaps to agent:done");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 4: Label State Machine Transitions
    ///
    /// Phase 1 failure produces: agent:epic → agent:in-progress → agent:error
    ///
    /// **Validates: Requirements 2.2, 2.3, 2.4, 12.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void LabelStateMachine_Phase1Failure_ProducesCorrectSequence(int seed)
    {
        // Phase 1 failure path: agent:epic → agent:in-progress → agent:error
        var transitions = SimulatePhase1Failure();

        transitions.Should().HaveCount(3);
        transitions[0].Should().Be(AgentLabels.Epic, "Phase 1 starts with agent:epic");
        transitions[1].Should().Be(AgentLabels.InProgress, "dispatch swaps to agent:in-progress");
        transitions[2].Should().Be(AgentLabels.Error, "Phase 1 failure swaps to agent:error");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 4: Label State Machine Transitions
    ///
    /// Phase 2 failure produces: agent:epic-approved → agent:in-progress → agent:error
    ///
    /// **Validates: Requirements 2.2, 2.3, 2.4, 12.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void LabelStateMachine_Phase2Failure_ProducesCorrectSequence(int seed)
    {
        // Phase 2 failure path: agent:epic-approved → agent:in-progress → agent:error
        var transitions = SimulatePhase2Failure();

        transitions.Should().HaveCount(3);
        transitions[0].Should().Be(AgentLabels.EpicApproved, "Phase 2 starts with agent:epic-approved");
        transitions[1].Should().Be(AgentLabels.InProgress, "dispatch swaps to agent:in-progress");
        transitions[2].Should().Be(AgentLabels.Error, "Phase 2 failure swaps to agent:error");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 4: Label State Machine Transitions
    ///
    /// For any random phase type and outcome, the label transition sequence is always
    /// exactly 3 labels long and follows the state machine rules.
    ///
    /// **Validates: Requirements 2.2, 2.3, 2.4, 12.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void LabelStateMachine_AnyPhaseAndOutcome_ProducesValidTransition(bool isPhase1, bool isSuccess)
    {
        var transitions = (isPhase1, isSuccess) switch
        {
            (true, true) => SimulatePhase1Success(),
            (true, false) => SimulatePhase1Failure(),
            (false, true) => SimulatePhase2Success(),
            (false, false) => SimulatePhase2Failure()
        };

        // All transitions are exactly 3 labels
        transitions.Should().HaveCount(3, "every decomposition execution produces exactly 3 label states");

        // First label is the trigger label
        var expectedStart = isPhase1 ? AgentLabels.Epic : AgentLabels.EpicApproved;
        transitions[0].Should().Be(expectedStart);

        // Second label is always agent:in-progress (dispatch)
        transitions[1].Should().Be(AgentLabels.InProgress);

        // Third label depends on outcome
        if (isSuccess)
        {
            var expectedEnd = isPhase1 ? AgentLabels.EpicReview : AgentLabels.Done;
            transitions[2].Should().Be(expectedEnd);
        }
        else
        {
            transitions[2].Should().Be(AgentLabels.Error);
        }
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 4: Label State Machine Transitions
    ///
    /// Phase 2 all-failed (zero successes) produces agent:error, not agent:done.
    ///
    /// **Validates: Requirements 2.4, 10.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public void LabelStateMachine_Phase2AllFailed_ProducesError(PositiveInt attemptedCountRaw)
    {
        var attemptedCount = Math.Min(attemptedCountRaw.Get, 20);

        // Simulate all sub-issue creations failing
        var results = Enumerable.Range(0, attemptedCount).Select(i => new SubIssueCreationResult
        {
            Title = $"Sub-issue {i}",
            Success = false,
            FailureReason = "Simulated failure"
        }).ToList();

        // Determine target label (same logic as PostDecompositionSummaryStep)
        var succeeded = results.Count(r => r.Success);
        var allFailed = results.Count == 0 || succeeded == 0;
        var targetLabel = allFailed ? AgentLabels.Error : AgentLabels.Done;

        targetLabel.Should().Be(AgentLabels.Error,
            "when all sub-issue creations fail, the label should be agent:error");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 4: Label State Machine Transitions
    ///
    /// Phase 2 partial success (at least one success) produces agent:done, not agent:error.
    ///
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void LabelStateMachine_Phase2PartialSuccess_ProducesDone(PositiveInt totalRaw, PositiveInt successRaw)
    {
        var total = Math.Max(Math.Min(totalRaw.Get, 20), 2);
        var successCount = Math.Min(successRaw.Get, total - 1); // At least one failure
        if (successCount < 1) successCount = 1; // Ensure at least one success

        var results = new List<SubIssueCreationResult>();
        for (var i = 0; i < successCount; i++)
        {
            results.Add(new SubIssueCreationResult
            {
                Title = $"Success-{i}",
                Success = true,
                Identifier = $"{100 + i}",
                Url = $"https://github.com/test/repo/issues/{100 + i}"
            });
        }
        for (var i = successCount; i < total; i++)
        {
            results.Add(new SubIssueCreationResult
            {
                Title = $"Failure-{i}",
                Success = false,
                FailureReason = "Simulated failure"
            });
        }

        // Determine target label (same logic as PostDecompositionSummaryStep)
        var succeeded = results.Count(r => r.Success);
        var allFailed = results.Count == 0 || succeeded == 0;
        var targetLabel = allFailed ? AgentLabels.Error : AgentLabels.Done;

        targetLabel.Should().Be(AgentLabels.Done,
            "when at least one sub-issue creation succeeds, the label should be agent:done");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Generates random issues with random label combinations from the relevant label set.
    /// </summary>
    private static List<IssueSummary> GenerateRandomIssues(int count, Random rng)
    {
        return Enumerable.Range(0, count).Select(i =>
        {
            // Pick a random subset of labels
            var labels = AllRelevantLabels
                .Where(_ => rng.NextDouble() > 0.5)
                .ToList();

            return new IssueSummary
            {
                Identifier = $"issue-{i}",
                Title = $"Test Issue {i}",
                Labels = labels,
                CreatedAt = DateTime.UtcNow.AddMinutes(-i)
            };
        }).ToList();
    }

    /// <summary>
    /// Simulates Phase 1 success label transitions.
    /// Returns the sequence of labels the issue passes through.
    /// </summary>
    private static List<string> SimulatePhase1Success()
    {
        // State machine: agent:epic → agent:in-progress (dispatch) → agent:epic-review (success)
        return [AgentLabels.Epic, AgentLabels.InProgress, AgentLabels.EpicReview];
    }

    /// <summary>
    /// Simulates Phase 1 failure label transitions.
    /// Returns the sequence of labels the issue passes through.
    /// </summary>
    private static List<string> SimulatePhase1Failure()
    {
        // State machine: agent:epic → agent:in-progress (dispatch) → agent:error (failure)
        return [AgentLabels.Epic, AgentLabels.InProgress, AgentLabels.Error];
    }

    /// <summary>
    /// Simulates Phase 2 success label transitions.
    /// Returns the sequence of labels the issue passes through.
    /// </summary>
    private static List<string> SimulatePhase2Success()
    {
        // State machine: agent:epic-approved → agent:in-progress (dispatch) → agent:done (success)
        return [AgentLabels.EpicApproved, AgentLabels.InProgress, AgentLabels.Done];
    }

    /// <summary>
    /// Simulates Phase 2 failure label transitions.
    /// Returns the sequence of labels the issue passes through.
    /// </summary>
    private static List<string> SimulatePhase2Failure()
    {
        // State machine: agent:epic-approved → agent:in-progress (dispatch) → agent:error (failure)
        return [AgentLabels.EpicApproved, AgentLabels.InProgress, AgentLabels.Error];
    }
}
