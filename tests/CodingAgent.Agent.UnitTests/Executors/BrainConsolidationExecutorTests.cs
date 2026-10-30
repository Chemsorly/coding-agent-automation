using AwesomeAssertions;
using CodingAgent.Agent.Executors;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Agent.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="BrainConsolidationExecutor"/>.
/// Tests: commits and pushes on success, marks failed when the push fails, refuses a read-only brain.
/// The merge of a rejected push is tested in BrainUpdateServiceConsolidationMergeTests.
/// </summary>
public class BrainConsolidationExecutorTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IRepositoryProvider> _mockBrainProvider = new();
    private readonly Mock<IAgentProvider> _mockAgentProvider = new();
    private readonly Mock<IBrainUpdateService> _mockBrainUpdateService = new();

    public BrainConsolidationExecutorTests()
    {
        // The brain update service pushes through the brain provider; a push it cannot merge fails like the provider's.
        _mockBrainUpdateService
            .Setup(x => x.PushConsolidationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .Returns(async (string path, string _, IRepositoryProvider provider, CancellationToken ct, int _) =>
            {
                await provider.PushBranchAsync(path, provider.BaseBranch, ct);
                return 1;
            });
    }

    private BrainConsolidationExecutor CreateExecutor() => new(_mockLogger.Object, _mockBrainUpdateService.Object);

    private static ConsolidationJobMessage CreateJob(string? jobId = null, bool brainReadOnly = false) => new()
    {
        JobId = jobId ?? Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.BrainConsolidation,
        TemplateId = "template-1",
        TemplateName = "Test Template",
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration { BrainReadOnly = brainReadOnly, WorkspaceBaseDirectory = Path.GetTempPath() },
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
            .Setup(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), (BranchName)"main", It.IsAny<CancellationToken>()))
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
        _mockBrainProvider.Verify(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), (BranchName)"main", It.IsAny<CancellationToken>()), Times.Once);
        // The push goes through the consolidation merge, with the commit's message.
        _mockBrainUpdateService.Verify(x => x.PushConsolidationAsync(
            It.IsAny<string>(), It.Is<string>(m => m.Contains(job.JobId)), _mockBrainProvider.Object,
            It.IsAny<CancellationToken>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_BrainReadOnly_DoesNotCloneOrRunTheAgent()
    {
        // Arrange: the configuration resolved at claim time makes the brain read-only for the template
        var executor = CreateExecutor();
        var job = CreateJob(brainReadOnly: true);

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("read-only");
        _mockBrainProvider.Verify(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockAgentProvider.Verify(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
        _mockBrainUpdateService.Verify(x => x.PushConsolidationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_PushStillRejected_MarksRunAsFailed()
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
            .Setup(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), (BranchName)"main", It.IsAny<CancellationToken>()))
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
        _mockBrainProvider.Verify(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()), Times.Never);
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

    [Fact]
    public async Task ExecuteAsync_WithProgress_ReportsStepsInOrder()
    {
        // Arrange
        var job = CreateJob();
        var steps = new List<PipelineStep>();
        var progress = new ConsolidationProgress(ConsolidationProgress.CreateRun(job), steps.Add);
        var executor = new BrainConsolidationExecutor(_mockLogger.Object, _mockBrainUpdateService.Object, progress);

        _mockBrainProvider.Setup(x => x.BaseBranch).Returns("main");
        _mockBrainProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), (BranchName)"main", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Use It.IsAny for the callback param since TryGenerateDiffSummaryAsync now passes onOutputLine through
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Files modified: 1", "Entries merged: 2", "Contradictions resolved: 0", "Entries pruned: 0"]
            });

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        steps.Should().Equal(
            PipelineStep.CloningRepository,
            PipelineStep.ConsolidatingKnowledge,
            PipelineStep.ReviewingBrainChanges,
            PipelineStep.PushingBrainChanges);
    }

    [Fact]
    public async Task ExecuteAsync_AgentSucceeds_ReturnsBrainOutcome()
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
            .Setup(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), (BranchName)"main", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("3f9b2c1d0e");
        _mockBrainProvider
            .Setup(x => x.GetCommitWebUrl("3f9b2c1d0e"))
            .Returns("https://github.com/org/brain/commit/3f9b2c1d0e");

        _mockBrainUpdateService
            .Setup(x => x.PushConsolidationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(2);

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
        result.BrainOutcome.Should().NotBeNull();
        result.BrainOutcome!.FilesModified.Should().Be(3);
        result.BrainOutcome.EntriesMerged.Should().Be(5);
        result.BrainOutcome.ContradictionsResolved.Should().Be(1);
        result.BrainOutcome.EntriesPruned.Should().Be(2);
        result.BrainOutcome.PushedCommitSha.Should().Be("3f9b2c1d0e");
        result.BrainOutcome.PushedCommitUrl.Should().Be("https://github.com/org/brain/commit/3f9b2c1d0e");
        result.BrainOutcome.PushAttempts.Should().Be(2);
        result.BrainOutcome.BaseBranch.Should().Be("main");
    }

    [Fact]
    public async Task ExecuteAsync_ShaCannotBeRead_StillSucceedsWithoutCommit()
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
            .Setup(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), (BranchName)"main", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("git log failed"));

        // TODO: PushConsolidationAsync is not set up here; Moq loose default returns Task.FromResult(0),
        // so pushAttempts = 0 and BrainOutcome.PushAttempts = 0, which cannot occur in production
        // (a successful push always returns ≥ 1). The regression is caught by
        // ExecuteAsync_AgentSucceeds_ReturnsBrainOutcome; consider adding a push mock returning 1 here
        // to make the scenario internally coherent.
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
        result.BrainOutcome.Should().NotBeNull();
        result.BrainOutcome!.PushedCommitSha.Should().BeNull();
        result.BrainOutcome.PushedCommitUrl.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShaIsEmpty_TreatsItAsNull()
    {
        // Verifies the string.IsNullOrEmpty guard: GetHeadCommitShaAsync returning "" is normalised to
        // null before being stored in BrainOutcome, and GetCommitWebUrl is never called with "".
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
            .Setup(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), (BranchName)"main", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBrainProvider
            .Setup(x => x.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

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
        result.BrainOutcome.Should().NotBeNull();
        result.BrainOutcome!.PushedCommitSha.Should().BeNull("empty SHA must be normalised to null");
        result.BrainOutcome.PushedCommitUrl.Should().BeNull("URL must be null when SHA is null");
        _mockBrainProvider.Verify(x => x.GetCommitWebUrl(It.IsAny<string>()), Times.Never,
            "GetCommitWebUrl must not be called with an empty SHA");
    }

    [Fact]
    public async Task ExecuteAsync_PushFails_HasNoBrainOutcome()
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

        _mockBrainUpdateService
            .Setup(x => x.PushConsolidationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
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
        result.BrainOutcome.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_CancelledBeforeBrainFileScan_ReturnsCancelledResult()
    {
        // Regression for issue #4014: OCE thrown during the brain file scan (archive step) must not be
        // swallowed by the bare catch {} — it must propagate through the outer archive-step catch
        // (which explicitly excludes OCE via `when (ex is not OperationCanceledException)`) and be
        // handled by WrapWithCancellationHandlingAsync, which returns a cancelled result.
        //
        // Note: ListTrackedFilesAsync is awaited *before* the .Select() lambda, so the OCE here is
        // thrown outside the inner catch being fixed. This test validates the overall cancellation
        // chain through the archive step.
        //
        // TODO: The `catch (OperationCanceledException) { throw; }` branch added inside the lambda
        // is defensive dead code: StreamReader(string, Encoding) and ReadLine() are synchronous and
        // cannot throw OperationCanceledException, so that specific line cannot be exercised without
        // replacing the StreamReader usage with a CT-aware abstraction. It is retained for consistency
        // with the project-wide convention that OCE is never swallowed.

        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockBrainProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBrainUpdateService
            .Setup(x => x.ListTrackedFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, cts.Token);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().ContainEquivalentOf("cancel");
    }

    [Fact]
    public async Task ExecuteAsync_UnreadableBrainFile_ContinuesAndLogsWarning()
    {
        // Regression for issue #4014: when ListTrackedFilesAsync returns a path that does not exist on
        // disk, the FileNotFoundException thrown by new StreamReader(full, ...) was previously swallowed
        // by the bare catch {}, producing a BrainTrackedFile with firstLine==null with no diagnostic.
        // After the fix the exception is caught by catch (Exception ex), Logger.Warning is emitted, and
        // the executor continues to completion.
        //
        // The path "general/SKILL.md" is a v2 skill path. BrainV1Archiver.Plan only archives it when
        // firstLine is not the v2 skill marker ("<!-- brain:v2 skill -->"). Since the read failed,
        // firstLine remains null (the initialised default), so archival is planned; File.Move then fails
        // (the file does not exist) and Logger.Warning("Brain archive step failed...") is emitted.
        // The secondary assertion below confirms firstLine was not silently replaced with the marker
        // value, which would have suppressed the archival attempt.

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
            .Setup(x => x.PushBranchAsync(It.IsAny<WorkspacePath>(), (BranchName)"main", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // "general/SKILL.md" is a v2 skill path. It does not exist on disk, so StreamReader throws
        // FileNotFoundException. BrainV1Archiver archives it when firstLine != the skill marker,
        // letting File.Move failure serve as an indirect witness that firstLine was null/non-marker.
        _mockBrainUpdateService
            .Setup(x => x.ListTrackedFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "general/SKILL.md" }.AsReadOnly());
        // Prevent NullReferenceException in the review validation step (step 6) that would emit
        // an unrelated Warning and interfere with the warning-count assertions below.
        _mockBrainUpdateService
            .Setup(x => x.GetWorkingTreeChanges(It.IsAny<string>()))
            .Returns(new List<BrainPathChange>());

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        var result = await executor.ExecuteAsync(job, _mockBrainProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue("the executor should continue past an unreadable brain file");

        // Primary: the fix's catch (Exception ex) branch was reached and emitted the "brain file" warning.
        // The compiler resolves Warning(ex, "Failed to read brain file {Path}", full) — where full is a
        // string — to Warning<string>(Exception?, string, string).
        _mockLogger.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.Is<string>(t => t.Contains("brain file")),
            It.IsAny<string>()),
            Times.AtLeastOnce);

        // Secondary (indirect firstLine==null witness): BrainV1Archiver scheduled the skill file for
        // archival (firstLine was null, not the v2 marker), File.Move failed, and the outer archive-step
        // catch emitted "Brain archive step failed". If firstLine had been replaced with the marker
        // "<!-- brain:v2 skill -->", no archival would have been planned and this warning would be absent.
        _mockLogger.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.Is<string>(t => t.Contains("Brain archive step failed")),
            It.IsAny<string>()),
            Times.AtLeastOnce);
    }
}
