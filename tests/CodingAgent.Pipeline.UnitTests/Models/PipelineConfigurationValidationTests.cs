using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Range checks of single settings. The setters accept any value so stored configurations always load;
/// <see cref="PipelineSettingsValidator"/> refuses values outside a setting's range when settings are saved.
/// </summary>
public class PipelineConfigurationValidationTests
{
    private static IReadOnlyList<string> Errors(PipelineConfiguration config) => PipelineSettingsValidator.Validate(config);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public void ClosedLoopMaxConsecutivePollFailures_OutsideOneToFifty_IsRejected(int value)
    {
        Errors(new PipelineConfiguration { ClosedLoopMaxConsecutivePollFailures = value })
            .Should().ContainSingle().Which.Should().StartWith("ClosedLoopMaxConsecutivePollFailures must be between 1 and 50");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    public void ClosedLoopMaxConsecutivePollFailures_ValidValues_AreAccepted(int value)
    {
        Errors(new PipelineConfiguration { ClosedLoopMaxConsecutivePollFailures = value }).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void ClosedLoopMaxPagesToFetch_OutsideOneToHundred_IsRejected(int value)
    {
        Errors(new PipelineConfiguration { ClosedLoopMaxPagesToFetch = value })
            .Should().ContainSingle().Which.Should().StartWith("ClosedLoopMaxPagesToFetch must be between 1 and 100");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void ClosedLoopMaxPagesToFetch_ValidValues_AreAccepted(int value)
    {
        Errors(new PipelineConfiguration { ClosedLoopMaxPagesToFetch = value }).Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void AnalysisCommitThreshold_OutsideZeroToThousand_IsRejected(int value)
    {
        Errors(new PipelineConfiguration { AnalysisCommitThreshold = value })
            .Should().ContainSingle().Which.Should().StartWith("AnalysisCommitThreshold must be between 0 and 1000");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(1000)]
    public void AnalysisCommitThreshold_ValidValues_AreAccepted(int value)
    {
        Errors(new PipelineConfiguration { AnalysisCommitThreshold = value }).Should().BeEmpty();
    }

    [Fact]
    public void OutOfRangeValue_IsStored_SoAStoredConfigurationStillLoads()
    {
        var config = new PipelineConfiguration { AnalysisCommitThreshold = 5000 };

        config.AnalysisCommitThreshold.Should().Be(5000);
    }

    // ── AgentTimeout validation ─────────────────────────────────────────────────

    [Fact]
    public void AgentTimeout_Zero_NormalizesToDefault()
    {
        // Zero was a legal persisted value before validation existed; it becomes the default, so stored rows keep working.
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.Zero };
        config.AgentTimeout.Should().Be(PipelineConstants.DefaultAgentTimeout,
            "a zero AgentTimeout must be normalized to the default (30 minutes)");
    }

    [Fact]
    public void AgentTimeout_Zero_JsonDeserialization_NormalizesToDefault()
    {
        // Exercises the deserialization path used by PostgresConfigurationStore.
        const string json = """{"AgentTimeout":"00:00:00"}""";

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);

        config.Should().NotBeNull();
        config!.AgentTimeout.Should().Be(PipelineConstants.DefaultAgentTimeout,
            "a zero AgentTimeout loaded from JSON must be normalized to the default (30 minutes)");
    }

    [Fact]
    public void AgentTimeout_NullJson_NormalizesToDefault()
    {
        // TimeSpanJsonConverter.Read returns TimeSpan.Zero for a null JSON value, which the setter turns into the default.
        const string json = """{"AgentTimeout":null}""";

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);

        config.Should().NotBeNull();
        config!.AgentTimeout.Should().Be(PipelineConstants.DefaultAgentTimeout);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(59)]
    [InlineData(86_401)]
    public void AgentTimeout_OutsideOneMinuteToOneDay_IsRejected(int seconds)
    {
        Errors(new PipelineConfiguration { AgentTimeout = TimeSpan.FromSeconds(seconds) })
            .Should().ContainSingle().Which.Should().StartWith("AgentTimeout must be between 00:01:00 and 1.00:00:00");
    }

    [Fact]
    public void AgentTimeout_PositiveValue_Accepted()
    {
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(30) };

        config.AgentTimeout.Should().Be(TimeSpan.FromMinutes(30));
        Errors(config).Should().BeEmpty();
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
