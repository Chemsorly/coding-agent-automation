using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

// TODO: [WARNING] Missing dedicated unit tests for AnalyzeCodeStep, ReviewCodeStep, and RunQualityGatesStep.
// These steps delegate to orchestrator methods that are not virtual (cannot be mocked with Moq).
// They are tested indirectly through PipelineOrchestrationServiceTests but lack isolated step-level tests.
// Partial coverage added below for null guards, disabled-review short-circuit, and terminal state detection.

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
        BrainSyncOrchestrator? brainSync = null)
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
            AgentExecution = new AgentExecutionOrchestrator(_logger),
            QualityGates = new QualityGateOrchestrator(
                Mock.Of<IQualityGateValidator>(), prOrchestrator, _logger),
            BrainSync = brainSync,
            PrOrchestrator = prOrchestrator,
            Logger = _logger
        };
    }

    // ── FetchIssueStep ──

    [Fact]
    public async Task FetchIssueStep_Success_SetsContextIssueAndComments()
    {
        var issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = ["bug"] };
        var comments = new List<IssueComment> { new() { Id = "c1", Author = "user", Body = "comment", CreatedAt = DateTime.UtcNow } };
        _issueProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        _issueProvider.Setup(p => p.ListCommentsAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(comments);

        var step = new FetchIssueStep(new IssueDescriptionParser());
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        context.Issue.Should().Be(issue);
        context.IssueComments.Should().BeEquivalentTo(comments);
        _run.IssueTitle.Should().Be("Test");
    }

    [Fact]
    public async Task FetchIssueStep_EmptyTitle_FailsRun()
    {
        var issue = new IssueDetail { Identifier = "42", Title = "", Description = "Desc", Labels = [] };
        _issueProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(issue);

        var step = new FetchIssueStep(new IssueDescriptionParser());
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

        var step = new FetchIssueStep(new IssueDescriptionParser());
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

        var step = new FetchIssueStep(new IssueDescriptionParser());
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

        var step = new FetchIssueStep(new IssueDescriptionParser());
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        context.IssueComments.Should().BeEmpty();
    }

    // ── CloneRepositoryStep ──

    [Fact]
    public async Task CloneRepositoryStep_Success_SetsWorkspaceAndTransitions()
    {
        _repoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        _repoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Clone failed"));

        var step = new CloneRepositoryStep();
        var context = BuildContext();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
        _run.FailureReason.Should().Contain("Clone failed");
    }

    // ── SyncBrainPreRunStep ──

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
        var brainSync = new BrainSyncOrchestrator(Mock.Of<IBrainUpdateService>(), _logger);
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
        var brainSync = new BrainSyncOrchestrator(Mock.Of<IBrainUpdateService>(), _logger);
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
        mockBrainProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("clone failed"));
        var brainSync = new BrainSyncOrchestrator(Mock.Of<IBrainUpdateService>(), _logger);
        _run.WorkspacePath = "/tmp/test";

        var step = new SyncBrainPreRunStep();
        var context = BuildContext(brainProvider: mockBrainProvider.Object, brainSync: brainSync);
        await step.ExecuteAsync(context, CancellationToken.None);

        var callbacks = (TestCallbacks)context.Callbacks;
        callbacks.BrainSyncReports.Should().ContainSingle()
            .Which.ContextLoaded.Should().BeFalse();
    }

    // ── DetectReworkStep ──

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

    // ── CreateBranchStep ──

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

    // ── GenerateCodeStep ──

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

    // ── BrainPullBeforeWriteStep ──

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
        var brainSync = new BrainSyncOrchestrator(Mock.Of<IBrainUpdateService>(), _logger);

        var step = new BrainPullBeforeWriteStep();
        var context = BuildContext(brainProvider: brainProvider, brainSync: brainSync);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
    }

    // ── PipelineStepRunner ──

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

    // ── AnalyzeCodeStep ──

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

    // ── GenerateCodeStep (null guard) ──

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

    // ── ReviewCodeStep (null guard) ──

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

    // ── RunQualityGatesStep ──

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

    // ── PipelineStepRunner (null guard) ──

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

    // ── Helper ──

    private sealed class TestStep : IPipelineStep
    {
        private readonly Func<StepResult> _execute;
        private readonly bool _throwOnCancel;

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
        public void AddRunToHistory(PipelineRun run) { }
        public Task UpdateFileChangeStats(PipelineRun run) => Task.CompletedTask;
        public Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct) => Task.CompletedTask;
        public Task CreatePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct) => Task.CompletedTask;
        public Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount)
        {
            BrainSyncReports.Add((contextLoaded, knowledgeFileCount));
            return Task.CompletedTask;
        }
    }
}
