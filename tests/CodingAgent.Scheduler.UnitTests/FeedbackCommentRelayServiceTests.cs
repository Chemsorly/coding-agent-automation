using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using Moq;
using Xunit;
using ILeaderGate = CodingAgent.Pipeline.Interfaces.ILeaderGate;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Unit tests for <see cref="FeedbackCommentRelayService"/>.
/// Tests call the internal <see cref="FeedbackCommentRelayService.SweepOnceForTestAsync"/> method directly
/// rather than running through BackgroundService timing, to avoid thread-scheduling races.
/// </summary>
// TODO: Add a test for the config-load failure fallback path in LoadMaxAttemptsAsync:
//   when GetPipelineConfigAsync throws, the sweep should still proceed using DefaultFeedbackCommentOutboxMaxAttempts (5).
//   Without this test, a change that aborts the sweep on config failure would go undetected.
//
// TODO: Add a test for the null-Description / null-FormatComment path in ProcessEntryAsync:
//   an entry whose FeedbackJson deserializes to an IssueFeedback with Description=null should be
//   marked Completed without calling PostCommentAsync. This ensures the "no-deliverable-body"
//   guard does not silently change to MarkFailed or throw in a future refactor.
public sealed class FeedbackCommentRelayServiceTests
{
    private readonly Mock<IPipelineApiFeedbackCommentOutboxClient> _outboxClient = new();
    private readonly Mock<IProviderFactory> _providerFactory = new();
    private readonly Mock<IPipelineApiConfigClient> _configClient = new();
    private readonly Mock<ILogger> _logger = new();

