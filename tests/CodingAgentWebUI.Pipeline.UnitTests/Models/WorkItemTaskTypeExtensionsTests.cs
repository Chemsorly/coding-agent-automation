using System.Diagnostics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class WorkItemTaskTypeExtensionsTests
{
    [Theory]
    [InlineData(WorkItemTaskType.Implementation, PipelineRunType.Implementation)]
    [InlineData(WorkItemTaskType.Review, PipelineRunType.Review)]
    [InlineData(WorkItemTaskType.Decomposition, PipelineRunType.DecompositionAnalysis)]
    [InlineData(WorkItemTaskType.Consolidation, PipelineRunType.Consolidation)]
    public void ToDefaultRunType_ReturnsExpectedRunType(WorkItemTaskType taskType, PipelineRunType expected)
        => taskType.ToDefaultRunType().Should().Be(expected);

    [Fact]
    public void ToDefaultRunType_UnknownValue_ThrowsUnreachableException()
        => Assert.Throws<UnreachableException>(() => ((WorkItemTaskType)999).ToDefaultRunType());
}
