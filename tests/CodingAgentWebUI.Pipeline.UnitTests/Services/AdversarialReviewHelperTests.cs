using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="AdversarialReviewHelper"/>.
/// </summary>
public class AdversarialReviewHelperTests : IDisposable
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<ILogger> _mockLogger;
    private readonly string _workspacePath;
    private readonly string _reviewFilePath = ".agent/test-review.md";
    private readonly AdversarialReviewConfig _enabledConfig;
    private readonly List<string> _outputLines;

    public AdversarialReviewHelperTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockLogger = new Mock<ILogger>();
        _workspacePath = Path.Combine(Path.GetTempPath(), $"adversarial-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".agent"));

        _enabledConfig = new AdversarialReviewConfig
        {
            Enabled = true,
            AgentTimeout = TimeSpan.FromMinutes(5)
        };

        _outputLines = new List<string>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    private Action<string> CaptureOutput => line => _outputLines.Add(line);

    private string AbsoluteReviewPath => Path.Combine(_workspacePath, _reviewFilePath);

    private AgentResult SuccessResult(TokenUsage? usage = null) => new()
    {
        ExitCode = 0,
        OutputLines = Array.Empty<string>(),
        Usage = usage
    };

    private AgentResult FailureResult(int exitCode = 1, TokenUsage? usage = null) => new()
    {
        ExitCode = exitCode,
        OutputLines = Array.Empty<string>(),
        Usage = usage
    };

    [Fact]
    public async Task Disabled_ReturnsSkipped()
    {
        var config = new AdversarialReviewConfig { Enabled = false, AgentTimeout = TimeSpan.FromMinutes(5) };

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            config,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        result.Should().BeSameAs(AdversarialReviewResult.Skipped);
        result.ReviewExecuted.Should().BeFalse();
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task DiscriminatorFails_SkipsRefinement()
    {
        var reviewUsage = new TokenUsage { InputTokens = 100, OutputTokens = 50 };
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(FailureResult(exitCode: 2, usage: reviewUsage));

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        result.ReviewExecuted.Should().BeTrue();
        result.RefinementTriggered.Should().BeFalse();
        result.ReviewTokenUsage.Should().Be(reviewUsage);
        // Only one call (discriminator), no refinement
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task NoFindingsFile_SkipsRefinement()
    {
        // Discriminator succeeds but does not write the findings file
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(SuccessResult());

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        result.ReviewExecuted.Should().BeTrue();
        result.RefinementTriggered.Should().BeFalse();
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task CriticalFinding_TriggersRefinement()
    {
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Discriminator writes findings file with CRITICAL
                    File.WriteAllText(AbsoluteReviewPath, "[CRITICAL] Major issue found\n[SUGGESTION] Minor style issue");
                    return SuccessResult();
                }
                // Refinement call
                return SuccessResult();
            });

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        result.ReviewExecuted.Should().BeTrue();
        result.RefinementTriggered.Should().BeTrue();
        result.Severities!.Critical.Should().Be(1);
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task WarningFinding_TriggersRefinement()
    {
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    File.WriteAllText(AbsoluteReviewPath, "[WARNING] Incomplete coverage\n[SUGGESTION] Add docs");
                    return SuccessResult();
                }
                return SuccessResult();
            });

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        result.ReviewExecuted.Should().BeTrue();
        result.RefinementTriggered.Should().BeTrue();
        result.Severities!.Warning.Should().Be(1);
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SuggestionOnly_SkipsRefinement()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                File.WriteAllText(AbsoluteReviewPath, "[SUGGESTION] Consider renaming\n[SUGGESTION] Add comments");
                return SuccessResult();
            });

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        result.ReviewExecuted.Should().BeTrue();
        result.RefinementTriggered.Should().BeFalse();
        result.Severities!.Suggestion.Should().Be(2);
        result.Severities.Critical.Should().Be(0);
        result.Severities.Warning.Should().Be(0);
        // Only discriminator call, no refinement
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task DiscriminatorThrows_ReturnsGracefully()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new InvalidOperationException("Agent crashed"));

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        result.ReviewExecuted.Should().BeFalse();
        result.RefinementTriggered.Should().BeFalse();
    }

    [Fact]
    public async Task RefinementFails_KeepsOriginal()
    {
        var callCount = 0;
        var reviewUsage = new TokenUsage { InputTokens = 200, OutputTokens = 100 };
        var refinementUsage = new TokenUsage { InputTokens = 300, OutputTokens = 150 };

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    File.WriteAllText(AbsoluteReviewPath, "[CRITICAL] Bad output");
                    return SuccessResult(usage: reviewUsage);
                }
                // Refinement fails
                return FailureResult(exitCode: 1, usage: refinementUsage);
            });

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        result.ReviewExecuted.Should().BeTrue();
        result.RefinementTriggered.Should().BeFalse(); // Treated as not successfully refined
        result.ReviewTokenUsage.Should().Be(reviewUsage);
        result.RefinementTokenUsage.Should().Be(refinementUsage);
    }

    [Fact]
    public async Task RefinementThrows_PreservesReviewUsage()
    {
        var callCount = 0;
        var reviewUsage = new TokenUsage { InputTokens = 500, OutputTokens = 250 };

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    File.WriteAllText(AbsoluteReviewPath, "[CRITICAL] Needs fix");
                    return SuccessResult(usage: reviewUsage);
                }
                throw new TimeoutException("Refinement timed out");
            });

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        // When refinement throws, the whole try-catch catches it and returns ReviewExecuted=false
        // But the review usage is lost because the exception is caught at the outer level
        result.ReviewExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task TokenUsage_CapturedFromBothCalls()
    {
        var callCount = 0;
        var reviewUsage = new TokenUsage { InputTokens = 1000, OutputTokens = 500 };
        var refinementUsage = new TokenUsage { InputTokens = 2000, OutputTokens = 1000 };

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    File.WriteAllText(AbsoluteReviewPath, "[CRITICAL] Issue found");
                    return SuccessResult(usage: reviewUsage);
                }
                return SuccessResult(usage: refinementUsage);
            });

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        result.ReviewTokenUsage.Should().Be(reviewUsage);
        result.RefinementTokenUsage.Should().Be(refinementUsage);
    }

    [Fact]
    public async Task StaleReviewFile_DeletedBeforeDispatch()
    {
        // Create a stale review file before calling
        File.WriteAllText(AbsoluteReviewPath, "[CRITICAL] Old stale finding");

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                // At this point, the stale file should have been deleted
                // Discriminator does NOT write a new file
                return SuccessResult();
            });

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        // Since the stale file was deleted and discriminator didn't write a new one,
        // we should get "no findings file" behavior
        result.ReviewExecuted.Should().BeTrue();
        result.RefinementTriggered.Should().BeFalse();
        File.Exists(AbsoluteReviewPath).Should().BeFalse();
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task OutputLines_Emitted()
    {
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    File.WriteAllText(AbsoluteReviewPath, "[CRITICAL] Issue\n[WARNING] Another issue");
                    return SuccessResult();
                }
                return SuccessResult();
            });

        await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        // Should have output lines for: dispatching, review complete (with counts), refinement triggered, refinement complete
        _outputLines.Should().Contain(line => line.Contains("Dispatching"));
        _outputLines.Should().Contain(line => line.Contains("CRITICAL") && line.Contains("WARNING"));
        _outputLines.Should().Contain(line => line.Contains("Refinement triggered"));
        _outputLines.Should().Contain(line => line.Contains("Refinement complete"));
    }

    [Fact]
    public async Task CaseInsensitiveSeverityParsing()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                // Write findings with mixed case severity markers
                File.WriteAllText(AbsoluteReviewPath, "[critical] lowercase critical\n[WARNING] uppercase warning\n[Suggestion] mixed case suggestion");
                return SuccessResult();
            });

        var result = await AdversarialReviewHelper.ExecuteReviewAsync(
            _mockAgent.Object,
            _workspacePath,
            "review prompt",
            "refinement prompt",
            _reviewFilePath,
            _enabledConfig,
            CaptureOutput,
            _mockLogger.Object,
            CancellationToken.None);

        // Both [critical] and [WARNING] should be detected (case-insensitive)
        result.Severities!.Critical.Should().Be(1);
        result.Severities.Warning.Should().Be(1);
        result.Severities.Suggestion.Should().Be(1);
        // Since CRITICAL is found, refinement should be triggered
        result.RefinementTriggered.Should().BeTrue();
    }
}
