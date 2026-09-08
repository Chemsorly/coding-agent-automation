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

    // ── AgentTimeout validation ─────────────────────────────────────────────────

    [Fact]
    public void AgentTimeout_Zero_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new PipelineConfiguration { AgentTimeout = TimeSpan.Zero };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("AgentTimeout");
    }

    [Fact]
    public void AgentTimeout_Negative_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new PipelineConfiguration { AgentTimeout = TimeSpan.FromSeconds(-1) };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("AgentTimeout");
    }

    [Fact]
    public void AgentTimeout_PositiveValue_Accepted()
    {
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(30) };
        config.AgentTimeout.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void AgentTimeout_SmallPositiveValue_Accepted()
    {
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromSeconds(1) };
        config.AgentTimeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void AgentTimeout_DefaultValue_IsPositive()
    {
        // TODO [WARNING]: This assertion is too weak — any positive value passes, including 1ms.
        // The acceptance criterion requires the default to remain PipelineConstants.DefaultAgentTimeout
        // (30 min). Strengthen to: config.AgentTimeout.Should().Be(PipelineConstants.DefaultAgentTimeout).
        // (TestQualityReviewer review [WARNING] @ PipelineConfigurationValidationTests.cs:120)
        var config = new PipelineConfiguration();
        config.AgentTimeout.Should().BeGreaterThan(TimeSpan.Zero);
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
