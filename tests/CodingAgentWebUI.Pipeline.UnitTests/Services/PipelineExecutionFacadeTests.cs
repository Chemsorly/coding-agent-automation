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

}
