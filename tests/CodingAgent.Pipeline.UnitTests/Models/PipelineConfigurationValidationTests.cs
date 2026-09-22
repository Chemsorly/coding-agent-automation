using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

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
    public void AgentTimeout_Zero_NormalizesToDefault()
    {
        // Zero was a legal persisted value before the validation guard was added.
        // Direct construction with zero must clamp to the default rather than throw,
        // so that deserialization of stale DB rows does not crash config loading.
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.Zero };
        config.AgentTimeout.Should().Be(PipelineConstants.DefaultAgentTimeout,
            "a zero AgentTimeout must be normalized to the default (30 minutes)");
    }

    [Fact]
    public void AgentTimeout_Zero_JsonDeserialization_NormalizesToDefault()
    {
        // Exercises the exact deserialization path used by PostgresConfigurationStore:
        // JsonSerializer.Deserialize<PipelineConfiguration> with PipelineJsonOptions.Default
        // invokes the init setter, which previously threw on "00:00:00".
        const string json = """{"AgentTimeout":"00:00:00"}""";

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);

        config.Should().NotBeNull();
        config!.AgentTimeout.Should().Be(PipelineConstants.DefaultAgentTimeout,
            "a zero AgentTimeout loaded from JSON must be normalized to the default (30 minutes)");
    }

    [Fact]
    public void AgentTimeout_NullJson_NormalizesToDefault()
    {
        // TODO [WARNING]: This test cannot distinguish between "the init setter received TimeSpan.Zero
        // and clamped it to the default" and "the setter was never called and the field initializer
        // default was used." If TimeSpanJsonConverter is ever changed to skip the setter (e.g., returns
        // null and STJ falls back to the field default rather than calling init), this test would still
        // pass vacuously even if the zero-clamping logic were removed. The real normalization via
        // zero-input is already covered by AgentTimeout_Zero_NormalizesToDefault and
        // AgentTimeout_Zero_JsonDeserialization_NormalizesToDefault; consider whether this test adds
        // net coverage or only tests the converter's null-handling behavior.
        // (Correctness review [WARNING] @ PipelineConfigurationValidationTests.cs:117 |
        //  TestQualityReviewer review [WARNING] @ PipelineConfigurationValidationTests.cs:113)
        // TimeSpanJsonConverter.Read returns TimeSpan.Zero (default) for a null JSON value.
        // The init setter receives zero and must clamp it to the default rather than throw.
        const string json = """{"AgentTimeout":null}""";

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);

        config.Should().NotBeNull();
        config!.AgentTimeout.Should().Be(PipelineConstants.DefaultAgentTimeout,
            "a null AgentTimeout in JSON produces TimeSpan.Zero via TimeSpanJsonConverter.Read " +
            "and must be normalized to the default rather than throwing");
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
    public void AgentTimeout_DefaultValue_IsDefaultAgentTimeout()
    {
        var config = new PipelineConfiguration();
        config.AgentTimeout.Should().Be(PipelineConstants.DefaultAgentTimeout);
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
