using CodingAgentWebUI.Pipeline.Models;
using AwesomeAssertions;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class PipelineConfigurationValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ClosedLoopMaxConsecutivePollFailures_RejectsValuesLessThanOne(int value)
    {
        var act = () => new PipelineConfiguration { ClosedLoopMaxConsecutivePollFailures = value };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ClosedLoopMaxConsecutivePollFailures");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(100)]
    public void ClosedLoopMaxConsecutivePollFailures_AcceptsValidValues(int value)
    {
        var config = new PipelineConfiguration { ClosedLoopMaxConsecutivePollFailures = value };
        config.ClosedLoopMaxConsecutivePollFailures.Should().Be(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ClosedLoopMaxPagesToFetch_RejectsValuesLessThanOne(int value)
    {
        var act = () => new PipelineConfiguration { ClosedLoopMaxPagesToFetch = value };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ClosedLoopMaxPagesToFetch");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void ClosedLoopMaxPagesToFetch_AcceptsValidValues(int value)
    {
        var config = new PipelineConfiguration { ClosedLoopMaxPagesToFetch = value };
        config.ClosedLoopMaxPagesToFetch.Should().Be(value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void AnalysisCommitThreshold_RejectsNegativeValues(int value)
    {
        var act = () => new PipelineConfiguration { AnalysisCommitThreshold = value };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("AnalysisCommitThreshold");
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(5000)]
    public void AnalysisCommitThreshold_RejectsValuesAbove1000(int value)
    {
        var act = () => new PipelineConfiguration { AnalysisCommitThreshold = value };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("AnalysisCommitThreshold");
    }

    [Fact]
    public void AnalysisCommitThreshold_AcceptsZero()
    {
        var config = new PipelineConfiguration { AnalysisCommitThreshold = 0 };
        config.AnalysisCommitThreshold.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    public void AnalysisCommitThreshold_AcceptsValidValues(int value)
    {
        var config = new PipelineConfiguration { AnalysisCommitThreshold = value };
        config.AnalysisCommitThreshold.Should().Be(value);
    }

    // ── PipelineRunRetentionCount ──────────────────────────────────────────────

    [Fact]
    public void PipelineRunRetentionCount_WhenSetToZero_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new PipelineConfiguration { PipelineRunRetentionCount = 0 };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("PipelineRunRetentionCount");
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-10)]
    [InlineData(-100)]
    public void PipelineRunRetentionCount_WhenSetToInvalidNegativeValue_ThrowsArgumentOutOfRangeException(int value)
    {
        var act = () => new PipelineConfiguration { PipelineRunRetentionCount = value };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("PipelineRunRetentionCount");
    }

    [Fact]
    public void PipelineRunRetentionCount_WhenSetToMinusOne_IsAccepted()
    {
        var config = new PipelineConfiguration { PipelineRunRetentionCount = -1 };
        config.PipelineRunRetentionCount.Should().Be(-1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10000)]
    public void PipelineRunRetentionCount_WhenSetToPositiveValue_IsAccepted(int value)
    {
        var config = new PipelineConfiguration { PipelineRunRetentionCount = value };
        config.PipelineRunRetentionCount.Should().Be(value);
    }

    [Fact]
    public void PipelineRunRetentionCount_DefaultIsMinusOne()
    {
        var config = new PipelineConfiguration();
        config.PipelineRunRetentionCount.Should().Be(-1);
    }

    // ── WorkItemRetentionCount ─────────────────────────────────────────────────

    [Fact]
    public void WorkItemRetentionCount_WhenSetToZero_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new PipelineConfiguration { WorkItemRetentionCount = 0 };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("WorkItemRetentionCount");
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-10)]
    [InlineData(-100)]
    public void WorkItemRetentionCount_WhenSetToInvalidNegativeValue_ThrowsArgumentOutOfRangeException(int value)
    {
        var act = () => new PipelineConfiguration { WorkItemRetentionCount = value };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("WorkItemRetentionCount");
    }

    [Fact]
    public void WorkItemRetentionCount_WhenSetToMinusOne_IsAccepted()
    {
        var config = new PipelineConfiguration { WorkItemRetentionCount = -1 };
        config.WorkItemRetentionCount.Should().Be(-1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(5000)]
    public void WorkItemRetentionCount_WhenSetToPositiveValue_IsAccepted(int value)
    {
        var config = new PipelineConfiguration { WorkItemRetentionCount = value };
        config.WorkItemRetentionCount.Should().Be(value);
    }

    [Fact]
    public void WorkItemRetentionCount_DefaultIsMinusOne()
    {
        var config = new PipelineConfiguration();
        config.WorkItemRetentionCount.Should().Be(-1);
    }

    // ── DbRetentionSweepInterval ───────────────────────────────────────────────

    [Fact]
    public void DbRetentionSweepInterval_DefaultIsOneDay()
    {
        var config = new PipelineConfiguration();
        config.DbRetentionSweepInterval.Should().Be(TimeSpan.FromHours(24));
    }
}

public class RateLimitExceededExceptionTests
{
    [Fact]
    public void ParameterlessConstructor_SetsDefaultMessage()
    {
        var ex = new RateLimitExceededException();
        ex.Message.Should().Contain("rate limit exceeded");
    }

    [Fact]
    public void StringConstructor_SetsMessage()
    {
        var ex = new RateLimitExceededException("custom message");
        ex.Message.Should().Be("custom message");
    }

    [Fact]
    public void StringAndExceptionConstructor_SetsMessageAndInner()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new RateLimitExceededException("custom", inner);
        ex.Message.Should().Be("custom");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void ResetAtConstructor_SetsResetAtAndMessage()
    {
        var resetAt = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var ex = new RateLimitExceededException(resetAt);
        ex.ResetAt.Should().Be(resetAt);
        ex.Message.Should().Contain("2025-01-01");
    }
}

public class AnalysisIncompleteExceptionTests
{
    [Fact]
    public void ParameterlessConstructor_SetsDefaultMessage()
    {
        var ex = new AnalysisIncompleteException();
        ex.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void StringConstructor_SetsMessage()
    {
        var ex = new AnalysisIncompleteException("analysis failed");
        ex.Message.Should().Be("analysis failed");
    }

    [Fact]
    public void StringAndExceptionConstructor_SetsMessageAndInner()
    {
        var inner = new IOException("disk full");
        var ex = new AnalysisIncompleteException("analysis failed", inner);
        ex.Message.Should().Be("analysis failed");
        ex.InnerException.Should().BeSameAs(inner);
    }
}
