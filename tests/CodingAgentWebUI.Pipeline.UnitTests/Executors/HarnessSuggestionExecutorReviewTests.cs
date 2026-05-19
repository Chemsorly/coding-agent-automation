using AwesomeAssertions;
using CodingAgentWebUI.Agent.Executors;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.UnitTests.Helpers;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="HarnessSuggestionExecutor"/> adversarial review integration.
/// Validates: Requirements 3, 6.
/// </summary>
public class HarnessSuggestionExecutorReviewTests : IDisposable
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly HarnessSuggestionExecutor _executor;
    private readonly string _workspacePath;
    private readonly List<string> _outputLines;

    private static readonly string ValidSuggestionsJson = """
        {
            "generatedAtUtc": "2026-07-01T12:00:00Z",
            "basedOnRunCount": 5,
            "successRate": 80.0,
            "suggestions": [
                {
                    "text": "Add retry logic for flaky network calls",
                    "rationale": "Network timeouts observed in 3 of 5 runs",
                    "frequency": 3
                }
            ]
        }
        """;

    private static readonly string ValidFeedbackJson = """
        [
            {"outcome": "Success"},
            {"outcome": "Success"},
            {"outcome": "Failure"},
            {"outcome": "Success"},
            {"outcome": "Failure"}
        ]
        """;

    public HarnessSuggestionExecutorReviewTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();
        _executor = new HarnessSuggestionExecutor(_mockLogger.Object);
        _workspacePath = Path.Combine(Path.GetTempPath(), $"harness-review-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        _outputLines = new List<string>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    private Action<string> CaptureOutput => line => _outputLines.Add(line);

    private ConsolidationJobMessage CreateJob(bool reviewEnabled = true) => new()
    {
        JobId = Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.HarnessSuggestions,
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration
        {
            HarnessSuggestionsReviewEnabled = reviewEnabled,
            AgentTimeout = TimeSpan.FromMinutes(5)
        },
        FeedbackDataJson = ValidFeedbackJson,
        WorkspacePath = _workspacePath
    };

    private AgentResult SuccessResult(string[]? outputLines = null, TokenUsage? usage = null) => new()
    {
        ExitCode = 0,
        OutputLines = outputLines ?? [$"Here are suggestions:\n```json\n{ValidSuggestionsJson}\n```"],
        Usage = usage
    };

    /// <summary>
    /// Test 1: When review is enabled and the generator succeeds, the executor sends a
    /// write-to-file prompt (UseResume=true), then dispatches the discriminator via
    /// AdversarialReviewHelper.
    /// </summary>
    [Fact]
    public async Task ReviewEnabled_WritesOutputFileThenReviews()
    {
        // Arrange
        var job = CreateJob(reviewEnabled: true);
        var callCount = 0;

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call: generator agent (initial analysis)
                    return SuccessResult();
                }
                if (callCount == 2)
                {
                    // Second call: write-to-file step (UseResume=true)
                    req.UseResume.Should().BeTrue();
                    // Simulate the agent writing the output file
                    var outputDir = Path.Combine(_workspacePath, "harness", ".agent");
                    Directory.CreateDirectory(outputDir);
                    File.WriteAllText(
                        Path.Combine(_workspacePath, "harness", AgentWorkspacePaths.HarnessSuggestionsOutputFilePath),
                        ValidSuggestionsJson);
                    return SuccessResult();
                }
                if (callCount == 3)
                {
                    // Third call: discriminator review (UseResume=false)
                    req.UseResume.Should().BeFalse();
                    // Write review findings with only suggestions (no CRITICAL/WARNING)
                    var reviewPath = Path.Combine(
                        _workspacePath, "harness",
                        AgentWorkspacePaths.HarnessSuggestionsReviewFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
                    File.WriteAllText(reviewPath, "[SUGGESTION] Consider adding more detail to rationale");
                    return SuccessResult();
                }
                return SuccessResult();
            });

        // Act
        var result = await _executor.ExecuteAsync(job, _mockAgent.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeTrue();
        result.HarnessSuggestions.Should().NotBeNull();
        result.HarnessSuggestions!.Suggestions.Should().HaveCount(1);

        // Verify at least 3 agent calls were made: generator, write-to-file, discriminator
        _mockAgent.Verify(
            a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.AtLeast(3));
    }

    /// <summary>
    /// Test 2: When the write-to-file step doesn't produce the output file, the executor
    /// falls back to ParseSuggestions(responseText).
    /// </summary>
    [Fact]
    public async Task OutputFileMissing_FallsBackToResponseParsing()
    {
        // Arrange
        var job = CreateJob(reviewEnabled: true);
        var callCount = 0;

        var responseWithSuggestions = $"Here are suggestions:\n```json\n{ValidSuggestionsJson}\n```";

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Generator succeeds with suggestions in response text
                    return new AgentResult
                    {
                        ExitCode = 0,
                        OutputLines = responseWithSuggestions.Split('\n')
                    };
                }
                if (callCount == 2)
                {
                    // Write-to-file step: agent succeeds but does NOT write the file
                    return SuccessResult();
                }
                // No further calls expected since review is skipped
                return SuccessResult();
            });

        // Act
        var result = await _executor.ExecuteAsync(job, _mockAgent.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeTrue();
        result.HarnessSuggestions.Should().NotBeNull();
        result.HarnessSuggestions!.BasedOnRunCount.Should().Be(5);
        result.HarnessSuggestions.Suggestions.Should().HaveCount(1);

        // Only 2 agent calls: generator + write-to-file (review skipped because file missing)
        _mockAgent.Verify(
            a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(2));

        // Output should indicate fallback
        _outputLines.Should().Contain(line => line.Contains("falling back", StringComparison.OrdinalIgnoreCase)
            || line.Contains("not created", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Test 3: When refinement runs but the refined file has invalid JSON, the executor
    /// falls back to response text parsing.
    /// </summary>
    [Fact]
    public async Task RefinedFileMalformed_FallsBackToResponseParsing()
    {
        // Arrange
        var job = CreateJob(reviewEnabled: true);
        var callCount = 0;

        var responseWithSuggestions = $"Here are suggestions:\n```json\n{ValidSuggestionsJson}\n```";

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Generator succeeds
                    return new AgentResult
                    {
                        ExitCode = 0,
                        OutputLines = responseWithSuggestions.Split('\n')
                    };
                }
                if (callCount == 2)
                {
                    // Write-to-file step: writes valid JSON initially
                    var outputDir = Path.Combine(_workspacePath, "harness", ".agent");
                    Directory.CreateDirectory(outputDir);
                    File.WriteAllText(
                        Path.Combine(_workspacePath, "harness", AgentWorkspacePaths.HarnessSuggestionsOutputFilePath),
                        ValidSuggestionsJson);
                    return SuccessResult();
                }
                if (callCount == 3)
                {
                    // Discriminator review: writes CRITICAL finding
                    var reviewPath = Path.Combine(
                        _workspacePath, "harness",
                        AgentWorkspacePaths.HarnessSuggestionsReviewFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
                    File.WriteAllText(reviewPath, "[CRITICAL] Suggestions not grounded in feedback data");
                    return SuccessResult();
                }
                if (callCount == 4)
                {
                    // Refinement: overwrites the output file with malformed JSON
                    var outputPath = Path.Combine(
                        _workspacePath, "harness",
                        AgentWorkspacePaths.HarnessSuggestionsOutputFilePath);
                    File.WriteAllText(outputPath, "{ this is not valid json at all }}}");
                    return SuccessResult();
                }
                return SuccessResult();
            });

        // Act
        var result = await _executor.ExecuteAsync(job, _mockAgent.Object, CancellationToken.None, CaptureOutput);

        // Assert — falls back to response text parsing which has valid suggestions
        result.Success.Should().BeTrue();
        result.HarnessSuggestions.Should().NotBeNull();
        result.HarnessSuggestions!.Suggestions.Should().HaveCount(1);
        result.HarnessSuggestions.BasedOnRunCount.Should().Be(5);

        // All 4 calls made: generator, write-to-file, discriminator, refinement
        _mockAgent.Verify(
            a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(4));
    }

    /// <summary>
    /// Test 4: When HarnessSuggestionsReviewEnabled = false, no review calls are made
    /// and the executor uses response text parsing.
    /// </summary>
    [Fact]
    public async Task ReviewDisabled_UsesExistingParsing()
    {
        // Arrange
        var job = CreateJob(reviewEnabled: false);

        var responseWithSuggestions = $"Here are suggestions:\n```json\n{ValidSuggestionsJson}\n```";

        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Generator call (UseResume=false)
                    return new AgentResult
                    {
                        ExitCode = 0,
                        OutputLines = responseWithSuggestions.Split('\n')
                    };
                }
                if (callCount == 2)
                {
                    // Write-to-file step: agent succeeds but does NOT write the file
                    return SuccessResult();
                }
                // No further calls expected since review is disabled
                return SuccessResult();
            });

        // Act
        var result = await _executor.ExecuteAsync(job, _mockAgent.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeTrue();
        result.HarnessSuggestions.Should().NotBeNull();
        result.HarnessSuggestions!.Suggestions.Should().HaveCount(1);

        // When review is disabled: generator + write-to-file, but NO discriminator or refinement calls.
        // The write-to-file step still runs (it's independent of review), but since the file
        // isn't created, it falls back to response text parsing. The review helper returns Skipped.
        // Verify no discriminator call was made (UseResume=false after the generator)
        _mockAgent.Verify(
            a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(2)); // Only generator + write-to-file, no review calls
    }

    /// <summary>
    /// Test 5: ReviewTokenUsage and RefinementTokenUsage are set on the result
    /// from the review and refinement agent calls.
    /// </summary>
    [Fact]
    public async Task TokenUsage_SetOnResult()
    {
        // Arrange
        var job = CreateJob(reviewEnabled: true);
        var callCount = 0;
        var reviewUsage = new TokenUsage { InputTokens = 1000, OutputTokens = 500 };
        var refinementUsage = new TokenUsage { InputTokens = 2000, OutputTokens = 1000 };

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Generator
                    return SuccessResult();
                }
                if (callCount == 2)
                {
                    // Write-to-file step
                    var outputDir = Path.Combine(_workspacePath, "harness", ".agent");
                    Directory.CreateDirectory(outputDir);
                    File.WriteAllText(
                        Path.Combine(_workspacePath, "harness", AgentWorkspacePaths.HarnessSuggestionsOutputFilePath),
                        ValidSuggestionsJson);
                    return SuccessResult();
                }
                if (callCount == 3)
                {
                    // Discriminator review — returns with review token usage
                    var reviewPath = Path.Combine(
                        _workspacePath, "harness",
                        AgentWorkspacePaths.HarnessSuggestionsReviewFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
                    File.WriteAllText(reviewPath, "[CRITICAL] Major issue found");
                    return new AgentResult
                    {
                        ExitCode = 0,
                        OutputLines = [],
                        Usage = reviewUsage
                    };
                }
                if (callCount == 4)
                {
                    // Refinement — returns with refinement token usage
                    // Also re-write valid suggestions to the output file
                    var outputPath = Path.Combine(
                        _workspacePath, "harness",
                        AgentWorkspacePaths.HarnessSuggestionsOutputFilePath);
                    File.WriteAllText(outputPath, ValidSuggestionsJson);
                    return new AgentResult
                    {
                        ExitCode = 0,
                        OutputLines = [],
                        Usage = refinementUsage
                    };
                }
                return SuccessResult();
            });

        // Act
        var result = await _executor.ExecuteAsync(job, _mockAgent.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeTrue();
        result.ReviewTokenUsage.Should().Be(reviewUsage);
        result.RefinementTokenUsage.Should().Be(refinementUsage);
    }
}
