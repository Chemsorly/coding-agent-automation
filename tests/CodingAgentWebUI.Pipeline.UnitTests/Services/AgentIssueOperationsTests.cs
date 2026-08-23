using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for AgentIssueOperations.
/// Covers: SwapLabelAsync delegation, PostCommentViaIssueProviderAsync (found/not-found/exception),
/// PostIssueFeedbackCommentAsync (null feedback, exception swallowing).
/// </summary>
public sealed class AgentIssueOperationsTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly AgentIssueOperations _sut;

    public AgentIssueOperationsTests()
    {
        _sut = new AgentIssueOperations(_facade.Object, _labelService.Object, _logger.Object);
    }

    private static PipelineRun MakeRun(string runId = "run-1") =>
        PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "GH-42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });

    private static ProviderConfig MakeConfig(string id = "github") =>
        new() { Id = id, Kind = ProviderKind.Issue, DisplayName = "GitHub", ProviderType = "GitHub" };

    // ── SwapLabelAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task SwapLabelAsync_DelegatesToLabelService()
    {
        _labelService.Setup(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
            AgentLabels.Done, It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = MakeRun();
        await _sut.SwapLabelAsync(run, AgentLabels.Done);

        _labelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
            AgentLabels.Done, It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── PostCommentViaIssueProviderAsync ──────────────────────────────────

    [Fact]
    public async Task PostCommentViaIssueProviderAsync_WhenConfigNotFound_ReturnsNull()
    {
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var result = await _sut.PostCommentViaIssueProviderAsync(MakeRun(), "comment body");

        result.Should().BeNull();
    }

    [Fact]
    public async Task PostCommentViaIssueProviderAsync_WhenProviderThrows_ReturnsNull()
    {
        var config = MakeConfig();
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider down"));
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _facade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var result = await _sut.PostCommentViaIssueProviderAsync(MakeRun(), "body");

        result.Should().BeNull(); // exception swallowed
    }

    [Fact]
    public async Task PostCommentViaIssueProviderAsync_WhenSucceeds_ReturnsCommentUrl()
    {
        var config = MakeConfig();
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), "body", It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/comment/1");
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _facade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var result = await _sut.PostCommentViaIssueProviderAsync(MakeRun(), "body");

        result.Should().Be("https://github.com/comment/1");
    }

    // ── PostIssueFeedbackCommentAsync ─────────────────────────────────────

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_WhenNullFeedback_DoesNothing()
    {
        var run = MakeRun();
        run.Feedback = null;

        // Should not call GetProviderConfigByIdAsync at all
        var act = () => _sut.PostIssueFeedbackCommentAsync(run);
        await act.Should().NotThrowAsync();

        _facade.Verify(f => f.GetProviderConfigByIdAsync(
            It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_WhenFeedbackCommentIsNull_DoesNothing()
    {
        var run = MakeRun();
        run.Feedback = null; // no feedback at all

        var act = () => _sut.PostIssueFeedbackCommentAsync(run);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_WhenExceptionThrown_IsSwallowed()
    {
        // Trigger via PostCommentViaIssueProviderAsync throwing internally
        // Use a run with no Feedback → FeedbackCommentFormatter returns null → returns early (safe path)
        // To hit the exception path, the config lookup must throw
        var run = MakeRun();
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback() // non-null issue feedback triggers comment posting
        };

        _facade.Setup(f => f.GetProviderConfigByIdAsync(
            It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db error"));

        var act = () => _sut.PostIssueFeedbackCommentAsync(run);
        await act.Should().NotThrowAsync(); // outer catch swallows it
    }
}
