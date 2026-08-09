using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="LabelServiceExtensions"/>.
/// Verifies the best-effort label swap pattern: exception swallowing, OCE propagation,
/// and correct routing via PipelineRun convenience overload.
/// </summary>
public class LabelServiceExtensionsTests
{
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task TrySwapLabelAsync_Success_DoesNotThrow()
    {
        // TODO: The NotThrowAsync assertion is tautological since the mock returns Task.CompletedTask.
        // The Verify call below provides the real value. Consider removing NotThrowAsync or restructuring.
        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var act = () => _mockLabelService.Object.TrySwapLabelAsync(
            "provider-1", "org/repo#42", AgentLabels.InProgress,
            LabelTargetKind.Issue, _logger, "TestContext", CancellationToken.None);

        await act.Should().NotThrowAsync();

        _mockLabelService.Verify(l => l.SwapLabelAsync(
            new ProviderConfigId("provider-1"), "org/repo#42", AgentLabels.InProgress,
            LabelTargetKind.Issue, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task TrySwapLabelAsync_SwapThrows_DoesNotPropagate()
    {
        // TODO: Should also verify that ILogger.Warning is invoked with the exception to ensure
        // the catch block actually logs rather than silently discarding exceptions.
        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider unavailable"));

        var act = () => _mockLabelService.Object.TrySwapLabelAsync(
            "provider-1", "org/repo#42", AgentLabels.Error,
            LabelTargetKind.Issue, _logger, "TestContext", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TrySwapLabelAsync_OperationCanceledException_Propagates()
    {
        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _mockLabelService.Object.TrySwapLabelAsync(
            "provider-1", "org/repo#42", AgentLabels.Error,
            LabelTargetKind.Issue, _logger, "TestContext", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TrySwapLabelAsync_WithPipelineRun_UsesProviderConfigIdForLabel()
    {
        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Review run should use RepoProviderConfigId (not IssueProviderConfigId)
        var run = PipelineRun.CreateReview(new PipelineRunCreationParams
        {
            RunType = PipelineRunType.Review,
            RunId = "run-ext-review",
            IssueIdentifier = "org/repo#10",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            ReviewPrBranchName = "feature/x",
            ReviewPrTargetBranch = "main"
        });

        await _mockLabelService.Object.TrySwapLabelAsync(
            run, AgentLabels.Cancelled, _logger, "TestContext", CancellationToken.None);

        // Should route to rp-1 (RepoProviderConfigId) for Review runs
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            new ProviderConfigId("rp-1"), "org/repo#10", AgentLabels.Cancelled,
            LabelTargetKind.PullRequest, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task TrySwapLabelAsync_WithPipelineRun_UsesLabelTargetKind()
    {
        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Implementation run should use IssueProviderConfigId and LabelTargetKind.Issue
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-ext-impl",
            IssueIdentifier = "org/repo#20",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        });

        await _mockLabelService.Object.TrySwapLabelAsync(
            run, AgentLabels.InProgress, _logger, "TestContext", CancellationToken.None);

        // Should route to ip-1 (IssueProviderConfigId) with LabelTargetKind.Issue
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            new ProviderConfigId("ip-1"), "org/repo#20", AgentLabels.InProgress,
            LabelTargetKind.Issue, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task TrySwapLabelAsync_TaskCanceledException_Propagates()
    {
        // TaskCanceledException derives from OperationCanceledException — should also propagate
        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());

        var act = () => _mockLabelService.Object.TrySwapLabelAsync(
            "provider-1", "org/repo#42", AgentLabels.Error,
            LabelTargetKind.Issue, _logger, "TestContext", CancellationToken.None);

        await act.Should().ThrowAsync<TaskCanceledException>();
    }
}
