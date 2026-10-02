using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

public class PipelineStepExtensionsTests
{
    [Fact]
    public void ToDisplayName_AllEnumValues_ReturnNonEmptyString()
    {
        foreach (var step in Enum.GetValues<PipelineStep>())
        {
            var result = step.ToDisplayName();
            Assert.False(string.IsNullOrEmpty(result), $"ToDisplayName() returned null/empty for {step}");
        }
    }

    [Theory]
    [InlineData(PipelineStep.Created, "Pipeline Created")]
    [InlineData(PipelineStep.CloningRepository, "Cloning Repository")]
    [InlineData(PipelineStep.RunningEnvironmentSetup, "Environment Setup")]
    [InlineData(PipelineStep.SyncingBrainRepoPreRun, "Loading Brain Context")]
    [InlineData(PipelineStep.CreatingBranch, "Creating Branch")]
    [InlineData(PipelineStep.VerifyingBaseline, "Verifying Baseline")]
    [InlineData(PipelineStep.AnalyzingCode, "Analyzing Code")]
    [InlineData(PipelineStep.GeneratingCode, "Generating Code")]
    [InlineData(PipelineStep.PreparingForPullRequest, "Preparing for Pull Request")]
    [InlineData(PipelineStep.FinalizingPullRequest, "Finalizing Pull Request")]
    [InlineData(PipelineStep.Completed, "Completed")]
    [InlineData(PipelineStep.Failed, "Failed")]
    [InlineData(PipelineStep.Cancelled, "Cancelled")]
    public void ToDisplayName_KnownValues_ReturnExpectedLabel(PipelineStep step, string expected)
    {
        Assert.Equal(expected, step.ToDisplayName());
    }

    #region IsQualityGateExitState

    [Theory]
    [InlineData(PipelineStep.Failed)]
    [InlineData(PipelineStep.ConflictRestart)]
    [InlineData(PipelineStep.PrMerged)]
    [InlineData(PipelineStep.PrClosed)]
    public void IsQualityGateExitState_ExitStates_ReturnsTrue(PipelineStep step)
    {
        Assert.True(step.IsQualityGateExitState(),
            $"{step} must be a quality-gate exit state");
    }

    [Theory]
    [InlineData(PipelineStep.Completed)]
    [InlineData(PipelineStep.Cancelled)]
    [InlineData(PipelineStep.RunningQualityGates)]
    [InlineData(PipelineStep.GeneratingCode)]
    [InlineData(PipelineStep.Created)]
    public void IsQualityGateExitState_NonExitStates_ReturnsFalse(PipelineStep step)
    {
        Assert.False(step.IsQualityGateExitState(),
            $"{step} must NOT be a quality-gate exit state");
    }

    /// <summary>
    /// Verifies that <see cref="PipelineStepExtensions.IsQualityGateExitState"/> is a strict
    /// subset of <see cref="PipelineStepExtensions.IsTerminal"/>: ConflictRestart is a quality-gate
    /// exit state but Completed (a terminal state) is not, demonstrating the two methods are not
    /// interchangeable.
    /// </summary>
    [Fact]
    public void IsQualityGateExitState_IsStrictSubsetOfIsTerminal_DoesNotIncludeCompletedOrCancelled()
    {
        // ConflictRestart: in both sets
        Assert.True(PipelineStep.ConflictRestart.IsQualityGateExitState());
        Assert.True(PipelineStep.ConflictRestart.IsTerminal());

        // Completed: terminal but NOT a quality-gate exit state
        Assert.False(PipelineStep.Completed.IsQualityGateExitState(),
            "Completed must not be a quality-gate exit state — IsQualityGateExitState is narrower than IsTerminal");
        Assert.True(PipelineStep.Completed.IsTerminal());

        // Cancelled: terminal but NOT a quality-gate exit state
        Assert.False(PipelineStep.Cancelled.IsQualityGateExitState(),
            "Cancelled must not be a quality-gate exit state — IsQualityGateExitState is narrower than IsTerminal");
        Assert.True(PipelineStep.Cancelled.IsTerminal());
    }

    #endregion
}
