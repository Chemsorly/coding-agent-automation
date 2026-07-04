using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Tests for <see cref="PipelineStepFactory"/> — verifies the shared core implementation step sequence.
/// </summary>
public class PipelineStepFactoryTests
{
    [Fact]
    public void CreateImplementationCoreSteps_Returns9Steps()
    {
        var steps = PipelineStepFactory.CreateImplementationCoreSteps();

        steps.Should().HaveCount(9);
    }

    [Fact]
    public void CreateImplementationCoreSteps_StepsAreInCorrectOrder()
    {
        var steps = PipelineStepFactory.CreateImplementationCoreSteps();

        // TODO: Replace ContainInOrder with .Equal(...) to assert the exact sequence (not just relative order).
        // ContainInOrder is too weak — it won't catch unexpected steps inserted into the factory.
        steps.Select(s => s.GetType()).Should().ContainInOrder(
            typeof(DetectReworkStep),
            typeof(WritePrConversationContextStep),
            typeof(CreateBranchStep),
            typeof(VerifyBaselineStep),
            typeof(AnalyzeCodeStep),
            typeof(GenerateCodeStep),
            typeof(BrainPullBeforeWriteStep),
            typeof(ReviewCodeStep),
            typeof(RunQualityGatesStep));
    }

    [Fact]
    public void CreateImplementationCoreSteps_ReturnsNewListEachCall()
    {
        var steps1 = PipelineStepFactory.CreateImplementationCoreSteps();
        var steps2 = PipelineStepFactory.CreateImplementationCoreSteps();

        steps1.Should().NotBeSameAs(steps2);
    }
}
