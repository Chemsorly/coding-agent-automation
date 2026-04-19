using AwesomeAssertions;
using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Unit tests for PipelineOrchestrationService.
/// </summary>
public class PipelineOrchestrationServiceTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IAgentProvider> _mockAgentProvider;
    private readonly Mock<IQualityGateValidator> _mockValidator;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineOrchestrationService _service;

    public PipelineOrchestrationServiceTests()
    {
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockAgentProvider = new Mock<IAgentProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();
        _mockValidator = new Mock<IQualityGateValidator>();

        SetupDefaultMocks();

        _service = new PipelineOrchestrationService(
            _mockConfigStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            _mockValidator.Object,
            _mockLogger.Object,
            runsDirectory: Path.Combine(Path.GetTempPath(), $"test-runs-{Guid.NewGuid()}"));
    }

    private void SetupDefaultMocks()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" }
            });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" }
            });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test" }
            });

        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42", Title = "Test Issue", Description = "Test description",
                Labels = Array.Empty<string>(), AcceptanceCriteria = Array.Empty<string>()
            });
        _mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");

        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        _mockAgentProvider.Setup(p => p.ExecuteWithResumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        _mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(_mockIssueProvider.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(_mockRepoProvider.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(_mockAgentProvider.Object);
    }

    [Fact]
    public async Task StartPipeline_WhenAlreadyRunning_ThrowsInvalidOperationException()
    {
        // Start first pipeline — now pauses at WaitingForAnalysisApproval
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);

        // Attempt to start second pipeline — should throw
        var act = () => _service.StartPipelineAsync("issue-1", "repo-1", "99", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in progress*");
    }

    [Fact]
    public async Task CancelPipeline_DuringWaitingForAnalysisApproval_TransitionsToCancelled()
    {
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);

        await _service.CancelPipelineAsync();

        run.CurrentStep.Should().Be(PipelineStep.Cancelled);
        run.CompletedAt.Should().NotBeNull();
        _service.IsRunning.Should().BeFalse();
        _service.GetRunHistory().Should().HaveCount(1);
        _service.GetRunHistory()[0].FinalStep.Should().Be(PipelineStep.Cancelled);
    }

    [Fact]
    public async Task ApproveAnalysis_TransitionsToWaitingForChat()
    {
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);

        await _service.ApproveAnalysisAsync(CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);
    }

    [Fact]
    public async Task ApproveAnalysis_WhenNotInApprovalState_ThrowsInvalidOperationException()
    {
        var act = () => _service.ApproveAnalysisAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*WaitingForAnalysisApproval*");
    }

    [Fact]
    public async Task CancelPipeline_DuringWaitingForChat_TransitionsToCancelled()
    {
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);

        await _service.ApproveAnalysisAsync(CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);

        await _service.CancelPipelineAsync();

        run.CurrentStep.Should().Be(PipelineStep.Cancelled);
        run.CompletedAt.Should().NotBeNull();
        _service.IsRunning.Should().BeFalse();
        _service.GetRunHistory().Should().HaveCount(1);
        _service.GetRunHistory()[0].FinalStep.Should().Be(PipelineStep.Cancelled);
    }

    [Fact]
    public async Task StartPipeline_WithMissingIssueTitle_FailsWithInsufficientInfo()
    {
        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42", Title = "", Description = "Some description",
                Labels = Array.Empty<string>(), AcceptanceCriteria = Array.Empty<string>()
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Be("insufficient issue information");
        _service.GetRunHistory().Should().HaveCount(1);
    }

    [Fact]
    public async Task StartPipeline_WithMissingDescription_FailsWithInsufficientInfo()
    {
        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42", Title = "Valid Title", Description = "",
                Labels = Array.Empty<string>(), AcceptanceCriteria = Array.Empty<string>()
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Be("insufficient issue information");
    }

    [Fact]
    public async Task RunHistory_TracksCompletedRuns()
    {
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);

        await _service.ApproveAnalysisAsync(CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);

        await _service.ProceedToQualityGatesAsync(CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.PullRequestUrl.Should().NotBeNullOrEmpty();

        var history = _service.GetRunHistory();
        history.Should().HaveCount(1);
        history[0].RunId.Should().Be(run.RunId);
        history[0].FinalStep.Should().Be(PipelineStep.Completed);
        history[0].PullRequestUrl.Should().NotBeNullOrEmpty();
        history[0].IssueIdentifier.Should().Be("42");
    }

    [Fact]
    public async Task StartPipeline_WhenIssueProviderThrows_FailsWithConnectivityError()
    {
        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("Failed to fetch issue");
        run.FailureReason.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task StartPipeline_PostsAnalysisCommentOnIssue()
    {
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);
        _mockIssueProvider.Verify(
            p => p.PostCommentAsync("42", It.Is<string>(s => s.Contains("Agent Analysis")), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StartPipeline_WhenAnalysisCommentFails_PausesAtApproval()
    {
        _mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);
    }

    [Fact]
    public async Task StartPipeline_AnalyzesCodeBeforePostingComment()
    {
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);

        // Warm-up prompt should have been called via ExecuteAsync
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Briefly describe the project structure")),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Once);

        // Analysis should have been called via ExecuteWithResumeAsync (resumes the warm-up session)
        _mockAgentProvider.Verify(
            p => p.ExecuteWithResumeAsync(
                It.Is<string>(s => s.Contains("Analyze the codebase")),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Once);
    }

    [Fact]
    public async Task StartPipeline_WhenExistingAnalysisCommentFound_SkipsAgentAnalysis()
    {
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>
            {
                new()
                {
                    Id = "1",
                    Body = "## 🤖 Agent Analysis\n\nPrevious analysis content here.",
                    Author = "bot",
                    CreatedAt = DateTime.UtcNow.AddHours(-1)
                }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);
        run.AnalysisContent.Should().Contain("Previous analysis content here.");

        // Agent should NOT have been called for analysis
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase")),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Never);

        // Comment should NOT have been posted again
        _mockIssueProvider.Verify(
            p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WhenListCommentsFails_RunsFreshAnalysis()
    {
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);

        // Should have fallen back to running fresh analysis via warm-up + resume
        _mockAgentProvider.Verify(
            p => p.ExecuteWithResumeAsync(
                It.Is<string>(s => s.Contains("Analyze the codebase")),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Once);
    }

    [Fact]
    public async Task ApproveAnalysis_WithSelfReviewEnabled_RunsReviewBeforeChat()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                SelfReviewEnabled = true,
                SelfReviewMaxIterations = 1
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);

        await _service.ApproveAnalysisAsync(CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);
        run.ReviewIterationsCompleted.Should().Be(1);

        // Verify the review prompt was sent via ExecuteWithResumeAsync
        _mockAgentProvider.Verify(
            p => p.ExecuteWithResumeAsync(
                It.Is<string>(s => s.Contains("sub-agent")),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ApproveAnalysis_WithSelfReviewDisabled_SkipsReview()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                SelfReviewEnabled = false
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        await _service.ApproveAnalysisAsync(CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);
        run.ReviewIterationsCompleted.Should().Be(0);
    }

    [Fact]
    public async Task ApproveAnalysis_WithMultipleReviewIterations_RunsAllIterations()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                SelfReviewEnabled = true,
                SelfReviewMaxIterations = 3
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        await _service.ApproveAnalysisAsync(CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);
        run.ReviewIterationsCompleted.Should().Be(3);
    }

    [Fact]
    public async Task ApproveAnalysis_WhenReviewFails_StopsIterationsAndContinuesToChat()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                SelfReviewEnabled = true,
                SelfReviewMaxIterations = 3
            });

        // The "sub-agent" matcher hits both the analysis call in StartPipelineAsync
        // and the review calls in ApproveAnalysisAsync. Call sequence:
        //   1. StartPipelineAsync → analysis prompt (contains "sub-agent") → callCount=1
        //   2. ApproveAnalysisAsync → review iteration 1 → callCount=2 (succeeds)
        //   3. ApproveAnalysisAsync → review iteration 2 → callCount=3 (throws)
        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteWithResumeAsync(
                It.Is<string>(s => s.Contains("sub-agent")),
                It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount >= 3)
                    throw new InvalidOperationException("Agent crashed");
                return new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() };
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        await _service.ApproveAnalysisAsync(CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);
        run.ReviewIterationsCompleted.Should().Be(1);
    }

    [Fact]
    public async Task SelfReviewPrompt_IsConfigurable()
    {
        var customPrompt = "Custom review: check for bugs only.";
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                SelfReviewEnabled = true,
                SelfReviewMaxIterations = 1,
                SelfReviewPrompt = customPrompt
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        await _service.ApproveAnalysisAsync(CancellationToken.None);

        _mockAgentProvider.Verify(
            p => p.ExecuteWithResumeAsync(
                It.Is<string>(s => s.Contains(customPrompt) && s.Contains("Test Issue")),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Once);
    }

    [Fact]
    public void SelfReviewDefaults_AreCorrect()
    {
        var config = new PipelineConfiguration();
        config.SelfReviewEnabled.Should().BeFalse();
        config.SelfReviewMaxIterations.Should().Be(1);
        config.SelfReviewPrompt.Should().Contain("sub-agent");
    }

    // --- Blacklist enforcement (GIT-04) ---

    [Fact]
    public async Task ProceedToQualityGates_WarnAndExclude_PopulatesBlacklistedFilesAndCompletes()
    {
        // Arrange — CommitAllAsync returns blacklisted files
        var blacklisted = new List<string> { ".kiro/steering/rule.md", ".github/workflows/ci.yml" };
        _mockRepoProvider.Setup(p => p.CommitAllAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blacklisted as IReadOnlyList<string>);

        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = true }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        await _service.ApproveAnalysisAsync(CancellationToken.None);

        // Act
        await _service.ProceedToQualityGatesAsync(CancellationToken.None);

        // Assert — pipeline completed, blacklisted files recorded
        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.BlacklistedFilesDetected.Should().Contain(".kiro/steering/rule.md");
        run.BlacklistedFilesDetected.Should().Contain(".github/workflows/ci.yml");
    }

    [Fact]
    public async Task ProceedToQualityGates_FailMode_TransitionsToFailed()
    {
        // Arrange — Fail mode config
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                BlacklistMode = BlacklistMode.Fail
            });

        var blacklisted = new List<string> { ".github/workflows/ci.yml" };
        _mockRepoProvider.Setup(p => p.CommitAllAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blacklisted as IReadOnlyList<string>);

        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = true }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        await _service.ApproveAnalysisAsync(CancellationToken.None);

        // Act
        await _service.ProceedToQualityGatesAsync(CancellationToken.None);

        // Assert — pipeline failed with clear reason
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("Blacklisted files detected");
        run.FailureReason.Should().Contain(".github/workflows/ci.yml");
        run.BlacklistedFilesDetected.Should().Contain(".github/workflows/ci.yml");
    }

    [Fact]
    public async Task ProceedToQualityGates_AllFilesBlacklisted_HandlesGracefully()
    {
        // Arrange — CommitAllAsync throws "No changes to commit" (all files were blacklisted)
        // In CreatePullRequestAsync, this is caught and treated as "already committed".
        // The pipeline continues and creates a PR (or fails at BranchHasCommitsAhead).
        _mockRepoProvider.Setup(p => p.CommitAllAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace."));

        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = true }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        await _service.ApproveAnalysisAsync(CancellationToken.None);

        // Act
        await _service.ProceedToQualityGatesAsync(CancellationToken.None);

        // Assert — pipeline does not crash; it completes or fails gracefully
        run.CurrentStep.Should().BeOneOf(PipelineStep.Completed, PipelineStep.Failed);
    }
}
