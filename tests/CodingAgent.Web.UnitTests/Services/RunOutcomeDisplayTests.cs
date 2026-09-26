using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="RunOutcomeDisplay"/> — the shared presentation of a run's final step.
/// The terminal-like steps (ConflictRestart, PrMerged, PrClosed) used to be shown as "Running" on the
/// Runs list and Overview, and the Run page offered "Cancel Pipeline" on them.
/// </summary>
public class RunOutcomeDisplayTests
{
    public static TheoryData<PipelineStep> AllSteps()
    {
        var data = new TheoryData<PipelineStep>();
        foreach (var step in Enum.GetValues<PipelineStep>())
            data.Add(step);
        return data;
    }

    private static PipelineRunSummary Run(PipelineStep finalStep) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "1",
        IssueTitle = "t",
        FinalStep = finalStep,
        StartedAtOffset = DateTimeOffset.UtcNow,
    };

    [Theory]
    [MemberData(nameof(AllSteps))]
    public void Classify_IsRunning_ExactlyWhenTheStepIsNotTerminal(PipelineStep step)
    {
        var running = RunOutcomeDisplay.Classify(step) == RunOutcome.Running;

        running.Should().Be(!step.IsTerminal(),
            "every terminal step (PipelineStepExtensions.IsTerminal) must map to an outcome, so a new terminal step can't silently render as Running");
        RunOutcomeDisplay.IsActive(step).Should().Be(running);
    }

    [Theory]
    [InlineData(PipelineStep.Completed, RunOutcome.Succeeded, "Completed", "step-completed")]
    [InlineData(PipelineStep.PrMerged, RunOutcome.Succeeded, "Merged", "step-completed")]
    [InlineData(PipelineStep.Failed, RunOutcome.Failed, "Failed", "step-failed")]
    [InlineData(PipelineStep.Cancelled, RunOutcome.Cancelled, "Cancelled", "step-cancelled")]
    [InlineData(PipelineStep.PrClosed, RunOutcome.Cancelled, "Closed", "step-cancelled")]
    [InlineData(PipelineStep.ConflictRestart, RunOutcome.Restarted, "Restarted", "step-restart")]
    [InlineData(PipelineStep.ReviewingCode, RunOutcome.Running, "Running", "step-running")]
    public void TerminalAndLiveSteps_MapToOutcomeLabelAndBadge(PipelineStep step, RunOutcome outcome, string label, string badgeClass)
    {
        RunOutcomeDisplay.Classify(step).Should().Be(outcome);
        RunOutcomeDisplay.Label(step).Should().Be(label);
        RunOutcomeDisplay.BadgeClass(step).Should().Be(badgeClass);
    }

    [Fact]
    public void SuccessRate_IgnoresRestartedAndRunningRuns()
    {
        var runs = new[]
        {
            Run(PipelineStep.Completed),
            Run(PipelineStep.PrMerged),
            Run(PipelineStep.Failed),
            Run(PipelineStep.ConflictRestart),
            Run(PipelineStep.ConflictRestart),
            Run(PipelineStep.RunningQualityGates),
        };

        // 2 succeeded out of 3 decided runs; the restarts and the in-flight run don't count.
        RunOutcomeDisplay.SuccessRate(runs).Should().Be(67);
    }

    [Fact]
    public void SuccessRate_IsNull_WhenNoRunReachedAnOutcome()
    {
        RunOutcomeDisplay.SuccessRate([Run(PipelineStep.ConflictRestart), Run(PipelineStep.GeneratingCode)])
            .Should().BeNull();
        RunOutcomeDisplay.SuccessRate([]).Should().BeNull();
    }
}
