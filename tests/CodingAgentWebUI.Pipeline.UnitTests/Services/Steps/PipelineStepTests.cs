using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

// Decision (Issue #297): Approach 1 selected â€” IAgentPhaseExecutor and IQualityGateExecutor interfaces
// already exist and are fully mockable. Isolated tests for AnalyzeCodeStep, ReviewCodeStep, and
// RunQualityGatesStep are in dedicated test classes below (AnalyzeCodeStepIsolatedTests,
// ReviewCodeStepIsolatedTests, RunQualityGatesStepIsolatedTests).

public class PipelineStepTests
{
    private readonly Mock<IRepositoryProvider> _repoProvider = new();
    private readonly Mock<IAgentProvider> _agentProvider = new();
    private readonly Mock<IIssueProvider> _issueProvider = new();
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly PipelineConfiguration _config;
    private readonly PipelineRun _run;
    private readonly List<string> _outputLines = [];
    private readonly List<PipelineStep> _transitions = [];

    public PipelineStepTests()
    {
        _config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        _run = new PipelineRun
        {
            RunId = "test-run-id",
            IssueIdentifier = "42",
            IssueTitle = string.Empty,
            IssueProviderConfigId = "issue-config",
            RepoProviderConfigId = "repo-config",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.Created,
            RepositoryName = "owner/repo"
        };
    }

    private PipelineStepContext BuildContext(
        IRepositoryProvider? brainProvider = null,
        BrainSyncService? brainSync = null)
    {
        var prOrchestrator = new PullRequestOrchestrator(_logger);
        var callbacks = new TestCallbacks(_transitions, _outputLines);
        return new PipelineStepContext
        {
            Run = _run,
            Config = _config,
            RepoProvider = _repoProvider.Object,
            AgentProvider = _agentProvider.Object,
            BrainProvider = brainProvider,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = _configStore.Object,
            IssueProvider = _issueProvider.Object,
            Callbacks = callbacks,
            IssueOps = _issueOps.Object,
            AgentExecution = new AgentPhaseExecutor(_logger),
            QualityGates = new QualityGateExecutor(
                Mock.Of<IQualityGateValidator>(), prOrchestrator, new CiLogWriter(_logger), new FeedbackService(_logger), _logger),
            BrainSync = brainSync,
            PrOrchestrator = prOrchestrator,
            Logger = _logger
        };
    }

    // â”€â”€ FetchIssueStep â”€â”€

    [Fact]
    public async Task FetchIssueStep_Success_SetsContextIssueAndComments()
    {
        var issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = ["bug"] };
        var comments = new List<IssueComment> { new() { Id = "c1", Author = "user", Body = "comment", CreatedAt = DateTime.UtcNow } };
        _issueProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        _issueProvider.Setup(p => p.ListCommentsAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(comments);

        var step = new FetchIssueStep(new IssueDescriptionParser(), new IssueImageExtractor());
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        context.Issue!.Identifier.Should().Be("42");
        context.IssueComments.Should().BeEquivalentTo(comments);
        _run.IssueTitle.Should().Be("Test");
    }

    [Fact]
    public async Task FetchIssueStep_EmptyTitle_FailsRun()
    {
        var issue = new IssueDetail { Identifier = "42", Title = "", Description = "Desc", Labels = [] };
        _issueProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(issue);

        var step = new FetchIssueStep(new IssueDescriptionParser(), new IssueImageExtractor());
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
        _run.FailureReason.Should().Contain("insufficient");
    }

    [Fact]
    public async Task FetchIssueStep_EmptyDescription_FailsRun()
    {
        var issue = new IssueDetail { Identifier = "42", Title = "Title", Description = "  ", Labels = [] };
        _issueProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(issue);

        var step = new FetchIssueStep(new IssueDescriptionParser(), new IssueImageExtractor());
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
        _run.FailureReason.Should().Contain("insufficient");
    }

    [Fact]
    public async Task FetchIssueStep_ProviderThrows_FailsRun()
    {
        _issueProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var step = new FetchIssueStep(new IssueDescriptionParser(), new IssueImageExtractor());
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
        _run.FailureReason.Should().Contain("Network error");
    }

