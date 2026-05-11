using AwesomeAssertions;
using CodingAgentWebUI.Agent.Executors;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="HarnessSuggestionExecutor"/>.
/// Tests: skips agent call when no feedback, parses suggestions correctly.
/// </summary>
public class HarnessSuggestionExecutorTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IAgentProvider> _mockAgentProvider = new();

    private HarnessSuggestionExecutor CreateExecutor() => new(_mockLogger.Object);

    private static ConsolidationJobMessage CreateJob(string? feedbackDataJson = null) => new()
    {
        JobId = Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.HarnessSuggestions,
        TemplateId = null,
        TemplateName = null,
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration(),
        FeedbackDataJson = feedbackDataJson
    };

    [Fact]
    public async Task ExecuteAsync_NullFeedbackData_SkipsAgentCall()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(feedbackDataJson: null);

        // Act
        var result = await executor.ExecuteAsync(job, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No new feedback to analyze");

        // Agent should never be called
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyFeedbackData_SkipsAgentCall()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(feedbackDataJson: "   ");

        // Act
        var result = await executor.ExecuteAsync(job, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No new feedback to analyze");

        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ValidFeedback_ParsesSuggestionsCorrectly()
    {
        // Arrange
        var executor = CreateExecutor();
        var feedbackJson = """
            [
                {"outcome": "Success"},
                {"outcome": "Success"},
                {"outcome": "Failure"}
            ]
            """;
        var job = CreateJob(feedbackDataJson: feedbackJson);

        var agentOutput = """
            Here are my suggestions:
            ```json
            {
                "generatedAtUtc": "2026-07-01T12:00:00Z",
                "basedOnRunCount": 3,
                "successRate": 66.7,
                "suggestions": [
                    {
                        "text": "Add retry logic for flaky network calls",
                        "rationale": "2 out of 3 failures were network timeouts",
                        "frequency": 2
                    },
                    {
                        "text": "Increase agent timeout for large repos",
                        "rationale": "Timeout occurred on repos with >1000 files",
                        "frequency": 1
                    }
                ]
            }
            ```
            """;

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = agentOutput.Split('\n')
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.HarnessSuggestions.Should().NotBeNull();
        result.HarnessSuggestions!.Suggestions.Should().HaveCount(2);
        result.HarnessSuggestions.BasedOnRunCount.Should().Be(3);
        result.Summary.Should().Contain("2 suggestion");
    }

    [Fact]
    public async Task ExecuteAsync_AgentFails_ReturnsFailedResult()
    {
        // Arrange
        var executor = CreateExecutor();
        var feedbackJson = """[{"outcome": "Success"}]""";
        var job = CreateJob(feedbackDataJson: feedbackJson);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 1,
                OutputLines = ["Error: agent crashed"]
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exit");
    }

    // ── CalculateFeedbackMetrics Tests ──────────────────────────────────

    [Fact]
    public void CalculateFeedbackMetrics_AllSuccess_Returns100Percent()
    {
        // Arrange
        var json = """[{"outcome": "Success"}, {"outcome": "Success"}]""";

        // Act
        var (count, rate) = HarnessSuggestionExecutor.CalculateFeedbackMetrics(json);

        // Assert
        count.Should().Be(2);
        rate.Should().Be(100m);
    }

    [Fact]
    public void CalculateFeedbackMetrics_MixedOutcomes_CalculatesCorrectRate()
    {
        // Arrange
        var json = """[{"outcome": "Success"}, {"outcome": "Failure"}, {"outcome": "Success"}]""";

        // Act
        var (count, rate) = HarnessSuggestionExecutor.CalculateFeedbackMetrics(json);

        // Assert
        count.Should().Be(3);
        rate.Should().BeApproximately(66.67m, 0.01m);
    }

    [Fact]
    public void CalculateFeedbackMetrics_EmptyArray_ReturnsZeros()
    {
        // Arrange
        var json = "[]";

        // Act
        var (count, rate) = HarnessSuggestionExecutor.CalculateFeedbackMetrics(json);

        // Assert
        count.Should().Be(0);
        rate.Should().Be(0m);
    }

    [Fact]
    public void CalculateFeedbackMetrics_InvalidJson_ReturnsZeros()
    {
        // Arrange
        var json = "not valid json at all";

        // Act
        var (count, rate) = HarnessSuggestionExecutor.CalculateFeedbackMetrics(json);

        // Assert
        count.Should().Be(0);
        rate.Should().Be(0m);
    }

    // ── ParseSuggestions Tests ───────────────────────────────────────────

    [Fact]
    public void ParseSuggestions_FencedJsonBlock_ParsesCorrectly()
    {
        // Arrange
        var responseText = """
            Here are my suggestions:
            ```json
            {
                "generatedAtUtc": "2026-07-01T00:00:00Z",
                "basedOnRunCount": 5,
                "successRate": 80.0,
                "suggestions": [
                    {"text": "Improve prompts", "rationale": "Low success rate", "frequency": 3}
                ]
            }
            ```
            """;

        // Act
        var result = HarnessSuggestionExecutor.ParseSuggestions(responseText);

        // Assert
        result.Should().NotBeNull();
        result!.BasedOnRunCount.Should().Be(5);
        result.SuccessRate.Should().Be(80.0m);
        result.Suggestions.Should().HaveCount(1);
        result.Suggestions[0].Text.Should().Be("Improve prompts");
        result.Suggestions[0].Frequency.Should().Be(3);
    }

    [Fact]
    public void ParseSuggestions_NoJsonBlock_ReturnsNull()
    {
        // Arrange
        var responseText = "I couldn't find any patterns in the feedback data.";

        // Act
        var result = HarnessSuggestionExecutor.ParseSuggestions(responseText);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ParseSuggestions_MalformedJson_ReturnsNull()
    {
        // Arrange
        var responseText = """
            ```json
            { this is not valid json }
            ```
            """;

        // Act
        var result = HarnessSuggestionExecutor.ParseSuggestions(responseText);

        // Assert
        result.Should().BeNull();
    }
}
