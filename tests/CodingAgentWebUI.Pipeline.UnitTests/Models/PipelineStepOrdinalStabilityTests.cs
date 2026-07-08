using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Guards the integer ordinals of <see cref="PipelineStep"/> values.
/// These ordinals are serialized as integers over MessagePack (SignalR wire protocol)
/// in <c>ActiveJobState.CurrentStep</c>, <c>HeartbeatMessage.CurrentStep</c>, and
/// <c>JobCompletionPayload.FinalStep</c>. If ordinals shift, the orchestrator and agents
/// silently misinterpret each other's step reports, causing incorrect pipeline state.
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
    public void OrdinalValue_MatchesExpected(PipelineStep step, int expectedOrdinal)
    {
        ((int)step).Should().Be(expectedOrdinal);
    }

    [Fact]
    public void EnumHasExactly30Members()
    {
        // Guard against adding new members without updating this test.
        // If a new step is added, the developer must also add a corresponding
        // [InlineData] row to the Theory above and update this count.
        Enum.GetValues<PipelineStep>().Should().HaveCount(30);
    }

    [Fact]
    public void MaxOrdinalIs29()
    {
        // Documents the next available ordinal value (30).
        // When adding a new PipelineStep, assign it = 30 (or the next sequential value).
        Enum.GetValues<PipelineStep>().Cast<int>().Max().Should().Be(29);
    }
}