    [Fact]
    public async Task FetchIssueStep_CommentFetchFails_ContinuesWithoutComments()
    {
        var issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        _issueProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        _issueProvider.Setup(p => p.ListCommentsAsync("42", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fail"));

        var step = new FetchIssueStep(new IssueDescriptionParser(), new IssueImageExtractor());
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        context.IssueComments.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchIssueStep_WithImagesInBody_PopulatesIssueImages()
    {
        var description = "Here is a screenshot:\n![error](https://user-images.githubusercontent.com/123/image.png)\nEnd.";
        var issue = new IssueDetail { Identifier = "42", Title = "Bug", Description = description, Labels = ["bug"] };
        var comments = new List<IssueComment>();
        _issueProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        _issueProvider.Setup(p => p.ListCommentsAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(comments);

        var step = new FetchIssueStep(new IssueDescriptionParser(), new IssueImageExtractor());
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        context.Issue!.Images.Should().HaveCount(1);
        context.Issue.Images[0].Url.Should().Be("https://user-images.githubusercontent.com/123/image.png");
        context.Issue.Images[0].AltText.Should().Be("error");
        context.Issue.Images[0].SourceType.Should().Be(ImageSourceType.Body);
    }

    [Fact]
    public async Task FetchIssueStep_WithNoImages_ReconstructsIssueDetailWithEmptyImages()
    {
        var issue = new IssueDetail { Identifier = "42", Title = "Feature", Description = "No images here", Labels = [] };
        var comments = new List<IssueComment>();
        _issueProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        _issueProvider.Setup(p => p.ListCommentsAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(comments);

        var step = new FetchIssueStep(new IssueDescriptionParser(), new IssueImageExtractor());
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        // After image extraction integration, the step reconstructs IssueDetail.
        // The context.Issue should NOT be the same reference as the input issue.
        context.Issue.Should().NotBeSameAs(issue);
        context.Issue!.Images.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchIssueStep_WithImagesInComments_PopulatesIssueImages()
    {
        var issue = new IssueDetail { Identifier = "42", Title = "Bug", Description = "Description text", Labels = [] };
        var comments = new List<IssueComment>
        {
            new() { Id = "c1", Author = "user", Body = "See ![screenshot](https://example.com/img.png)", CreatedAt = DateTime.UtcNow }
        };
        _issueProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        _issueProvider.Setup(p => p.ListCommentsAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(comments);

        var step = new FetchIssueStep(new IssueDescriptionParser(), new IssueImageExtractor());
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        context.Issue!.Images.Should().HaveCount(1);
        context.Issue.Images[0].Url.Should().Be("https://example.com/img.png");
        context.Issue.Images[0].SourceType.Should().Be(ImageSourceType.Comment);
        context.Issue.Images[0].SourceIndex.Should().Be(0);
    }

    // â”€â”€ CloneRepositoryStep â”€â”€

    [Fact]
    public async Task CloneRepositoryStep_Success_SetsWorkspaceAndTransitions()
    {
        _repoProvider.Setup(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var step = new CloneRepositoryStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.WorkspacePath.Should().NotBeNullOrEmpty();
        _transitions.Should().Contain(PipelineStep.CloningRepository);
    }

    [Fact]
    public async Task CloneRepositoryStep_CloneFails_FailsRun()
    {
        _repoProvider.Setup(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Clone failed"));

        var step = new CloneRepositoryStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
        _run.FailureReason.Should().Contain("Clone failed");
    }

    // â”€â”€ SyncBrainPreRunStep â”€â”€

    [Fact]
    public async Task SyncBrainPreRunStep_NoBrainProvider_Skips()
    {
        var step = new SyncBrainPreRunStep();
        var context = BuildContext(brainProvider: null, brainSync: null);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _transitions.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncBrainPreRunStep_WithBrainProvider_Transitions()
    {
        var brainProvider = Mock.Of<IRepositoryProvider>();
        var brainSync = new BrainSyncService(Mock.Of<IBrainUpdateService>(), _logger);
        _run.WorkspacePath = "/tmp/test";

        var step = new SyncBrainPreRunStep();
        var context = BuildContext(brainProvider: brainProvider, brainSync: brainSync);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _transitions.Should().Contain(PipelineStep.SyncingBrainRepoPreRun);
    }

    [Fact]
    public async Task SyncBrainPreRunStep_NoBrainProvider_DoesNotReportBrainSync()
    {
        var step = new SyncBrainPreRunStep();
        var context = BuildContext(brainProvider: null, brainSync: null);
        await step.ExecuteAsync(context, CancellationToken.None);

        var callbacks = (TestCallbacks)context.Callbacks;
        callbacks.BrainSyncReports.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncBrainPreRunStep_WithBrainProvider_ReportsBrainSyncResult()
    {
        var brainProvider = Mock.Of<IRepositoryProvider>();
        var brainSync = new BrainSyncService(Mock.Of<IBrainUpdateService>(), _logger);
        _run.WorkspacePath = "/tmp/test";

        var step = new SyncBrainPreRunStep();
        var context = BuildContext(brainProvider: brainProvider, brainSync: brainSync);
        await step.ExecuteAsync(context, CancellationToken.None);

        var callbacks = (TestCallbacks)context.Callbacks;
        callbacks.BrainSyncReports.Should().ContainSingle();
    }

    [Fact]
    public async Task SyncBrainPreRunStep_SyncFails_ReportsBrainSyncFailure()
    {
        var mockBrainProvider = new Mock<IRepositoryProvider>();
        mockBrainProvider.Setup(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("clone failed"));
        var brainSync = new BrainSyncService(Mock.Of<IBrainUpdateService>(), _logger);
        _run.WorkspacePath = "/tmp/test";

        var step = new SyncBrainPreRunStep();
        var context = BuildContext(brainProvider: mockBrainProvider.Object, brainSync: brainSync);
        await step.ExecuteAsync(context, CancellationToken.None);

        var callbacks = (TestCallbacks)context.Callbacks;
        callbacks.BrainSyncReports.Should().ContainSingle()
            .Which.ContextLoaded.Should().BeFalse();
    }

    // â”€â”€ DetectReworkStep â”€â”€

    [Fact]
    public async Task DetectReworkStep_FindsPr_SetsLinkedPullRequest()
    {
        var pr = new LinkedPullRequest { Number = 5, BranchName = "feature/auto-42", Url = "http://pr/5", IsDraft = false };
        _repoProvider.Setup(p => p.GetAgentPullRequestsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LinkedPullRequest> { pr });

        var step = new DetectReworkStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.LinkedPullRequest.Should().Be(pr);
    }

    [Fact]
    public async Task DetectReworkStep_DraftPr_ClosesAndDoesNotLink()
    {
        var pr = new LinkedPullRequest { Number = 7, BranchName = "feature/auto-42", Url = "http://pr/7", IsDraft = true };
        _repoProvider.Setup(p => p.GetAgentPullRequestsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LinkedPullRequest> { pr });
        _repoProvider.Setup(p => p.ClosePullRequestAsync(7, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var step = new DetectReworkStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.LinkedPullRequest.Should().BeNull();
        _repoProvider.Verify(p => p.ClosePullRequestAsync(7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DetectReworkStep_AlreadyLinked_Skips()
    {
        _run.LinkedPullRequest = new LinkedPullRequest { Number = 3, BranchName = "existing", Url = "http://pr/3", IsDraft = false };

        var step = new DetectReworkStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _repoProvider.Verify(p => p.GetAgentPullRequestsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectReworkStep_NoPrsFound_LeavesNull()
    {
        _repoProvider.Setup(p => p.GetAgentPullRequestsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LinkedPullRequest>());

        var step = new DetectReworkStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.LinkedPullRequest.Should().BeNull();
    }

    [Fact]
    public async Task DetectReworkStep_DetectionFails_ContinuesWithNewIssueFlow()
    {
        _repoProvider.Setup(p => p.GetAgentPullRequestsAsync("42", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API error"));

        var step = new DetectReworkStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.LinkedPullRequest.Should().BeNull();
    }

    [Fact]
    public async Task DetectReworkStep_MultipleDrafts_ClosesAllAndDoesNotLink()
    {
        var drafts = new List<LinkedPullRequest>
        {
            new() { Number = 10, BranchName = "feature/auto-42-a", Url = "http://pr/10", IsDraft = true },
            new() { Number = 12, BranchName = "feature/auto-42-b", Url = "http://pr/12", IsDraft = true }
        };
        _repoProvider.Setup(p => p.GetAgentPullRequestsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(drafts);
        _repoProvider.Setup(p => p.ClosePullRequestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var step = new DetectReworkStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.LinkedPullRequest.Should().BeNull();
        _repoProvider.Verify(p => p.ClosePullRequestAsync(10, It.IsAny<CancellationToken>()), Times.Once);
        _repoProvider.Verify(p => p.ClosePullRequestAsync(12, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DetectReworkStep_MixedDraftAndNonDraft_ClosesDraftAndLinksNonDraft()
    {
        var prs = new List<LinkedPullRequest>
        {
            new() { Number = 8, BranchName = "feature/auto-42-a", Url = "http://pr/8", IsDraft = false },
            new() { Number = 11, BranchName = "feature/auto-42-b", Url = "http://pr/11", IsDraft = true }
        };
        _repoProvider.Setup(p => p.GetAgentPullRequestsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(prs);
        _repoProvider.Setup(p => p.ClosePullRequestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var step = new DetectReworkStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.LinkedPullRequest.Should().Be(prs[0]);
        _repoProvider.Verify(p => p.ClosePullRequestAsync(11, It.IsAny<CancellationToken>()), Times.Once);
        // TODO: Add Times.Never verification for non-draft PR #8 to ensure it is not incorrectly closed
    }

    [Fact]
    public async Task DetectReworkStep_MultipleNonDrafts_SelectsHighestNumber()
    {
        var prs = new List<LinkedPullRequest>
        {
            new() { Number = 3, BranchName = "feature/auto-42-a", Url = "http://pr/3", IsDraft = false },
            new() { Number = 9, BranchName = "feature/auto-42-b", Url = "http://pr/9", IsDraft = false }
        };
        _repoProvider.Setup(p => p.GetAgentPullRequestsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(prs);

        var step = new DetectReworkStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.LinkedPullRequest.Should().Be(prs[1]);
    }

    [Fact]
    public async Task DetectReworkStep_DraftHigherThanNonDraft_ClosesDraftSelectsNonDraft()
    {
        var prs = new List<LinkedPullRequest>
        {
            new() { Number = 5, BranchName = "feature/auto-42-a", Url = "http://pr/5", IsDraft = false },
            new() { Number = 15, BranchName = "feature/auto-42-b", Url = "http://pr/15", IsDraft = true }
        };
        _repoProvider.Setup(p => p.GetAgentPullRequestsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(prs);
        _repoProvider.Setup(p => p.ClosePullRequestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var step = new DetectReworkStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.LinkedPullRequest.Should().Be(prs[0]);
        _repoProvider.Verify(p => p.ClosePullRequestAsync(15, It.IsAny<CancellationToken>()), Times.Once);
        _repoProvider.Verify(p => p.ClosePullRequestAsync(5, It.IsAny<CancellationToken>()), Times.Never);
    }

    // â”€â”€ CreateBranchStep â”€â”€

    [Fact]
    public async Task CreateBranchStep_NewIssue_CreatesBranch()
    {
        _run.WorkspacePath = "/tmp/test";
        _repoProvider.Setup(p => p.CreateBranchAsync("/tmp/test", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("feature/auto-42-test");

        var step = new CreateBranchStep();
        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test Issue", Description = "Desc", Labels = [] };
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.BranchName.Should().Be("feature/auto-42-test");
        _transitions.Should().Contain(PipelineStep.CreatingBranch);
    }

    [Fact]
    public async Task CreateBranchStep_Rework_ChecksOutAndMerges()
    {
        _run.WorkspacePath = "/tmp/test";
        _run.LinkedPullRequest = new LinkedPullRequest { Number = 5, BranchName = "feature/auto-42-old", Url = "http://pr/5", IsDraft = false };
        _repoProvider.Setup(p => p.CheckoutRemoteBranchAsync("/tmp/test", "feature/auto-42-old", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(p => p.MergeFromBaseAsync("/tmp/test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult { Success = true, HasConflicts = false, ConflictFiles = [] });
        _repoProvider.SetupGet(p => p.BaseBranch).Returns("main");

        var step = new CreateBranchStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _run.BranchName.Should().Be("feature/auto-42-old");
    }

    [Fact]
    public async Task CreateBranchStep_CheckoutFails_FailsRun()
    {
        _run.WorkspacePath = "/tmp/test";
        _run.LinkedPullRequest = new LinkedPullRequest { Number = 5, BranchName = "feature/auto-42-old", Url = "http://pr/5", IsDraft = false };
        _repoProvider.Setup(p => p.CheckoutRemoteBranchAsync("/tmp/test", "feature/auto-42-old", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("checkout failed"));

        var step = new CreateBranchStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
        _run.FailureReason.Should().Contain("checkout failed");
    }

    [Fact]
    public async Task CreateBranchStep_MergeFails_FailsRun()
    {
        _run.WorkspacePath = "/tmp/test";
        _run.LinkedPullRequest = new LinkedPullRequest { Number = 5, BranchName = "feature/auto-42-old", Url = "http://pr/5", IsDraft = false };
        _repoProvider.Setup(p => p.CheckoutRemoteBranchAsync("/tmp/test", "feature/auto-42-old", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(p => p.MergeFromBaseAsync("/tmp/test", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("merge conflict unresolvable"));

        var step = new CreateBranchStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
        _run.FailureReason.Should().Contain("merge");
    }

    [Fact]
    public async Task CreateBranchStep_BranchCreationFails_FailsRun()
    {
        _run.WorkspacePath = "/tmp/test";
        _repoProvider.Setup(p => p.CreateBranchAsync("/tmp/test", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("branch exists"));

        var step = new CreateBranchStep();
        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
        _run.FailureReason.Should().Contain("branch");
    }

    // â”€â”€ GenerateCodeStep â”€â”€

    [Fact]
    public async Task GenerateCodeStep_ReworkNoPrompt_SkipsCodeGen()
    {
        _run.LinkedPullRequest = new LinkedPullRequest
        {
            Number = 5, BranchName = "feature/auto-42", Url = "http://pr/5",
            IsDraft = false, ReviewComments = []
        };

        var step = new GenerateCodeStep();
        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _outputLines.Should().Contain(l => l.Contains("skipping code generation"));
    }

    // â”€â”€ BrainPullBeforeWriteStep â”€â”€

    [Fact]
    public async Task BrainPullBeforeWriteStep_NoBrain_Skips()
    {
        var step = new BrainPullBeforeWriteStep();
        var context = BuildContext(brainProvider: null, brainSync: null);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
    }

    [Fact]
    public async Task BrainPullBeforeWriteStep_BrainNotLoaded_Skips()
    {
        _run.BrainContextLoaded = false;
        var brainProvider = Mock.Of<IRepositoryProvider>();
        var brainSync = new BrainSyncService(Mock.Of<IBrainUpdateService>(), _logger);

        var step = new BrainPullBeforeWriteStep();
        var context = BuildContext(brainProvider: brainProvider, brainSync: brainSync);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
    }

    // â”€â”€ PipelineStepRunner â”€â”€

    [Fact]
    public async Task PipelineStepRunner_ExecutesStepsInOrder()
    {
        var executionOrder = new List<int>();
        var step1 = new TestStep(() => { executionOrder.Add(1); return StepResult.Continue; });
        var step2 = new TestStep(() => { executionOrder.Add(2); return StepResult.Continue; });
        var step3 = new TestStep(() => { executionOrder.Add(3); return StepResult.Continue; });

        var context = BuildContext();
        await PipelineStepRunner.ExecuteAsync([step1, step2, step3], context, CancellationToken.None);

        executionOrder.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task PipelineStepRunner_StopsOnStopResult()
    {
        var executionOrder = new List<int>();
        var step1 = new TestStep(() => { executionOrder.Add(1); return StepResult.Continue; });
        var step2 = new TestStep(() => { executionOrder.Add(2); return StepResult.Stop; });
        var step3 = new TestStep(() => { executionOrder.Add(3); return StepResult.Continue; });

        var context = BuildContext();
        await PipelineStepRunner.ExecuteAsync([step1, step2, step3], context, CancellationToken.None);

        executionOrder.Should().Equal(1, 2);
    }

    [Fact]
    public async Task PipelineStepRunner_EmptyStepList_CompletesSuccessfully()
    {
        var context = BuildContext();
        await PipelineStepRunner.ExecuteAsync([], context, CancellationToken.None);
        // No exception = success
    }

    [Fact]
    public async Task PipelineStepRunner_PropagatesCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var step = new TestStep(() => StepResult.Continue, throwOnCancel: true);
        var context = BuildContext();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => PipelineStepRunner.ExecuteAsync([step], context, cts.Token));
    }

    // â”€â”€ AnalyzeCodeStep â”€â”€

    [Fact]
    public async Task AnalyzeCodeStep_NullIssue_ThrowsInvalidOperationException()
    {
        var step = new AnalyzeCodeStep();
        var context = BuildContext();
        context.Issue = null;
        context.ParsedIssue = null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => step.ExecuteAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task AnalyzeCodeStep_NullParsedIssue_ThrowsInvalidOperationException()
    {
        var step = new AnalyzeCodeStep();
        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => step.ExecuteAsync(context, CancellationToken.None));
    }

    // â”€â”€ GenerateCodeStep (null guard) â”€â”€

    [Fact]
    public async Task GenerateCodeStep_NullIssue_ThrowsInvalidOperationException()
    {
        _run.LinkedPullRequest = null; // new issue flow, not rework
        var step = new GenerateCodeStep();
        var context = BuildContext();
        context.Issue = null;
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => step.ExecuteAsync(context, CancellationToken.None));
    }

    // â”€â”€ ReviewCodeStep (null guard) â”€â”€

    [Fact]
    public async Task ReviewCodeStep_NullIssue_ThrowsInvalidOperationException()
    {
        var step = new ReviewCodeStep();
        var context = BuildContext();
        context.Issue = null;
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        context.PreResolvedReviewerConfigs = new List<ReviewerConfiguration>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => step.ExecuteAsync(context, CancellationToken.None));
    }

    // â”€â”€ RunQualityGatesStep â”€â”€

    [Fact]
    public async Task RunQualityGatesStep_RunAlreadyFailed_ReturnsStop()
    {
        _run.CurrentStep = PipelineStep.Failed;
        _run.WorkspacePath = "/tmp/test";

        var step = new RunQualityGatesStep();
        var context = BuildContext();
        context.PreResolvedQualityGateConfigs = new List<QualityGateConfiguration>();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
    }

    [Fact]
    public async Task RunQualityGatesStep_RunAlreadyCompleted_ReturnsStop()
    {
        _run.CurrentStep = PipelineStep.Completed;
        _run.WorkspacePath = "/tmp/test";

        var step = new RunQualityGatesStep();
        var context = BuildContext();
        context.PreResolvedQualityGateConfigs = new List<QualityGateConfiguration>();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
    }

    [Fact]
    public async Task RunQualityGatesStep_RunAlreadyCancelled_ReturnsStop()
    {
        _run.CurrentStep = PipelineStep.Cancelled;
        _run.WorkspacePath = "/tmp/test";

        var step = new RunQualityGatesStep();
        var context = BuildContext();
        context.PreResolvedQualityGateConfigs = new List<QualityGateConfiguration>();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
    }

    // â”€â”€ PipelineStepRunner (null guard) â”€â”€

    [Fact]
    public async Task PipelineStepRunner_NullSteps_ThrowsArgumentNullException()
    {
        var context = BuildContext();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => PipelineStepRunner.ExecuteAsync(null!, context, CancellationToken.None));
    }

    [Fact]
    public async Task PipelineStepRunner_NullContext_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => PipelineStepRunner.ExecuteAsync([], null!, CancellationToken.None));
    }

    // â”€â”€ Helper â”€â”€

    private sealed class TestStep : IPipelineStep
    {
        private readonly Func<StepResult> _execute;
        private readonly bool _throwOnCancel;

        public string StepName => "Test";

        public TestStep(Func<StepResult> execute, bool throwOnCancel = false)
        {
            _execute = execute;
            _throwOnCancel = throwOnCancel;
        }

        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
        {
            if (_throwOnCancel) ct.ThrowIfCancellationRequested();
            return Task.FromResult(_execute());
        }
    }

    private sealed class TestCallbacks(List<PipelineStep> transitions, List<string> outputLines) : IPipelineCallbacks
    {
        public List<(bool ContextLoaded, int FileCount)> BrainSyncReports { get; } = [];
        public void TransitionTo(PipelineStep step) => transitions.Add(step);
        public void EmitOutputLine(string line) => outputLines.Add(line);
        public void NotifyChange() { }
        public Task AddRunToHistoryAsync(PipelineRun run) => Task.CompletedTask;
        public Task UpdateFileChangeStats(PipelineRun run) => Task.CompletedTask;
        public Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct) => Task.CompletedTask;
        public Task CreatePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct) => Task.CompletedTask;
        public Task CreateDraftPrIfNotExists(PipelineRun run, CancellationToken ct) => Task.CompletedTask;
        public Task FinalizePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct) => Task.CompletedTask;
        public Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount)
        {
            BrainSyncReports.Add((contextLoaded, knowledgeFileCount));
            return Task.CompletedTask;
        }
    }
}
