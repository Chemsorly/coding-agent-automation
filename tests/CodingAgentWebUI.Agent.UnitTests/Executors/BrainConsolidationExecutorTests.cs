using AwesomeAssertions;
using CodingAgentWebUI.Agent.Executors;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="BrainConsolidationExecutor"/>.
/// Tests: commits and pushes on success, marks failed on push conflict.
/// </summary>
public class BrainConsolidationExecutorTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IRepositoryProvider> _mockBrainProvider = new();
    private readonly Mock<IAgentProvider> _mockAgentProvider = new();

    private BrainConsolidationExecutor CreateExecutor() => new(_mockLogger.Object);

    private static ConsolidationJobMessage CreateJob(string? jobId = null) => new()
    {
        JobId = jobId ?? Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.BrainConsolidation,
        TemplateId = "template-1",
        TemplateName = "Test Template",
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration(),
        LastSuccessfulRunUtc = DateTime.UtcNow.AddDays(-7)
    };

    [Fact]
    public async Task ExecuteAsync_AgentSucceeds_CommitsAndPushes()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockBrainProvider.Setup(x => x.BaseBranch).Returns("main");
        _mockBrainProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), "main", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Files modified: 3", "Entries merged: 5", "Contradictions resolved: 1", "Entries pruned: 2"]
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.JobId.Should().Be(job.JobId);
        result.Summary.Should().NotBeNullOrWhiteSpace();

        _mockBrainProvider.Verify(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockBrainProvider.Verify(x => x.CommitAllAsync(It.IsAny<WorkspacePath>(), It.Is<string>(m => m.Contains(job.JobId)), It.IsAny<CancellationToken>()), Times.Once);
        _mockBrainProvider.Verify(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), "main", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PushConflict_MarksRunAsFailed()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockBrainProvider.Setup(x => x.BaseBranch).Returns("main");
        _mockBrainProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("non-fast-forward: push rejected"));

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Files modified: 1"]
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.JobId.Should().Be(job.JobId);
        result.ErrorMessage.Should().Contain("non-fast-forward");
    }

    [Fact]
    public async Task ExecuteAsync_AgentFails_ReturnsFailedResult()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockBrainProvider.Setup(x => x.BaseBranch).Returns("main");
        _mockBrainProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 1,
                OutputLines = ["Error: something went wrong"]
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exit");

        // Should NOT attempt commit or push when agent fails
        _mockBrainProvider.Verify(x => x.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockBrainProvider.Verify(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ParseMetrics_ValidOutput_ExtractsAllMetrics()
    {
        // Arrange
        var output = "Files modified: 3\nEntries merged: 5\nContradictions resolved: 1\nEntries pruned: 2";

        // Act
        var (filesModified, entriesMerged, contradictionsResolved, entriesPruned) =
            BrainConsolidationExecutor.ParseMetrics(output);

        // Assert
        filesModified.Should().Be(3);
        entriesMerged.Should().Be(5);
        contradictionsResolved.Should().Be(1);
        entriesPruned.Should().Be(2);
    }

    [Fact]
    public void ParseMetrics_MissingMetrics_ReturnsZeros()
    {
        // Arrange
        var output = "Consolidation complete. No changes needed.";

        // Act
        var (filesModified, entriesMerged, contradictionsResolved, entriesPruned) =
            BrainConsolidationExecutor.ParseMetrics(output);

        // Assert
        filesModified.Should().Be(0);
        entriesMerged.Should().Be(0);
        contradictionsResolved.Should().Be(0);
        entriesPruned.Should().Be(0);
    }
}
