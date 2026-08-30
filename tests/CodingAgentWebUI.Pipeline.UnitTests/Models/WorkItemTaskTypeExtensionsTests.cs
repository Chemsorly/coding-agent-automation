using System.Diagnostics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="WorkItemTaskTypeExtensions.ToDefaultRunType"/>.
/// </summary>
public sealed class WorkItemTaskTypeExtensionsTests
{
    [Theory]
    [InlineData(WorkItemTaskType.Implementation, PipelineRunType.Implementation)]
    [InlineData(WorkItemTaskType.Review,         PipelineRunType.Review)]
    [InlineData(WorkItemTaskType.Decomposition,  PipelineRunType.DecompositionAnalysis)]
    [InlineData(WorkItemTaskType.Consolidation,  PipelineRunType.Consolidation)]
    public void ToDefaultRunType_KnownValues_ReturnExpectedRunType(
        WorkItemTaskType taskType, PipelineRunType expectedRunType)
    {
        taskType.ToDefaultRunType().Should().Be(expectedRunType,
            because: $"WorkItemTaskType.{taskType} must map to PipelineRunType.{expectedRunType}");
    }

    [Fact]
    public void ToDefaultRunType_UnknownValue_ThrowsUnreachableException()
    {
        // Cast an out-of-range integer to simulate a future enum addition
        // that has not yet been handled in the switch expression.
        var unknownTaskType = (WorkItemTaskType)999;

        var act = () => unknownTaskType.ToDefaultRunType();

        act.Should().Throw<UnreachableException>(
            because: "an unrecognised WorkItemTaskType must not silently fall back to Implementation");
    }

    [Fact]
    public void ToDefaultRunType_AllCurrentEnumValues_AreMapped()
    {
        // Guard: if a new WorkItemTaskType member is added without updating ToDefaultRunType,
        // this test will catch it at the test-run level.
        foreach (var taskType in Enum.GetValues<WorkItemTaskType>())
        {
            var act = () => taskType.ToDefaultRunType();
            act.Should().NotThrow(because: $"WorkItemTaskType.{taskType} must have a mapping in ToDefaultRunType");
        }
    }
}
