using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="PostReviewFindingsStep"/>.
/// Tests no-reviewer-match, existing review detection/update, and non-fatal API failure.
/// Feature: 025-pr-review-pipeline, Requirements: Req 5, 7
/// </summary>
public class PostReviewFindingsStepTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IRepositoryProvider> _repoProvider = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly List<CancellationTokenSource> _tokenSources = new();

    private PipelineStepContext BuildContext(PipelineRun run)
    {
        var cts = new CancellationTokenSource();
        _tokenSources.Add(cts);
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = _repoProvider.Object,
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = cts,
            ProviderConfigStore = Mock.Of<IConfigurationStore>(),
            QualityGateConfigStore = Mock.Of<IConfigurationStore>(),
            ReviewerConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }

    [Fact]
    public async Task ExecuteAsync_NoReviewerMatch_PostsNoApplicableReviewersComment()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = Array.Empty<string>() // No reviewers matched
        };

        _repoProvider.Setup(r => r.FindExistingReviewCommentAsync(42, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var context = BuildContext(run);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // Should post a comment indicating no applicable reviewers
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            42,
            It.Is<string>(body => body.Contains("No applicable reviewers found")),
            PullRequestReviewType.Comment,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ExistingReview_UpdatesInsteadOfPostingNew()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "55",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = new[] { "SecurityBot" }
        };
        run.SetCodeReviewCounts(1, 0, 0);
        run.CodeReviewAgentFindings["SecurityBot"] = "Found SQL injection";

        // Simulate existing review comment found on first call, then null (collapsed)
        var callCount = 0;
        _repoProvider.Setup(r => r.FindExistingReviewCommentAsync(55, CommentMarkers.PrReview, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref callCount) == 1 ? 12345L : null);

        var context = BuildContext(run);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // Should collapse existing comment, then post new with RequestChanges (criticals present)
        _repoProvider.Verify(r => r.UpdateReviewCommentAsync(
            55, 12345L, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            55, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoExistingReview_PostsNewReview()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "60",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = new[] { "StyleBot" }
        };
        run.SetCodeReviewCounts(0, 2, 0);
        run.CodeReviewAgentFindings["StyleBot"] = "Naming convention issues";

        // No existing review found
        _repoProvider.Setup(r => r.FindExistingReviewCommentAsync(60, CommentMarkers.PrReview, It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var context = BuildContext(run);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // Should post new review with RequestChanges (warnings present)
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            60, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()), Times.Once);
        _repoProvider.Verify(r => r.UpdateReviewCommentAsync(
            It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ApiFailure_IsNonFatal_ReturnsContinue()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "70",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = new[] { "Agent1" }
        };
        run.SetCodeReviewCounts(0, 0, 1);
        run.CodeReviewAgentFindings["Agent1"] = "Minor suggestion";

        // Simulate API failure
        _repoProvider.Setup(r => r.FindExistingReviewCommentAsync(70, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<PullRequestReviewType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API rate limit exceeded"));

        var context = BuildContext(run);
        var step = new PostReviewFindingsStep();

        // Should NOT throw — non-fatal
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue,
            "API failure should be non-fatal, step should return Continue");
    }

    [Fact]
    public async Task ExecuteAsync_TransitionsToPostingFindings()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "80",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = Array.Empty<string>()
        };

        _repoProvider.Setup(r => r.FindExistingReviewCommentAsync(80, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var context = BuildContext(run);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        _callbacks.Verify(c => c.TransitionTo(PipelineStep.PostingFindings), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithFindings_PostsFormattedBody()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "90",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = new[] { "SecurityBot", "StyleBot" }
        };
        run.SetCodeReviewCounts(2, 1, 0);
        run.CodeReviewAgentFindings["SecurityBot"] = "SQL injection found";
        run.CodeReviewAgentFindings["StyleBot"] = "Naming issues";

        _repoProvider.Setup(r => r.FindExistingReviewCommentAsync(90, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        string? postedBody = null;
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            90, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()))
            .Callback<int, string, PullRequestReviewType, CancellationToken>((_, body, _, _) => postedBody = body)
            .Returns(Task.CompletedTask);

        var context = BuildContext(run);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        postedBody.Should().NotBeNull();
        postedBody.Should().Contain(CommentMarkers.PrReview);
        postedBody.Should().Contain("Automated Code Review");
        postedBody.Should().Contain("[CRITICAL]");
    }

    public void Dispose()
    {
        foreach (var cts in _tokenSources)
            cts.Dispose();
        (_logger as IDisposable)?.Dispose();
    }
}
