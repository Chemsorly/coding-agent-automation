using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="PrConversationContextWriter"/>.
/// Directly asserts the best-effort failure policy and the fetch-format-write sequence
/// on the shared helper (Acceptance Criterion 3).
/// </summary>
public sealed class PrConversationContextWriterTests : IDisposable
{
    private readonly Mock<IRepositoryProvider> _mockRepo = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath;

    public PrConversationContextWriterTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"pr-conv-writer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_HappyPath_WritesFileWithFormattedContent()
    {
        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(42, "alice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrConversationComment>
            {
                new() { Author = "alice", Body = "LGTM", CreatedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), IsBot = false, IsAuthor = true }
            });

        var context = BuildContext(prNumber: 42, reviewPrAuthor: "alice");

        await PrConversationContextWriter.WriteAsync(context, 42, CancellationToken.None);

        var filePath = Path.Combine(_workspacePath, AgentWorkspacePaths.PrConversationContextFilePath);
        File.Exists(filePath).Should().BeTrue("context file should be written on success");
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("LGTM");
        content.Should().Contain("[HUMAN/AUTHOR] @alice");
    }

    // ── Author handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_NullReviewPrAuthor_PassesEmptyStringToRepo()
    {
        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(10, "", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrConversationComment>());

        var context = BuildContext(prNumber: 10, reviewPrAuthor: null);

        await PrConversationContextWriter.WriteAsync(context, 10, CancellationToken.None);

        _mockRepo.Verify(r => r.ListPullRequestCommentsAsync(10, "", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Review thread content ─────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_CommentsWithFilePath_WritesReviewThreadContent()
    {
        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(7, "", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrConversationComment>
            {
                new()
                {
                    Author = "reviewer",
                    Body = "This needs a null check.",
                    CreatedAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
                    IsBot = false,
                    IsAuthor = false,
                    FilePath = "src/Foo.cs",
                    Line = 42,
                    IsResolved = false
                }
            });

        var context = BuildContext(prNumber: 7, reviewPrAuthor: null);

        await PrConversationContextWriter.WriteAsync(context, 7, CancellationToken.None);

        var filePath = Path.Combine(_workspacePath, AgentWorkspacePaths.PrConversationContextFilePath);
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("## Review Thread Comments");
        content.Should().Contain("src/Foo.cs");
        content.Should().Contain("This needs a null check.");
    }

    // ── Directory creation ────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_CreatesAgentDirectoryIfNotExists()
    {
        // Delete the .agent subdir to confirm CreateDirectory is called
        var agentDir = Path.Combine(_workspacePath, ".agent");
        if (Directory.Exists(agentDir))
            Directory.Delete(agentDir, recursive: true);

        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(5, "", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrConversationComment>());

        var context = BuildContext(prNumber: 5, reviewPrAuthor: null);

        await PrConversationContextWriter.WriteAsync(context, 5, CancellationToken.None);

        Directory.Exists(agentDir).Should().BeTrue("WriteAsync must create the .agent directory");
        var filePath = Path.Combine(_workspacePath, AgentWorkspacePaths.PrConversationContextFilePath);
        File.Exists(filePath).Should().BeTrue();
    }

    // ── Best-effort failure policy ────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_NonCancellationException_SwallowsAndReturnsNormally()
    {
        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(99, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("GitHub API unavailable"));

        var context = BuildContext(prNumber: 99, reviewPrAuthor: null);

        // Must not throw — best-effort policy swallows non-cancellation exceptions
        var act = async () => await PrConversationContextWriter.WriteAsync(context, 99, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteAsync_InvalidOperationException_SwallowsAndReturnsNormally()
    {
        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(55, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rate limited"));

        var context = BuildContext(prNumber: 55, reviewPrAuthor: null);

        var act = async () => await PrConversationContextWriter.WriteAsync(context, 55, CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Context file must not be written when fetch failed
        var filePath = Path.Combine(_workspacePath, AgentWorkspacePaths.PrConversationContextFilePath);
        File.Exists(filePath).Should().BeFalse("file should not exist when repo call threw");
    }

    [Fact]
    public async Task WriteAsync_OperationCanceledException_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _mockRepo
            .Setup(r => r.ListPullRequestCommentsAsync(77, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext(prNumber: 77, reviewPrAuthor: null);

        // OperationCanceledException must NOT be swallowed by the catch filter
        var act = async () => await PrConversationContextWriter.WriteAsync(context, 77, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PipelineStepContext BuildContext(int prNumber, string? reviewPrAuthor) =>
        new()
        {
            Run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = prNumber.ToString(),
                IssueTitle = $"PR #{prNumber}",
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "rp-1",
                WorkspacePath = _workspacePath,
                ReviewPrAuthor = reviewPrAuthor
            },
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
