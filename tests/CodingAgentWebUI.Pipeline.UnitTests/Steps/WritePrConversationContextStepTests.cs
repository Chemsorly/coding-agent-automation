using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="WritePrConversationContextStep"/>.
/// Verifies rework-mode PR conversation context file writing.
/// </summary>
public sealed class WritePrConversationContextStepTests : IDisposable
{
    private readonly Mock<IRepositoryProvider> _mockRepo = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath;

    public WritePrConversationContextStepTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"write-pr-ctx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    // ── No linked PR ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoLinkedPullRequest_ReturnsContinueWithoutCallingRepo()
    {
        var run = MakeRun(prNumber: null, reviewPrAuthor: null);
        var context = BuildContext(run);
        var step = new WritePrConversationContextStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _mockRepo.Verify(r => r.ListPullRequestCommentsAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Success path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LinkedPrWithComments_WritesPrConversationContextFile()
    {
        var run = MakeRun(prNumber: 42, reviewPrAuthor: "alice");

        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(42, "alice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrConversationComment>
            {
                new() { Author = "alice", Body = "LGTM", CreatedAt = DateTime.UtcNow, IsBot = false, IsAuthor = true }
            });

        var context = BuildContext(run);
        var step = new WritePrConversationContextStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        var filePath = Path.Combine(_workspacePath, AgentWorkspacePaths.PrConversationContextFilePath);
        File.Exists(filePath).Should().BeTrue("conversation context file should have been written");
    }

    [Fact]
    public async Task ExecuteAsync_NullReviewPrAuthor_PassesEmptyStringToListComments()
    {
        var run = MakeRun(prNumber: 10, reviewPrAuthor: null); // null → should fall back to ""

        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(10, "", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrConversationComment>());

        var context = BuildContext(run);
        var step = new WritePrConversationContextStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _mockRepo.Verify(r => r.ListPullRequestCommentsAsync(10, "", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Exception / catch path (lines 41-47) ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RepoThrows_ReturnsContinue_NonFatal()
    {
        var run = MakeRun(prNumber: 99, reviewPrAuthor: null);

        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(99, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("GitHub API unavailable"));

        var context = BuildContext(run);
        var step = new WritePrConversationContextStep();

        // Must not throw — step is non-fatal
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue, "exception should be swallowed and pipeline should continue");
    }

    [Fact]
    public async Task ExecuteAsync_RepoThrowsInvalidOperation_ReturnsContinue_NonFatal()
    {
        var run = MakeRun(prNumber: 55, reviewPrAuthor: null);

        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(55, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rate limited"));

        var context = BuildContext(run);
        var step = new WritePrConversationContextStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        // No context file should have been written
        var filePath = Path.Combine(_workspacePath, AgentWorkspacePaths.PrConversationContextFilePath);
        File.Exists(filePath).Should().BeFalse("file should not be written when fetching fails");
    }

    [Fact]
    public async Task ExecuteAsync_OperationCanceled_PropagatesException()
    {
        var run = MakeRun(prNumber: 77, reviewPrAuthor: null);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(77, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext(run);
        var step = new WritePrConversationContextStep();

        // OperationCanceledException should propagate (not swallowed by catch block)
        await step.Invoking(s => s.ExecuteAsync(context, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PipelineStepContext BuildContext(PipelineRun run)
    {
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = _mockRepo.Object,
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = Mock.Of<IPipelineCallbacks>(),
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }

    private PipelineRun MakeRun(int? prNumber, string? reviewPrAuthor) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "owner/repo#1",
        IssueTitle = "Test issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = _workspacePath,
        LinkedPullRequest = prNumber.HasValue
            ? new LinkedPullRequest { Number = prNumber.Value, Url = $"https://github.com/o/r/pull/{prNumber.Value}", BranchName = $"fix/{prNumber.Value}", IsDraft = false }
            : null,
        ReviewPrAuthor = reviewPrAuthor
    };
}
