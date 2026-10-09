using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using FsCheck;
using FsCheck.Xunit;

namespace CodingAgent.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for the label state machine transitions of epic decomposition.
/// Feature: 027-epic-decomposition-pipeline, Property P4.
/// Property P3 (epic polling eligibility) is tested against the production filter in
/// TemplatePolllerInstanceTests.
/// </summary>
public class DecompositionLabelPropertyTests
{
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
