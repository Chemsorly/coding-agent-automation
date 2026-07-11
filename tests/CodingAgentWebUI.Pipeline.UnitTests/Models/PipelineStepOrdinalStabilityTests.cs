using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Guards the integer ordinals of <see cref="PipelineStep"/> values.
/// These ordinals are serialized as integers over MessagePack (SignalR wire protocol)
/// in ActiveJobState.CurrentStep, HeartbeatMessage.CurrentStep, and JobCompletionPayload.FinalStep.
/// If these ordinals ever shift, agents and the orchestrator will misinterpret step values,
/// causing silent pipeline state corruption.
/// </summary>
public sealed class PipelineStepOrdinalStabilityTests
{
    [Theory]
    [InlineData(PipelineStep.Created, 0)]
    [InlineData(PipelineStep.CloningRepository, 1)]
    [InlineData(PipelineStep.SyncingBrainRepoPreRun, 2)]
    [InlineData(PipelineStep.CreatingBranch, 3)]
    [InlineData(PipelineStep.VerifyingBaseline, 4)]
    [InlineData(PipelineStep.AnalyzingCode, 5)]
    [InlineData(PipelineStep.ReviewingAnalysis, 6)]
    [InlineData(PipelineStep.PostingAnalysis, 7)]
    [InlineData(PipelineStep.GeneratingCode, 8)]
    [InlineData(PipelineStep.ReviewingCode, 9)]
    [InlineData(PipelineStep.RunningQualityGates, 10)]
    [InlineData(PipelineStep.PreparingForPullRequest, 11)]
    [InlineData(PipelineStep.CreatingPullRequest, 12)]
    [InlineData(PipelineStep.GeneratingPrDescription, 13)]
    [InlineData(PipelineStep.ReflectingOnRun, 14)]
    [InlineData(PipelineStep.SyncingBrainRepoPostRun, 15)]
    [InlineData(PipelineStep.Completed, 16)]
    [InlineData(PipelineStep.Failed, 17)]
    [InlineData(PipelineStep.Cancelled, 18)]
    [InlineData(PipelineStep.ExtractingLinkedIssues, 19)]
    [InlineData(PipelineStep.PostingFindings, 20)]
    [InlineData(PipelineStep.DownloadingOpenIssues, 21)]
    [InlineData(PipelineStep.ExploringCodebase, 22)]
    [InlineData(PipelineStep.GeneratingPlan, 23)]
    [InlineData(PipelineStep.ReviewingPlan, 24)]
    [InlineData(PipelineStep.PostingPlan, 25)]
    [InlineData(PipelineStep.GeneratingSubIssues, 26)]
    [InlineData(PipelineStep.CreatingIssues, 27)]
    [InlineData(PipelineStep.PostingSummary, 28)]
    [InlineData(PipelineStep.RunningEnvironmentSetup, 29)]
    public void Member_HasExpectedOrdinal(PipelineStep step, int expectedOrdinal)
    {
        ((int)step).Should().Be(expectedOrdinal);
    }

    [Fact]
    public void EnumHasExactly30Members()
    {
        // Guard against adding new members without updating the ordinal stability test.
        // If a new step is added, this test forces the developer to add a corresponding
        // [InlineData] assertion above and verify the wire protocol is not broken.
        Enum.GetValues<PipelineStep>().Should().HaveCount(30);
    }

    [Fact]
    public void MaxOrdinalIs29()
    {
        // Documents the current maximum ordinal value.
        // New members MUST use the next sequential value (30, 31, ...).
        var maxOrdinal = Enum.GetValues<PipelineStep>().Cast<int>().Max();
        maxOrdinal.Should().Be(29);
    }
}
