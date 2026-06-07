using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

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
    [InlineData(PipelineStep.CreatingPullRequest, "Creating Pull Request")]
    [InlineData(PipelineStep.Completed, "Completed")]
    [InlineData(PipelineStep.Failed, "Failed")]
    [InlineData(PipelineStep.Cancelled, "Cancelled")]
    public void ToDisplayName_KnownValues_ReturnExpectedLabel(PipelineStep step, string expected)
    {
        Assert.Equal(expected, step.ToDisplayName());
    }
}
