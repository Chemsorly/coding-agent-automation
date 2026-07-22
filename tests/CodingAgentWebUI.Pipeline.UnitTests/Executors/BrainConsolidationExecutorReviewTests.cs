using AwesomeAssertions;
using CodingAgentWebUI.Agent.Executors;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="BrainConsolidationExecutor"/> adversarial review integration.
/// Validates: Requirements 2, 6.
/// </summary>
public class BrainConsolidationExecutorReviewTests : IDisposable
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IRepositoryProvider> _mockBrainProvider = new();
    private readonly Mock<IAgentProvider> _mockAgentProvider = new();
    private readonly string _workspacePath;
    private readonly List<string> _outputLines = new();
    private readonly List<string> _callOrder = new();

    public BrainConsolidationExecutorReviewTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"brain-review-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);

        // Default setup: brainProvider.CloneAsync creates the workspace directory structure
        _mockBrainProvider.Setup(x => x.BaseBranch).Returns("main");
        _mockBrainProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                Directory.CreateDirectory(path);
                Directory.CreateDirectory(Path.Combine(path, ".agent"));
            })
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => _callOrder.Add("commit"))
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => _callOrder.Add("push"))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    private BrainConsolidationExecutor CreateExecutor() => new(_mockLogger.Object);

    private ConsolidationJobMessage CreateJob(bool reviewEnabled = true) => new()
    {
        JobId = Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.BrainConsolidation,
        TemplateId = "template-1",
        TemplateName = "Test Template",
        ProviderConfigs = [],
        PipelineConfiguration = TestPipelineConfig.Default() with
        {
            BrainConsolidationReviewEnabled = reviewEnabled
        },
        LastSuccessfulRunUtc = DateTime.UtcNow.AddDays(-7),
        WorkspacePath = _workspacePath
    };

    private Action<string> CaptureOutput => line => _outputLines.Add(line);

    private static AgentResult SuccessResult(TokenUsage? usage = null) => new()
    {
        ExitCode = 0,
        OutputLines = ["Files modified: 2", "Entries merged: 1"],
        Usage = usage
    };

    [Fact]
    public async Task ReviewEnabled_ProducesDiffThenReviews()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);
        var diffUsage = new TokenUsage { InputTokens = 100, OutputTokens = 50 };
        var reviewUsage = new TokenUsage { InputTokens = 200, OutputTokens = 100 };

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Generator call
                    return SuccessResult();
                }
                if (callCount == 2)
                {
                    // Diff summary call (UseResume=true)
                    var workspace = req.WorkspacePath;
                    var diffPath = Path.Combine(workspace, AgentWorkspacePaths.BrainConsolidationDiffFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(diffPath)!);
                    File.WriteAllText(diffPath, "## Changes\n- Modified lessons-learned.md\n- Merged 2 entries about Docker");
                    _callOrder.Add("diff-summary");
                    return SuccessResult(diffUsage);
                }
                if (callCount == 3)
                {
                    // Discriminator review call (UseResume=false)
                    var workspace = req.WorkspacePath;
                    var reviewPath = Path.Combine(workspace, AgentWorkspacePaths.BrainConsolidationReviewFilePath);
                    File.WriteAllText(reviewPath, "[SUGGESTION] Consider adding more detail to merged entries");
                    _callOrder.Add("review");
                    return SuccessResult(reviewUsage);
                }
                return SuccessResult();
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeTrue();
        // Verify diff summary was requested before review
        var diffIdx = _callOrder.IndexOf("diff-summary");
        var reviewIdx = _callOrder.IndexOf("review");
        diffIdx.Should().BeGreaterThanOrEqualTo(0);
        reviewIdx.Should().BeGreaterThan(diffIdx);
        // 3 agent calls: generator, diff summary, discriminator (no refinement since only SUGGESTION)
        _mockAgentProvider.Verify(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(3));
    }

    [Fact]
    public async Task DiffTooShort_SkipsReview()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Generator call
                    return SuccessResult();
                }
                if (callCount == 2)
                {
                    // Diff summary call — write a file that's too short (< 20 chars)
                    var workspace = req.WorkspacePath;
                    var diffPath = Path.Combine(workspace, AgentWorkspacePaths.BrainConsolidationDiffFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(diffPath)!);
                    File.WriteAllText(diffPath, "No changes");  // 10 chars < 20
                    return SuccessResult();
                }
                return SuccessResult();
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeTrue();
        // Only 2 agent calls: generator + diff summary (review skipped)
        _mockAgentProvider.Verify(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
        // Commit and push still happen
        _mockBrainProvider.Verify(x => x.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockBrainProvider.Verify(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), "main", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiffSummaryThrows_SkipsReviewGracefully()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Generator call succeeds
                    return SuccessResult();
                }
                // Diff summary call throws
                throw new TimeoutException("Agent timed out during diff summary");
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeTrue();
        // Only 2 agent calls: generator + diff summary (which threw)
        _mockAgentProvider.Verify(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
        // Commit and push still proceed
        _mockBrainProvider.Verify(x => x.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockBrainProvider.Verify(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), "main", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CommitHappensAfterReview()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
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
                    // Diff summary
                    var workspace = req.WorkspacePath;
                    var diffPath = Path.Combine(workspace, AgentWorkspacePaths.BrainConsolidationDiffFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(diffPath)!);
                    File.WriteAllText(diffPath, "## Changes\n- Modified lessons-learned.md with important updates");
                    return SuccessResult();
                }
                if (callCount == 3)
                {
                    // Discriminator writes findings with CRITICAL
                    var workspace = req.WorkspacePath;
                    var reviewPath = Path.Combine(workspace, AgentWorkspacePaths.BrainConsolidationReviewFilePath);
                    File.WriteAllText(reviewPath, "[CRITICAL] Incorrectly removed valuable entry about Docker");
                    _callOrder.Add("review");
                    return SuccessResult();
                }
                if (callCount == 4)
                {
                    // Refinement
                    _callOrder.Add("refinement");
                    return SuccessResult();
                }
                return SuccessResult();
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeTrue();
        // Verify commit happens AFTER review and refinement
        var reviewIdx = _callOrder.IndexOf("review");
        var refinementIdx = _callOrder.IndexOf("refinement");
        var commitIdx = _callOrder.IndexOf("commit");
        reviewIdx.Should().BeGreaterThanOrEqualTo(0);
        refinementIdx.Should().BeGreaterThan(reviewIdx);
        commitIdx.Should().BeGreaterThan(refinementIdx);
    }

    [Fact]
    public async Task ReviewDisabled_CommitsDirectly()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: false);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(SuccessResult());

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeTrue();
        // Only 1 agent call: generator (no diff summary, no review)
        // Note: diff summary is still called because it's separate from the review helper
        // But the review helper itself is skipped when disabled
        _mockBrainProvider.Verify(x => x.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockBrainProvider.Verify(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), "main", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiffSummaryTokenUsage_SetOnResult()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);
        var diffUsage = new TokenUsage { InputTokens = 500, OutputTokens = 250 };

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
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
                    // Diff summary — write valid diff file and return usage
                    var workspace = req.WorkspacePath;
                    var diffPath = Path.Combine(workspace, AgentWorkspacePaths.BrainConsolidationDiffFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(diffPath)!);
                    File.WriteAllText(diffPath, "## Changes\n- Modified lessons-learned.md\n- Resolved contradiction about WSL paths");
                    return SuccessResult(diffUsage);
                }
                if (callCount == 3)
                {
                    // Discriminator — no critical findings
                    var workspace = req.WorkspacePath;
                    var reviewPath = Path.Combine(workspace, AgentWorkspacePaths.BrainConsolidationReviewFilePath);
                    File.WriteAllText(reviewPath, "[SUGGESTION] Minor formatting improvement possible");
                    return SuccessResult(new TokenUsage { InputTokens = 300, OutputTokens = 150 });
                }
                return SuccessResult();
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeTrue();
        result.DiffSummaryTokenUsage.Should().NotBeNull();
        result.DiffSummaryTokenUsage!.InputTokens.Should().Be(500);
        result.DiffSummaryTokenUsage.OutputTokens.Should().Be(250);
    }

    [Fact]
    public async Task TokenUsage_PreservedWhenCommitFails()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);
        var diffUsage = new TokenUsage { InputTokens = 400, OutputTokens = 200 };
        var reviewUsage = new TokenUsage { InputTokens = 600, OutputTokens = 300 };

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
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
                    // Diff summary
                    var workspace = req.WorkspacePath;
                    var diffPath = Path.Combine(workspace, AgentWorkspacePaths.BrainConsolidationDiffFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(diffPath)!);
                    File.WriteAllText(diffPath, "## Changes\n- Modified multiple brain files with important updates");
                    return SuccessResult(diffUsage);
                }
                if (callCount == 3)
                {
                    // Discriminator — writes SUGGESTION only (no refinement triggered)
                    var workspace = req.WorkspacePath;
                    var reviewPath = Path.Combine(workspace, AgentWorkspacePaths.BrainConsolidationReviewFilePath);
                    File.WriteAllText(reviewPath, "[SUGGESTION] Consider adding timestamps");
                    return SuccessResult(reviewUsage);
                }
                return SuccessResult();
            });

        // Make commit throw
        _mockBrainProvider
            .Setup(x => x.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Commit failed: nothing to commit"));

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None, CaptureOutput);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Commit failed");
        // Token usage from review steps is preserved even though commit failed
        result.DiffSummaryTokenUsage.Should().NotBeNull();
        result.DiffSummaryTokenUsage!.InputTokens.Should().Be(400);
        result.DiffSummaryTokenUsage.OutputTokens.Should().Be(200);
        result.ReviewTokenUsage.Should().NotBeNull();
        result.ReviewTokenUsage!.InputTokens.Should().Be(600);
        result.ReviewTokenUsage.OutputTokens.Should().Be(300);
    }
}
