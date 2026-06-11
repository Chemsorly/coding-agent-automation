using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Validates that NullPipelineRunHistoryService provides no-op behavior and that
/// QualityGateExecutor works correctly with it — no NullReferenceException occurs.
/// Validates: Requirements 17.2, 17.3
/// </summary>
public class NullPipelineRunHistoryServiceTests
{
    [Fact]
    public void GetRunHistory_ReturnsEmptyList()
    {
        var sut = new NullPipelineRunHistoryService();

        var result = sut.GetRunHistory();

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetRunsByAgentId_ReturnsEmptyList()
    {
        var sut = new NullPipelineRunHistoryService();

        var result = sut.GetRunsByAgentId("agent-1", 10);

        result.Should().BeEmpty();
    }

    [Fact]
    public void AddRunToHistory_DoesNotThrow()
    {
        var sut = new NullPipelineRunHistoryService();
        var run = new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = "/tmp/ws"
        };

        var act = () => sut.AddRunToHistory(run);

        act.Should().NotThrow();
    }

    [Fact]
    public void TryDeleteWorkspace_DoesNotThrow()
    {
        var sut = new NullPipelineRunHistoryService();

        var act = () => sut.TryDeleteWorkspace("/tmp/ws", "run-1", "/tmp");

        act.Should().NotThrow();
    }

    [Fact]
    public void CleanupExpiredWorkspaces_DoesNotThrow()
    {
        var sut = new NullPipelineRunHistoryService();
        var config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" };

        var act = () => sut.CleanupExpiredWorkspaces(config, "active-run");

        act.Should().NotThrow();
    }

    [Fact]
    public async Task QualityGateExecutor_WithNullHistoryService_DoesNotThrow()
    {
        // Arrange: construct QualityGateExecutor with NullPipelineRunHistoryService
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        var historyService = new NullPipelineRunHistoryService();

        var executor = new QualityGateExecutor(
            mockValidator.Object,
            new PullRequestOrchestrator(mockLogger.Object),
            new CiLogWriter(mockLogger.Object),
            new FeedbackService(mockLogger.Object),
            mockLogger.Object,
            historyService);

        var run = new PipelineRun
        {
            RunId = "test-run-null-history",
            IssueIdentifier = "99",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = "/tmp/workspace"
        };

        var mockCallbacks = new Mock<IPipelineCallbacks>();
        mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockCallbacks.Setup(c => c.RemoveAllAgentLabels(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        mockCallbacks.Setup(c => c.CreatePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockCallbacks.Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsProcessAlive = true, IsExecuting = false });

        var mockIssueOps = new Mock<IAgentIssueOperations>();
        mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockRepoProvider = new Mock<IRepositoryProvider>();

        // Validator returns a passing report so the executor completes without retries
        var passingReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };
        mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(passingReport);

        var config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" };

        var context = new QualityGateContext
        {
            Run = run,
            Config = config,
            AgentProvider = mockAgent.Object,
            IssueOps = mockIssueOps.Object,
            Callbacks = mockCallbacks.Object,
            RepoProvider = mockRepoProvider.Object,
            QualityGateConfigs = [new QualityGateConfiguration { Id = "gate-1", DisplayName = "Build" }]
        };

        // Act & Assert: should not throw NullReferenceException
        await executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // If we get here without NullReferenceException, the null-safe history service works.
        // The run may or may not reach Completed depending on full executor flow,
        // but no exception means requirement 17.3 is satisfied.
    }
}
