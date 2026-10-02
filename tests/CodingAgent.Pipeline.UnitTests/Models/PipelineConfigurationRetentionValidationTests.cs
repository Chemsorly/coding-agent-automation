using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for the retention caps <see cref="PipelineConfiguration.PipelineRunRetentionCount"/> and
/// <see cref="PipelineConfiguration.WorkItemRetentionCount"/>: 0 or -1 (the default, from before 0 meant the same)
/// keeps every row, a positive number keeps that many per project, and the validator reports anything below -1.
/// </summary>
public class PipelineConfigurationRetentionValidationTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void RetentionCounts_InRange_AreValid(int value)
    {
        var config = new PipelineConfiguration { PipelineRunRetentionCount = value, WorkItemRetentionCount = value };

        PipelineSettingsValidator.Validate(config).Should().BeEmpty();
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public void RetentionCounts_BelowMinusOne_AreReported(int value)
    {
        var config = new PipelineConfiguration { PipelineRunRetentionCount = value, WorkItemRetentionCount = value };

        var errors = PipelineSettingsValidator.Validate(config);

        errors.Should().HaveCount(2);
        errors.Should().Contain(e => e.StartsWith(nameof(PipelineConfiguration.PipelineRunRetentionCount), StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith(nameof(PipelineConfiguration.WorkItemRetentionCount), StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultConfiguration_RetentionCountsKeepEveryRow()
    {
        var config = new PipelineConfiguration();

        config.PipelineRunRetentionCount.Should().Be(-1);
        config.WorkItemRetentionCount.Should().Be(-1);
    }
}
