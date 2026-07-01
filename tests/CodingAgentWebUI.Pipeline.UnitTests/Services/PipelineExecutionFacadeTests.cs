using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class PipelineExecutionFacadeTests
{
    [Fact]
    public void Constructor_ThrowsOnNullAgentExecution()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PipelineExecutionFacade(
                null!,
                Mock.Of<IQualityGateExecutor>(),
                Mock.Of<IQualityGateValidator>(),
                Mock.Of<IBrainSyncService>()));
    }

    [Fact]
    public void Constructor_ThrowsOnNullQualityGates()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PipelineExecutionFacade(
                Mock.Of<IAgentPhaseExecutor>(),
                null!,
                Mock.Of<IQualityGateValidator>(),
                Mock.Of<IBrainSyncService>()));
    }

    [Fact]
    public void Constructor_ThrowsOnNullBrainSync()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PipelineExecutionFacade(
                Mock.Of<IAgentPhaseExecutor>(),
                Mock.Of<IQualityGateExecutor>(),
                Mock.Of<IQualityGateValidator>(),
                null!));
    }

    [Fact]
    public void Constructor_AcceptsNullQualityGateValidator()
    {
        var facade = new PipelineExecutionFacade(
            Mock.Of<IAgentPhaseExecutor>(),
            Mock.Of<IQualityGateExecutor>(),
            null,
            Mock.Of<IBrainSyncService>());

        Assert.Null(facade.QualityGateValidator);
    }

    [Fact]
    // TODO: This test is tautological — it asserts auto-properties return constructor-assigned values,
    // which is guaranteed by C# compiler behavior. Consider replacing with a behavioral test.
    public void Properties_ExposeInjectedDependencies()
    {
        var agentExecution = Mock.Of<IAgentPhaseExecutor>();
        var qualityGates = Mock.Of<IQualityGateExecutor>();
        var validator = Mock.Of<IQualityGateValidator>();
        var brainSync = Mock.Of<IBrainSyncService>();

        var facade = new PipelineExecutionFacade(agentExecution, qualityGates, validator, brainSync);

        Assert.Same(agentExecution, facade.AgentExecution);
        Assert.Same(qualityGates, facade.QualityGates);
        Assert.Same(validator, facade.QualityGateValidator);
        Assert.Same(brainSync, facade.BrainSync);
    }
}
