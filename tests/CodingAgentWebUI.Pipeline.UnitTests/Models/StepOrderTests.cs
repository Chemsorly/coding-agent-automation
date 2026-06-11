using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class StepOrderTests
{
    [Theory]
    [InlineData(PipelineStep.Created, 0)]
    [InlineData(PipelineStep.CloningRepository, 1)]
    [InlineData(PipelineStep.RunningEnvironmentSetup, 2)]
    [InlineData(PipelineStep.SyncingBrainRepoPreRun, 3)]
    [InlineData(PipelineStep.CreatingBranch, 4)]
    [InlineData(PipelineStep.VerifyingBaseline, 5)]
    [InlineData(PipelineStep.AnalyzingCode, 6)]
    [InlineData(PipelineStep.ReviewingAnalysis, 7)]
    [InlineData(PipelineStep.PostingAnalysis, 8)]
    [InlineData(PipelineStep.GeneratingCode, 9)]
    [InlineData(PipelineStep.ReviewingCode, 10)]
    [InlineData(PipelineStep.RunningQualityGates, 11)]
    [InlineData(PipelineStep.PreparingForPullRequest, 12)]
    [InlineData(PipelineStep.CreatingPullRequest, 13)]
    [InlineData(PipelineStep.GeneratingPrDescription, 14)]
    [InlineData(PipelineStep.ReflectingOnRun, 15)]
    [InlineData(PipelineStep.SyncingBrainRepoPostRun, 16)]
    [InlineData(PipelineStep.Completed, 100)]
    public void GetOrder_ImplementationPipelineSteps_ReturnsCorrectOrder(PipelineStep step, int expectedOrder)
    {
        Assert.Equal(expectedOrder, StepOrder.GetOrder(step));
    }

    [Theory]
    [InlineData(PipelineStep.ExtractingLinkedIssues, 1)]
    [InlineData(PipelineStep.DownloadingOpenIssues, 2)]
    [InlineData(PipelineStep.ExploringCodebase, 3)]
    [InlineData(PipelineStep.GeneratingPlan, 4)]
    [InlineData(PipelineStep.ReviewingPlan, 5)]
    [InlineData(PipelineStep.PostingPlan, 6)]
    [InlineData(PipelineStep.GeneratingSubIssues, 7)]
    [InlineData(PipelineStep.CreatingIssues, 8)]
    [InlineData(PipelineStep.PostingSummary, 9)]
    [InlineData(PipelineStep.PostingFindings, 10)]
    public void GetOrder_DecompositionPipelineSteps_ReturnsCorrectOrder(PipelineStep step, int expectedOrder)
    {
        Assert.Equal(expectedOrder, StepOrder.GetOrder(step));
    }

    [Fact]
    public void GetOrder_UnknownStep_ReturnsNegativeOne()
    {
        // Failed and Cancelled are terminal states, not ordered pipeline steps
        Assert.Equal(-1, StepOrder.GetOrder(PipelineStep.Failed));
        Assert.Equal(-1, StepOrder.GetOrder(PipelineStep.Cancelled));
    }

    [Fact]
    public void GetOrder_ImplementationSteps_AreMonotonicallyIncreasing()
    {
        var implementationSteps = new[]
        {
            PipelineStep.Created,
            PipelineStep.CloningRepository,
            PipelineStep.RunningEnvironmentSetup,
            PipelineStep.SyncingBrainRepoPreRun,
            PipelineStep.CreatingBranch,
            PipelineStep.VerifyingBaseline,
            PipelineStep.AnalyzingCode,
            PipelineStep.ReviewingAnalysis,
            PipelineStep.PostingAnalysis,
            PipelineStep.GeneratingCode,
            PipelineStep.ReviewingCode,
            PipelineStep.RunningQualityGates,
            PipelineStep.PreparingForPullRequest,
            PipelineStep.CreatingPullRequest,
            PipelineStep.GeneratingPrDescription,
            PipelineStep.ReflectingOnRun,
            PipelineStep.SyncingBrainRepoPostRun,
            PipelineStep.Completed
        };

        for (var i = 1; i < implementationSteps.Length; i++)
        {
            var prev = StepOrder.GetOrder(implementationSteps[i - 1]);
            var curr = StepOrder.GetOrder(implementationSteps[i]);
            Assert.True(curr > prev,
                $"Expected {implementationSteps[i]} (order={curr}) > {implementationSteps[i - 1]} (order={prev})");
        }
    }

    [Fact]
    public void GetOrder_RunningEnvironmentSetup_HasOrder2_DespiteHighEnumOrdinal()
    {
        // RunningEnvironmentSetup has enum ordinal 29 but logical order 2
        var enumOrdinal = (int)PipelineStep.RunningEnvironmentSetup;
        var logicalOrder = StepOrder.GetOrder(PipelineStep.RunningEnvironmentSetup);

        Assert.Equal(29, enumOrdinal);
        Assert.Equal(2, logicalOrder);
    }

    [Fact]
    public void GetOrder_DecompositionSteps_DoNotInterfereWithImplementationOrdering()
    {
        // Decomposition steps share numeric order values (1-10) with implementation steps,
        // but they exist in a separate pipeline context. Verify that:
        // 1. Each pipeline's ordering is self-consistent
        // 2. Decomposition steps having the same order values doesn't corrupt implementation lookup
        var implementationStep = PipelineStep.CloningRepository; // order=1
        var decompositionStep = PipelineStep.ExtractingLinkedIssues; // also order=1

        // Both return their correct order independently
        Assert.Equal(1, StepOrder.GetOrder(implementationStep));
        Assert.Equal(1, StepOrder.GetOrder(decompositionStep));

        // Implementation pipeline ordering remains intact after querying decomposition steps
        Assert.True(StepOrder.GetOrder(PipelineStep.RunningEnvironmentSetup) > StepOrder.GetOrder(PipelineStep.CloningRepository));
        Assert.True(StepOrder.GetOrder(PipelineStep.GeneratingCode) > StepOrder.GetOrder(PipelineStep.AnalyzingCode));

        // Decomposition pipeline ordering remains intact after querying implementation steps
        Assert.True(StepOrder.GetOrder(PipelineStep.GeneratingPlan) > StepOrder.GetOrder(PipelineStep.ExploringCodebase));
        Assert.True(StepOrder.GetOrder(PipelineStep.CreatingIssues) > StepOrder.GetOrder(PipelineStep.GeneratingSubIssues));
    }
}