    public FeedbackCommentRelayServiceTests()
    {
        // ForContext<T>() must return a non-null logger so the relay doesn't NullRef.
        _logger.Setup(l => l.ForContext<FeedbackCommentRelayService>()).Returns(_logger.Object);
        _logger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>())).Returns(_logger.Object);
    }

    private FeedbackCommentRelayService CreateSut(ILeaderGate? leaderGate = null) =>
        new(
            _outboxClient.Object,
            _providerFactory.Object,
            _configClient.Object,
            leaderGate,
            _logger.Object,
            gracePeriod: TimeSpan.Zero,
            sweepInterval: TimeSpan.FromMilliseconds(10));

    private static FeedbackCommentOutboxEntry MakeEntry(
        string runId = "run-1",
        string? pullRequestNumber = null,
        string? description = "Issue is unclear") =>
        new()
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            IssueProviderConfigId = "github",
            IssueIdentifier = "GH-42",
            RepoProviderConfigId = "github-repo",
            PullRequestNumber = pullRequestNumber,
            FeedbackJson = JsonSerializer.Serialize(
                new IssueFeedback { Description = description },
                PipelineJsonOptions.Default),
            Status = "Pending",
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private void SetupConfig(int maxAttempts = 5)
    {
        _configClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { FeedbackCommentOutboxMaxAttempts = maxAttempts });
    }

    private void SetupNoProviders()
    {
        _configClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    // ── Test: pending → posts → marks completed ────────────────────────────

    [Fact]
    public async Task Pending_PostsComment_MarksCompleted()
    {
        var entry = MakeEntry();
        SetupConfig();

        _outboxClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([entry]);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-123");
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var issueConfig = new ProviderConfig { Id = "github", Kind = ProviderKind.Issue, DisplayName = "GitHub", ProviderType = "GitHub" };
        _configClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync([issueConfig]);
        _configClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _providerFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);

        await CreateSut().SweepOnceForTestAsync(CancellationToken.None);

        // TODO: Add a Verify on mockIssueProvider.PostCommentAsync to assert it was called with
        // the correct IssueIdentifier ("GH-42") and a non-empty comment body. Without it, a change
        // that marks entries completed without actually posting would not be caught by this test.
        _outboxClient.Verify(c => c.MarkCompletedAsync(entry.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Test: provider throws → stays pending, AttemptCount++ ─────────────

    [Fact]
    public async Task ProviderThrows_CallsMarkFailed()
    {
        var entry = MakeEntry();
        SetupConfig(maxAttempts: 5);

        _outboxClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([entry]);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider error"));
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var issueConfig = new ProviderConfig { Id = "github", Kind = ProviderKind.Issue, DisplayName = "GitHub", ProviderType = "GitHub" };
        _configClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync([issueConfig]);
        _configClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _providerFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);

        await CreateSut().SweepOnceForTestAsync(CancellationToken.None);

        _outboxClient.Verify(
            c => c.MarkFailedAsync(entry.Id, It.IsAny<string>(), 5, It.IsAny<CancellationToken>()),
            Times.Once);
        _outboxClient.Verify(c => c.MarkCompletedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test: max attempts reached → GetPending called with correct maxAttempts ──

    [Fact]
    public async Task MaxAttemptsReached_GetPendingCalledWithConfiguredMaxAttempts()
    {
        // TODO: This test verifies that maxAttempts is propagated to GetPendingAsync when the
        // queue is empty, but does NOT test that an exhausted entry (AttemptCount >= maxAttempts)
        // is excluded from a sweep. That exclusion is enforced by the GetPendingAsync filter in
        // PostgresFeedbackCommentOutboxStore (Status=Pending AND AttemptCount<maxAttempts), which
        // is not exercised here because the outbox client is mocked. A store-level test is needed
        // to verify this invariant.
        SetupConfig(maxAttempts: 7);
        _outboxClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        SetupNoProviders();

        await CreateSut().SweepOnceForTestAsync(CancellationToken.None);

        // Verify GetPending was called with the configured maxAttempts (7, not default 5)
        _outboxClient.Verify(
            c => c.GetPendingAsync(7, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _outboxClient.Verify(c => c.MarkCompletedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _outboxClient.Verify(c => c.MarkFailedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test: leader gate → sweep skipped when not leader ─────────────────

    [Fact]
    public async Task NotLeader_SweepSkipped()
    {
        var leaderGate = new Mock<ILeaderGate>();
        leaderGate.Setup(g => g.IsLeader).Returns(false);
        // Leader token is used in linked CancellationTokenSource — provide a non-cancelled token
        leaderGate.Setup(g => g.LeaderToken).Returns(CancellationToken.None);

        await CreateSut(leaderGate: leaderGate.Object).SweepOnceForTestAsync(CancellationToken.None);

        _outboxClient.Verify(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test: PR-body append → feedback link appended on success ──────────

    [Fact]
    public async Task WithPullRequestNumber_AppendsFeedbackLinkToPrBody()
    {
        var entry = MakeEntry(pullRequestNumber: "47");
        SetupConfig();

        _outboxClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([entry]);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        const string commentUrl = "https://github.com/org/repo/issues/42#issuecomment-999";
        mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(commentUrl);
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.GetPullRequestBodyAsync(47, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Original PR body");
        mockRepoProvider.Setup(p => p.UpdatePullRequestAsync(47, It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var issueConfig = new ProviderConfig { Id = "github", Kind = ProviderKind.Issue, DisplayName = "GitHub", ProviderType = "GitHub" };
        var repoConfig = new ProviderConfig { Id = "github-repo", Kind = ProviderKind.Repository, DisplayName = "GitHub Repo", ProviderType = "GitHub" };
        _configClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync([issueConfig]);
        _configClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync([repoConfig]);
        _providerFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);
        _providerFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepoProvider.Object);

        await CreateSut().SweepOnceForTestAsync(CancellationToken.None);

        mockRepoProvider.Verify(
            p => p.UpdatePullRequestAsync(
                47,
                It.Is<string>(body => body.Contains("## Agent Feedback") && body.Contains(commentUrl)),
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
        _outboxClient.Verify(c => c.MarkCompletedAsync(entry.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Test: PR-body idempotency → append skipped when marker already present ──

    [Fact]
    public async Task PrBodyAlreadyHasFeedbackMarker_AppendSkipped()
    {
        var entry = MakeEntry(pullRequestNumber: "47");
        SetupConfig();

        _outboxClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([entry]);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-999");
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        // Remote PR body already contains the feedback section
        mockRepoProvider.Setup(p => p.GetPullRequestBodyAsync(47, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Original body\n\n## Agent Feedback\n⚠️ Already present.");
        mockRepoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var issueConfig = new ProviderConfig { Id = "github", Kind = ProviderKind.Issue, DisplayName = "GitHub", ProviderType = "GitHub" };
        var repoConfig = new ProviderConfig { Id = "github-repo", Kind = ProviderKind.Repository, DisplayName = "GitHub Repo", ProviderType = "GitHub" };
        _configClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync([issueConfig]);
        _configClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync([repoConfig]);
        _providerFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);
        _providerFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepoProvider.Object);

        await CreateSut().SweepOnceForTestAsync(CancellationToken.None);

        // UpdatePullRequestAsync must NOT have been called — marker already present
        mockRepoProvider.Verify(
            p => p.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // But the outbox entry should still be marked completed (comment was posted successfully)
        _outboxClient.Verify(c => c.MarkCompletedAsync(entry.Id, It.IsAny<CancellationToken>()), Times.Once);
    }
}
