using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Services;

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

/// <summary>
/// Tests for <see cref="IAgentIssueOperations"/> default interface method implementations.
/// Exercises <c>ListOpenIssuesForProviderAsync</c> and <c>ListClosedIssuesForProviderAsync</c>
/// via a minimal concrete stub that does not override those methods, to verify the documented
/// fallback behaviour (delegates to own-tracker methods, ignoring the provider ID).
/// </summary>
public sealed class IAgentIssueOperationsDefaultMethodTests
{
    /// <summary>
    /// Minimal concrete implementation that exposes only the base interface methods.
    /// <see cref="IAgentIssueOperations.ListOpenIssuesForProviderAsync"/> and
    /// <see cref="IAgentIssueOperations.ListClosedIssuesForProviderAsync"/> are NOT overridden
    /// so the default interface implementations are exercised.
    /// </summary>
    private sealed class StubIssueOps : IAgentIssueOperations
    {
        private readonly CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> _openResult;
        private readonly CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> _closedResult;

        public StubIssueOps(
            CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> openResult,
            CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> closedResult)
        {
            _openResult = openResult;
            _closedResult = closedResult;
        }

        public Task<string?> PostCommentAsync(CodingAgent.Pipeline.Models.IssueIdentifier issueIdentifier, string body, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task SwapLabelAsync(CodingAgent.Pipeline.Models.IssueIdentifier issueIdentifier, string newLabel, CancellationToken ct)
            => Task.CompletedTask;

        public Task<CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary>> ListOpenIssuesAsync(
            int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
            => Task.FromResult(_openResult);

        public Task<CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary>> ListClosedIssuesAsync(
            int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since, CancellationToken ct)
            => Task.FromResult(_closedResult);
    }

    private static CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> MakeResult(string title) =>
        new()
        {
            Items = [new CodingAgent.Pipeline.Models.IssueSummary { Identifier = "1", Title = title, Labels = [] }],
            HasMore = false, Page = 1, PageSize = 50
        };

    [Fact]
    public async Task ListOpenIssuesForProviderAsync_DefaultImpl_DelegatesToListOpenIssuesAsync_IgnoringProviderId()
    {
        var expected = MakeResult("Open Issue From Own Tracker");
        IAgentIssueOperations sut = new StubIssueOps(expected, MakeResult("closed"));

        // Calling with a different provider ID — the default impl must silently delegate to own tracker
        var result = await sut.ListOpenIssuesForProviderAsync("some-other-provider", 1, 50, null, CancellationToken.None);

        result.Should().Be(expected, "default ListOpenIssuesForProviderAsync must delegate to ListOpenIssuesAsync");
    }

    [Fact]
    public async Task ListClosedIssuesForProviderAsync_DefaultImpl_DelegatesToListClosedIssuesAsync_IgnoringProviderId()
    {
        var expected = MakeResult("Closed Issue From Own Tracker");
        IAgentIssueOperations sut = new StubIssueOps(MakeResult("open"), expected);

        // Calling with a different provider ID — the default impl must silently delegate to own tracker
        var result = await sut.ListClosedIssuesForProviderAsync("some-other-provider", 1, 50, null, since: null, CancellationToken.None);

        result.Should().Be(expected, "default ListClosedIssuesForProviderAsync must delegate to ListClosedIssuesAsync");
    }

    // ── Default throw paths ───────────────────────────────────────────────
    // A minimal stub that implements only the non-optional members, so that calling any of the
    // throw-by-default methods goes to the interface default implementation.

    private sealed class MinimalStub : IAgentIssueOperations
    {
        public Task<string?> PostCommentAsync(CodingAgent.Pipeline.Models.IssueIdentifier issueIdentifier, string body, CancellationToken ct)
            => Task.FromResult<string?>(null);
        public Task SwapLabelAsync(CodingAgent.Pipeline.Models.IssueIdentifier issueIdentifier, string newLabel, CancellationToken ct)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task CreateIssueAsync_DefaultImpl_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.CreateIssueAsync("T", "B", [], CancellationToken.None));
    }

    [Fact]
    public async Task CreateIssueForProviderAsync_DefaultImpl_DelegatesToCreateIssueAsync_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        // CreateIssueForProviderAsync defaults to calling CreateIssueAsync, which throws
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.CreateIssueForProviderAsync("p", "T", "B", [], CancellationToken.None));
    }

    [Fact]
    public async Task ListOpenIssuesAsync_DefaultImpl_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.ListOpenIssuesAsync(1, 50, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetIssueAsync_DefaultImpl_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.GetIssueAsync(new CodingAgent.Pipeline.Models.IssueIdentifier("1"), CancellationToken.None));
    }

    [Fact]
    public async Task ListCommentsAsync_DefaultImpl_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.ListCommentsAsync(new CodingAgent.Pipeline.Models.IssueIdentifier("1"), CancellationToken.None));
    }

    [Fact]
    public async Task ListClosedIssuesAsync_DefaultImpl_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.ListClosedIssuesAsync(1, 50, null, since: null, CancellationToken.None));
    }
}
