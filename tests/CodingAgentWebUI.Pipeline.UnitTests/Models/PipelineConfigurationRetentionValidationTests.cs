using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for the new retention-related properties on <see cref="PipelineConfiguration"/>.
/// Validates that <see cref="PipelineConfiguration.PipelineRunRetentionCount"/>,
/// <see cref="PipelineConfiguration.WorkItemRetentionCount"/>, and
/// <see cref="PipelineConfiguration.DbRetentionSweepInterval"/> enforce their invariants
/// at init time.
/// </summary>
public class PipelineConfigurationRetentionValidationTests
{
    // ── PipelineRunRetentionCount ───────────────────────────────────────

    [Fact]
    public void PipelineRunRetentionCount_MinusOne_IsValid()
    {
        // -1 = disabled (sentinel) — must not throw
        var act = () => new PipelineConfiguration { PipelineRunRetentionCount = -1 };
        act.Should().NotThrow();
    }

    [Fact]
    public void PipelineRunRetentionCount_PositiveInteger_IsValid()
    {
        var act = () => new PipelineConfiguration { PipelineRunRetentionCount = 100 };
        act.Should().NotThrow();
    }

    [Fact]
    public void PipelineRunRetentionCount_One_IsValid()
    {
        var act = () => new PipelineConfiguration { PipelineRunRetentionCount = 1 };
        act.Should().NotThrow();
    }

    [Fact]
    public void PipelineRunRetentionCount_Zero_Throws()
    {
        // 0 would delete every row per project on every sweep — explicitly rejected
        var act = () => new PipelineConfiguration { PipelineRunRetentionCount = 0 };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*PipelineRunRetentionCount*");
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public void PipelineRunRetentionCount_NegativeOtherThanMinusOne_Throws(int value)
    {
        // Only -1 is a valid negative value
        var act = () => new PipelineConfiguration { PipelineRunRetentionCount = value };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*PipelineRunRetentionCount*");
    }

    // ── WorkItemRetentionCount ──────────────────────────────────────────

    [Fact]
    public void WorkItemRetentionCount_MinusOne_IsValid()
    {
        var act = () => new PipelineConfiguration { WorkItemRetentionCount = -1 };
        act.Should().NotThrow();
    }

    [Fact]
    public void WorkItemRetentionCount_PositiveInteger_IsValid()
    {
        var act = () => new PipelineConfiguration { WorkItemRetentionCount = 500 };
        act.Should().NotThrow();
    }

    [Fact]
    public void WorkItemRetentionCount_Zero_Throws()
    {
        var act = () => new PipelineConfiguration { WorkItemRetentionCount = 0 };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*WorkItemRetentionCount*");
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-50)]
    public void WorkItemRetentionCount_NegativeOtherThanMinusOne_Throws(int value)
    {
        var act = () => new PipelineConfiguration { WorkItemRetentionCount = value };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*WorkItemRetentionCount*");
    }

    // ── DbRetentionSweepInterval ────────────────────────────────────────

    [Fact]
    public void DbRetentionSweepInterval_OneMinute_IsValid()
    {
        var act = () => new PipelineConfiguration { DbRetentionSweepInterval = TimeSpan.FromMinutes(1) };
        act.Should().NotThrow();
    }

    [Fact]
    public void DbRetentionSweepInterval_24Hours_IsValid()
    {
        var act = () => new PipelineConfiguration { DbRetentionSweepInterval = TimeSpan.FromHours(24) };
        act.Should().NotThrow();
    }

    [Fact]
    public void DbRetentionSweepInterval_59Seconds_Throws()
    {
        // Below the 1-minute minimum — would hammer the DB
        var act = () => new PipelineConfiguration { DbRetentionSweepInterval = TimeSpan.FromSeconds(59) };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*DbRetentionSweepInterval*");
    }

    [Fact]
    public void DbRetentionSweepInterval_Zero_Throws()
    {
        var act = () => new PipelineConfiguration { DbRetentionSweepInterval = TimeSpan.Zero };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*DbRetentionSweepInterval*");
    }

    // ── Default values ──────────────────────────────────────────────────

    [Fact]
    public void DefaultConfiguration_RetentionCountsAreDisabled()
    {
        var config = new PipelineConfiguration();
        config.PipelineRunRetentionCount.Should().Be(-1, "retention is opt-in, default must be disabled");
        config.WorkItemRetentionCount.Should().Be(-1, "retention is opt-in, default must be disabled");
    }

    [Fact]
    public void DefaultConfiguration_SweepIntervalIs24Hours()
    {
        var config = new PipelineConfiguration();
        config.DbRetentionSweepInterval.Should().Be(TimeSpan.FromHours(24));
    }
}
