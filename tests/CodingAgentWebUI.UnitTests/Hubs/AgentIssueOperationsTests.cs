using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Unit tests for <see cref="AgentIssueOperations"/> covering uncovered branches:
/// - PostCommentViaIssueProviderAsync: null config, exception path
/// - PostIssueFeedbackCommentAsync: null feedback, null formatted comment, exception path
/// - AppendFeedbackLinkToPrBodyAsync: idempotency guard, null repo config, unparseable PR number,
///   remote body already contains section, exception in append
/// </summary>
public sealed class AgentIssueOperationsTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<ILogger> _logger = new();

    private AgentIssueOperations CreateOps() =>
        new(_facade.Object, _labelService.Object, _logger.Object);

    private static PipelineRun MakeRun(
        string issueId = "org/repo#42",
        string issueCfgId = "issue-cfg-1",
        string repoCfgId = "repo-cfg-1",
        string? prNumber = null,
        string? prBody = null) => new()
        {
            RunId = "run-1",
            IssueIdentifier = issueId,
            IssueTitle = "Test",
            IssueProviderConfigId = issueCfgId,
            RepoProviderConfigId = repoCfgId,
            PullRequestNumber = prNumber,
            PullRequestBody = prBody
            // LabelTargetKind is computed from RunType — defaults to Issue
        };

    // ── SwapLabelAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SwapLabelAsync_DelegatesToLabelService()
    {
        var run = MakeRun();
        var ops = CreateOps();

        await ops.SwapLabelAsync(run, AgentLabels.Done);

        _labelService.Verify(s => s.SwapLabelAsync(
            run.ProviderConfigIdForLabel,
            run.IssueIdentifier,
            AgentLabels.Done,
            run.LabelTargetKind,
            CancellationToken.None), Times.Once);
    }

    // ── PostCommentViaIssueProviderAsync — null config ─────────────────────

    [Fact]
    public async Task PostCommentViaIssueProviderAsync_NullConfig_ReturnsNull()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetProviderConfigByIdAsync(run.IssueProviderConfigId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var ops = CreateOps();
        var result = await ops.PostCommentViaIssueProviderAsync(run, "body");

        result.Should().BeNull();
    }

    // ── PostCommentViaIssueProviderAsync — provider throws ─────────────────

    [Fact]
    public async Task PostCommentViaIssueProviderAsync_ProviderThrows_ReturnsNull()
    {
        var run = MakeRun();
        var config = new ProviderConfig
        {
            Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test"
        };
        _facade.Setup(f => f.GetProviderConfigByIdAsync(run.IssueProviderConfigId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API rate limit"));
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var ops = CreateOps();
        var result = await ops.PostCommentViaIssueProviderAsync(run, "body");

        result.Should().BeNull();
    }

    // ── PostCommentViaIssueProviderAsync — success ─────────────────────────

    [Fact]
    public async Task PostCommentViaIssueProviderAsync_Success_ReturnsCommentUrl()
    {
        var run = MakeRun();
        var config = new ProviderConfig
        {
            Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test"
        };
        _facade.Setup(f => f.GetProviderConfigByIdAsync(run.IssueProviderConfigId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.PostCommentAsync("org/repo#42", "body", It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-1");
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var ops = CreateOps();
        var result = await ops.PostCommentViaIssueProviderAsync(run, "body");

        result.Should().Be("https://github.com/org/repo/issues/42#issuecomment-1");
    }

    // ── PostIssueFeedbackCommentAsync — null feedback ──────────────────────

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_NullFeedback_DoesNotPost()
    {
        // FeedbackCommentFormatter.FormatComment(null) returns null — early exit
        var run = MakeRun();
        run.Feedback = null;

        var ops = CreateOps();
        await ops.PostIssueFeedbackCommentAsync(run);

        _facade.Verify(f => f.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── PostIssueFeedbackCommentAsync — null IssueFeedback ─────────────────

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_NullIssueFeedback_DoesNotPost()
    {
        // run.Feedback.Issue is null → FeedbackCommentFormatter.FormatComment(null) returns null
        var run = MakeRun();
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = null
        };

        var ops = CreateOps();
        await ops.PostIssueFeedbackCommentAsync(run);

        _facade.Verify(f => f.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── PostIssueFeedbackCommentAsync — no PR number → no append ──────────

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_NoPullRequestNumber_DoesNotAppendToPr()
    {
        var run = MakeRun(prNumber: null);
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = "Some feedback" }
        };

        var config = new ProviderConfig { Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-1");
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var ops = CreateOps();
        await ops.PostIssueFeedbackCommentAsync(run);

        // Comment was posted but no repo provider should be created (no PR number)
        _facade.Verify(f => f.GetProviderConfigByIdAsync("repo-cfg-1", ProviderKind.Repository, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── PostIssueFeedbackCommentAsync — exception swallowed ────────────────

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_Exception_IsSwallowed()
    {
        var run = MakeRun();
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = "feedback" }
        };

        // GetProviderConfigByIdAsync throws — outer catch must swallow
        _facade.Setup(f => f.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB failure"));

        var ops = CreateOps();
        var act = async () => await ops.PostIssueFeedbackCommentAsync(run);

        await act.Should().NotThrowAsync("PostIssueFeedbackCommentAsync must swallow all exceptions");
    }

    // ── AppendFeedbackLinkToPrBodyAsync — idempotency guard (local body) ───

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_PrBodyAlreadyHasFeedbackSection_SkipsAppend()
    {
        var run = MakeRun(prNumber: "99", prBody: "Existing body\n\n## Agent Feedback\nAlready here.");
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = "feedback text" }
        };

        var issueConfig = new ProviderConfig { Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-2");
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockProvider.Object);

        var ops = CreateOps();
        await ops.PostIssueFeedbackCommentAsync(run);

        // Repo provider should NOT be created — idempotency guard fired
        _facade.Verify(f => f.GetProviderConfigByIdAsync("repo-cfg-1", ProviderKind.Repository, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── AppendFeedbackLinkToPrBodyAsync — null repo config ─────────────────

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_NullRepoConfig_DoesNotThrow()
    {
        var run = MakeRun(prNumber: "99", prBody: "Clean PR body");
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = "feedback text" }
        };

        var issueConfig = new ProviderConfig { Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-3");
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockProvider.Object);

        // Repo config not found
        _facade.Setup(f => f.GetProviderConfigByIdAsync("repo-cfg-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var ops = CreateOps();
        var act = async () => await ops.PostIssueFeedbackCommentAsync(run);

        await act.Should().NotThrowAsync("missing repo config should be handled gracefully");
        // Repo provider must not be created
        _facade.Verify(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()), Times.Never);
    }

    // ── AppendFeedbackLinkToPrBodyAsync — unparseable PR number ────────────

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_UnparseablePrNumber_DoesNotThrow()
    {
        var run = MakeRun(prNumber: "not-a-number", prBody: "Clean PR body");
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = "feedback" }
        };

        var issueConfig = new ProviderConfig { Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-4");
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockProvider.Object);

        var repoConfig = new ProviderConfig { Id = "repo-cfg-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("repo-cfg-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);

        var ops = CreateOps();
        var act = async () => await ops.PostIssueFeedbackCommentAsync(run);

        await act.Should().NotThrowAsync("unparseable PR number should be handled gracefully");
        // Repo provider must not be created (int.TryParse returns before CreateRepositoryProvider)
        _facade.Verify(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()), Times.Never);
    }

    // ── AppendFeedbackLinkToPrBodyAsync — remote body already has section ──

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_RemoteBodyAlreadyHasFeedbackSection_SkipsUpdate()
    {
        // local PullRequestBody is clean but provider returns body that already has the section
        var run = MakeRun(prNumber: "55", prBody: "clean local body");
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = "feedback" }
        };

        var issueConfig = new ProviderConfig { Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-5");
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);

        var repoConfig = new ProviderConfig { Id = "repo-cfg-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("repo-cfg-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        // Remote body already has the section
        mockRepoProvider.Setup(r => r.GetPullRequestBodyAsync(55, It.IsAny<CancellationToken>()))
            .ReturnsAsync("remote body\n\n## Agent Feedback\nAlready appended.");
        mockRepoProvider.Setup(r => r.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepoProvider.Object);

        var ops = CreateOps();
        await ops.PostIssueFeedbackCommentAsync(run);

        // UpdatePullRequestAsync must NOT be called (remote body already has feedback)
        mockRepoProvider.Verify(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── AppendFeedbackLinkToPrBodyAsync — append succeeds ──────────────────

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_HappyPath_AppendsFeedbackSectionToPr()
    {
        var run = MakeRun(prNumber: "77", prBody: "PR body");
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = "The implementation skipped AC #3." }
        };

        var issueConfig = new ProviderConfig { Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-99");
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);

        var repoConfig = new ProviderConfig { Id = "repo-cfg-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("repo-cfg-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.GetPullRequestBodyAsync(77, It.IsAny<CancellationToken>()))
            .ReturnsAsync("PR body");
        mockRepoProvider.Setup(r => r.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepoProvider.Object);

        var ops = CreateOps();
        await ops.PostIssueFeedbackCommentAsync(run);

        mockRepoProvider.Verify(r => r.UpdatePullRequestAsync(
            77,
            It.Is<string>(body => body.Contains("## Agent Feedback") && body.Contains("issuecomment-99")),
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── AppendFeedbackLinkToPrBodyAsync — exception in append is swallowed ─

    [Fact]
    public async Task PostIssueFeedbackCommentAsync_AppendThrows_IsSwallowed()
    {
        var run = MakeRun(prNumber: "88", prBody: "PR body");
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = "feedback" }
        };

        var issueConfig = new ProviderConfig { Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-88");
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);

        var repoConfig = new ProviderConfig { Id = "repo-cfg-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("repo-cfg-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.GetPullRequestBodyAsync(88, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("GitHub API unavailable"));
        mockRepoProvider.Setup(r => r.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepoProvider.Object);

        var ops = CreateOps();
        var act = async () => await ops.PostIssueFeedbackCommentAsync(run);

        await act.Should().NotThrowAsync("append failure must be swallowed — non-fatal operation");
    }
}
