using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Tests for <see cref="AgentIssueOperations"/> — the null-body fallback path in
/// <c>AppendFeedbackLinkToPrBodyAsync</c> where <c>GetPullRequestBodyAsync</c> returns
/// <c>null</c> and the method falls back to <c>run.PullRequestBody ?? ""</c>.
/// Also verifies that <c>run.PullRequestBody</c> is mutated after a successful append.
/// </summary>
public sealed class AgentIssueOperationsNullBodyTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<ILogger> _logger = new();

    private AgentIssueOperations CreateOps() =>
        new(_facade.Object, _labelService.Object, _logger.Object);

    private static RunFeedback WithIssueFeedback(string description = "Some feedback") =>
        new()
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = description }
        };

    private void SetupIssueProvider(string issueConfigId = "ic-1",
        string commentUrl = "https://github.com/org/repo/issues/1#issuecomment-1")
    {
        var issueConfig = new ProviderConfig { Id = issueConfigId, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync(issueConfigId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(commentUrl);
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockProvider.Object);
    }

    // ── GetPullRequestBodyAsync returns null → falls back to run.PullRequestBody ──

    [Fact]
    public async Task AppendFeedback_GetPullRequestBodyReturnsNull_FallsBackToRunPullRequestBody()
    {
        // Arrange: run has a local PullRequestBody; remote returns null
        var run = new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "T",
            IssueProviderConfigId = "ic-1",
            RepoProviderConfigId = "rc-1",
            PullRequestNumber = "42",
            PullRequestBody = "Local PR body without feedback section"
        };
        run.Feedback = WithIssueFeedback();

        SetupIssueProvider();

        var repoConfig = new ProviderConfig { Id = "rc-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("rc-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);

        var mockRepo = new Mock<IRepositoryProvider>();
        // Remote body returns null — must fall back to run.PullRequestBody
        mockRepo.Setup(r => r.GetPullRequestBodyAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        mockRepo.Setup(r => r.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo.Object);

        var ops = CreateOps();

        // Act
        await ops.PostIssueFeedbackCommentAsync(run);

        // Assert: UpdatePullRequestAsync is called with the local PullRequestBody as the base
        mockRepo.Verify(r => r.UpdatePullRequestAsync(
            42,
            It.Is<string>(body =>
                body.Contains("Local PR body without feedback section") &&
                body.Contains("## Agent Feedback")),
            false,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AppendFeedback_GetPullRequestBodyReturnsNull_AndRunBodyIsNull_UsesEmptyString()
    {
        // Both remote and local are null — must fall back to "" (not throw)
        var run = new PipelineRun
        {
            RunId = "run-2",
            IssueIdentifier = "org/repo#2",
            IssueTitle = "T",
            IssueProviderConfigId = "ic-1",
            RepoProviderConfigId = "rc-1",
            PullRequestNumber = "99",
            PullRequestBody = null  // local also null
        };
        run.Feedback = WithIssueFeedback();

        SetupIssueProvider();

        var repoConfig = new ProviderConfig { Id = "rc-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("rc-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.GetPullRequestBodyAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        mockRepo.Setup(r => r.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo.Object);

        var ops = CreateOps();

        // Must not throw — null ?? "" gives empty string as the base body
        var act = async () => await ops.PostIssueFeedbackCommentAsync(run);
        await act.Should().NotThrowAsync("null remote and null local body must produce a valid empty-base append, not a crash");

        // The PR update should still happen with just the feedback section
        mockRepo.Verify(r => r.UpdatePullRequestAsync(
            99,
            It.Is<string>(body => body.Contains("## Agent Feedback")),
            false,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── run.PullRequestBody is mutated after successful append ────────────

    [Fact]
    public async Task AppendFeedback_SuccessfulAppend_MutatesRunPullRequestBody()
    {
        var run = new PipelineRun
        {
            RunId = "run-3",
            IssueIdentifier = "org/repo#3",
            IssueTitle = "T",
            IssueProviderConfigId = "ic-1",
            RepoProviderConfigId = "rc-1",
            PullRequestNumber = "77",
            PullRequestBody = "Original body"
        };
        run.Feedback = WithIssueFeedback("Great job on AC #1.");

        SetupIssueProvider(commentUrl: "https://github.com/org/repo/issues/3#issuecomment-77");

        var repoConfig = new ProviderConfig { Id = "rc-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" };
        _facade.Setup(f => f.GetProviderConfigByIdAsync("rc-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.GetPullRequestBodyAsync(77, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Original body");  // remote matches local
        mockRepo.Setup(r => r.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _facade.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo.Object);

        var ops = CreateOps();
        await ops.PostIssueFeedbackCommentAsync(run);

        // run.PullRequestBody must be updated to include the feedback section
        run.PullRequestBody.Should().Contain("## Agent Feedback",
            "AppendFeedbackLinkToPrBodyAsync must mutate run.PullRequestBody after successful UpdatePullRequestAsync");
        run.PullRequestBody.Should().Contain("issuecomment-77",
            "The appended body must reference the comment URL");
    }
}
