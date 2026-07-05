using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Regression tests for <see cref="PipelineStepFactory"/> ensuring the shared core
/// implementation step sequence is correct and stable.
/// </summary>
public class PipelineStepFactoryTests
{
    // TODO: Add a regression test asserting that both PipelineOrchestrationService.BuildStepPipeline()
    // and LocalPipelineExecutor.BuildAgentStepPipeline() produce equivalent core step sequences
    // (the shared suffix after their respective prefixes). Currently these tests only verify the
    // factory in isolation; if a call site reverts to an inline list, these tests would still pass.
    [Fact]
    public void CreateCoreImplementationSteps_ReturnsExpectedStepSequence()
    {
        var steps = PipelineStepFactory.CreateCoreImplementationSteps();

        var stepNames = steps.Select(s => s.StepName).ToList();
        stepNames.Should().Equal(
            "DetectRework",
            "WritePrConversationContext",
            "CreateBranch",
            "VerifyBaseline",
            "AnalyzeCode",
            "GenerateCode",
            "BrainPullBeforeWrite",
            "ReviewCode",
            "RunQualityGates");
    }

    [Fact]
    public void CreateCoreImplementationSteps_ReturnsExpectedTypes()
    {
        var steps = PipelineStepFactory.CreateCoreImplementationSteps();

        steps.Should().HaveCount(9);
        steps[0].Should().BeOfType<DetectReworkStep>();
        steps[1].Should().BeOfType<WritePrConversationContextStep>();
        steps[2].Should().BeOfType<CreateBranchStep>();
        steps[3].Should().BeOfType<VerifyBaselineStep>();
        steps[4].Should().BeOfType<AnalyzeCodeStep>();
        steps[5].Should().BeOfType<GenerateCodeStep>();
        steps[6].Should().BeOfType<BrainPullBeforeWriteStep>();
        steps[7].Should().BeOfType<ReviewCodeStep>();
        steps[8].Should().BeOfType<RunQualityGatesStep>();
    }

    [Fact]
    public void CreateCoreImplementationSteps_ReturnsNewInstanceEachCall()
    {
        var steps1 = PipelineStepFactory.CreateCoreImplementationSteps();
        var steps2 = PipelineStepFactory.CreateCoreImplementationSteps();

        steps1.Should().NotBeSameAs(steps2);
    }
}
