using AwesomeAssertions;
using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;
using KiroWebUI.Tests.Helpers;

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
            new CiLogWriter(_mockLogger.Object),
            _mockLogger.Object,
            runsDirectory: Path.Combine(Path.GetTempPath(), $"test-runs-{Guid.NewGuid()}"));
    }

    private void SetupDefaultMocks()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
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
                Labels = Array.Empty<string>()
            });
        _mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());

        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        _mockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);

        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        _mockAgentProvider.Setup(p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });

        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
            });

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(_mockIssueProvider.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(_mockRepoProvider.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(_mockAgentProvider.Object);
    }

    // Helper: start pipeline with a blocking agent so it stays "running"
    private (Task<PipelineRun> pipelineTask, TaskCompletionSource<AgentResult> agentTcs) StartBlockingPipeline()
    {
        var agentTcs = new TaskCompletionSource<AgentResult>();
        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) =>
            {
                callCount++;
                if (callCount <= 1) // analysis call completes immediately
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                return agentTcs.Task; // code generation blocks
            });

        var task = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        return (task, agentTcs);
    }

    /// <summary>
    /// Helper: writes a review findings file into the workspace so the orchestrator can read it.
    /// Used by tests that verify code review severity parsing (file-based, not stdout).
    /// </summary>
    private static void WriteReviewFindingsFile(string workspacePath, string content)
    {
        var findingsDir = Path.Combine(workspacePath, ".kiro");
        Directory.CreateDirectory(findingsDir);
        File.WriteAllText(Path.Combine(findingsDir, "review-findings.md"), content);
    }

    /// <summary>
    /// Helper: sets up a mock review agent that writes findings to the workspace file and returns an empty AgentResult.
    /// </summary>
    private void SetupReviewAgentWithFindings(string promptMatch, string findingsContent)
    {
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains(promptMatch) && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                WriteReviewFindingsFile(req.WorkspacePath, findingsContent);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });
    }

    [Fact]
    public async Task StartPipeline_WhenAlreadyRunning_ThrowsInvalidOperationException()
    {
        var (pipelineTask, agentTcs) = StartBlockingPipeline();
        await Task.Delay(200); // let pipeline reach GeneratingCode

        _service.IsRunning.Should().BeTrue();

        var act = () => _service.StartPipelineAsync("issue-1", "repo-1", "99", "agent-1", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in progress*");

        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await pipelineTask;
    }

    [Fact]
    public async Task StartPipeline_RecordsModelFromAgentProviderConfig()
    {
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new()
                {
                    Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test",
                    Settings = new Dictionary<string, string> { ["model"] = "claude-sonnet-4.6" }
                }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.ModelName.Should().Be("claude-sonnet-4.6");
    }

    [Fact]
    public async Task StartPipeline_WithoutModelInConfig_DefaultsToAuto()
    {
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new()
                {
                    Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test",
                    Settings = new Dictionary<string, string>()
                }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.ModelName.Should().Be("auto");
    }

    [Fact]
    public async Task CancelPipeline_DuringExecution_TransitionsToCancelled()
    {
        var (pipelineTask, agentTcs) = StartBlockingPipeline();
        await Task.Delay(200);

        await _service.CancelPipelineAsync();
        agentTcs.TrySetCanceled();

        try { await pipelineTask; } catch { }

        var run = _service.ActiveRun!;
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
            .ReturnsAsync(new IssueDetail { Identifier = "42", Title = "", Description = "Some description", Labels = Array.Empty<string>() });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Be("insufficient issue information");
    }

    [Fact]
    public async Task StartPipeline_WithMissingDescription_FailsWithInsufficientInfo()
    {
        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "42", Title = "Valid Title", Description = "", Labels = Array.Empty<string>() });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Be("insufficient issue information");
    }

    [Fact]
    public async Task StartPipeline_CompletesFullFlow()
    {
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.PullRequestUrl.Should().NotBeNullOrEmpty();
        _service.GetRunHistory().Should().HaveCount(1);
        _service.GetRunHistory()[0].FinalStep.Should().Be(PipelineStep.Completed);
        _service.GetRunHistory()[0].PullRequestUrl.Should().NotBeNullOrEmpty();
        _service.GetRunHistory()[0].IssueIdentifier.Should().Be("42");
    }

    [Fact]
    public async Task RunHistory_IncludesModelName()
    {
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new()
                {
                    Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test",
                    Settings = new Dictionary<string, string> { ["model"] = "claude-opus-4.6" }
                }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        _service.GetRunHistory().Should().HaveCount(1);
        _service.GetRunHistory()[0].ModelName.Should().Be("claude-opus-4.6");
    }

    [Fact]
    public async Task StartPipeline_WhenIssueProviderThrows_FailsWithConnectivityError()
    {
        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("Failed to fetch issue");
        run.FailureReason.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task StartPipeline_PostsAnalysisCommentOnIssue()
    {
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        _mockIssueProvider.Verify(
            p => p.PostCommentAsync("42", It.Is<string>(s => s.Contains("Agent Analysis")), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StartPipeline_WhenAnalysisCommentFails_ContinuesToCompletion()
    {
        _mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task StartPipeline_AnalyzesCodeBeforePostingComment()
    {
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        _mockAgentProvider.Verify(
            p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Once);
    }

    [Fact]
    public async Task StartPipeline_WhenExistingAnalysisCommentFound_SkipsAgentAnalysis()
    {
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>
            {
                new() { Id = "1", Body = "## 🤖 Agent Analysis\n\nPrevious analysis content here.", Author = "bot", CreatedAt = DateTime.UtcNow.AddHours(-1) }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.AnalysisContent.Should().Contain("Previous analysis content here.");
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Never);
        _mockIssueProvider.Verify(
            p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WhenListCommentsFails_RunsFreshAnalysis()
    {
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Once);
    }

    [Fact]
    public async Task StartPipeline_TransitionsThroughExpectedSteps()
    {
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        transitions.Should().ContainInOrder(
            PipelineStep.CloningRepository,
            PipelineStep.CreatingBranch,
            PipelineStep.AnalyzingCode,
            PipelineStep.PostingAnalysis,
            PipelineStep.GeneratingCode,
            PipelineStep.RunningQualityGates,
            PipelineStep.CreatingPullRequest,
            PipelineStep.Completed);
    }

    [Fact]
    public async Task StartPipeline_WhenQualityGatesFail_CreatesDraftPrAfterRetries()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), MaxRetries = 1 });

        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = false, Details = "2 tests failed" }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.PullRequestUrl.Should().NotBeNullOrEmpty();
        run.IsDraftPr.Should().BeTrue();
        run.RetryCount.Should().Be(1);
    }

    // --- Code review tests ---

    [Fact]
    public async Task StartPipeline_WithCodeReviewEnabled_RunsReview()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1 }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.CodeReviewIterationsCompleted.Should().Be(1);
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("sub-agent") && r.UseResume), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartPipeline_WithCodeReviewDisabled_SkipsReview()
    {
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.CodeReviewIterationsCompleted.Should().Be(0);
    }

    [Fact]
    public async Task StartPipeline_WithMultipleReviewIterations_RunsAllIterations()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 3 }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.CodeReviewIterationsCompleted.Should().Be(3);
    }

    [Fact]
    public async Task StartPipeline_WhenReviewFails_StopsIterationsAndContinues()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 3 }
            });

        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("sub-agent") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount >= 3) throw new InvalidOperationException("Agent crashed");
                return new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() };
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.CodeReviewIterationsCompleted.Should().Be(1);
    }

    [Fact]
    public async Task CodeReviewPrompt_IsConfigurable()
    {
        var customPrompt = "Custom review: check for bugs only.";
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, Prompt = customPrompt }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains(customPrompt) && r.Prompt.Contains("Test Issue") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Once);
    }

    [Fact]
    public void CodeReviewDefaults_AreCorrect()
    {
        var config = new PipelineConfiguration();
        config.CodeReview.Enabled.Should().BeTrue();
        config.CodeReview.MaxIterations.Should().Be(1);
        config.CodeReview.Prompt.Should().Contain("sub-agent");
        config.CodeReview.Prompt.Should().Contain("[CRITICAL]");
        config.CodeReview.FixPrompt.Should().BeNull();
    }

    // --- Fix prompt tests ---

    [Fact]
    public async Task StartPipeline_WithFixPromptAndCriticals_SendsFixPrompt()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, FixPrompt = PipelineConfiguration.DefaultFixPrompt }
            });

        SetupReviewAgentWithFindings("sub-agent", "[CRITICAL] Missing null check\n[WARNING] Consider renaming");

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CodeReviewCriticalCount.Should().Be(1);
        run.CodeReviewWarningCount.Should().Be(1);
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("[CRITICAL]") && r.Prompt.Contains("Fix only") && r.UseResume), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Once);
    }

    [Fact]
    public async Task StartPipeline_WithFixPromptAndNoCriticals_SkipsFixPrompt()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, FixPrompt = PipelineConfiguration.DefaultFixPrompt }
            });

        SetupReviewAgentWithFindings("sub-agent", "[WARNING] Consider renaming\n[SUGGESTION] Use var");

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CodeReviewCriticalCount.Should().Be(0);
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Fix only")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WithNullFixPrompt_SinglePassBehavior()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, FixPrompt = null }
            });

        SetupReviewAgentWithFindings("sub-agent", "[CRITICAL] Missing null check");

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CodeReviewCriticalCount.Should().Be(1);
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Fix only")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Never);
    }

    [Fact]
    public async Task StartPipeline_SeverityCountsStoredOnRun()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1 }
            });

        SetupReviewAgentWithFindings("sub-agent",
            "[CRITICAL] Bug A\n[CRITICAL] Bug B\n[WARNING] Style issue\n[SUGGESTION] Rename X\n[SUGGESTION] Rename Y\n[SUGGESTION] Rename Z");

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CodeReviewCriticalCount.Should().Be(2);
        run.CodeReviewWarningCount.Should().Be(1);
        run.CodeReviewSuggestionCount.Should().Be(3);
        run.CodeReviewRawFindings.Should().NotBeNullOrEmpty();
    }

    // --- Blacklist enforcement ---

    [Fact]
    public async Task StartPipeline_WarnAndExclude_PopulatesBlacklistedFilesAndCompletes()
    {
        var blacklisted = new List<string> { ".kiro/steering/rule.md", ".github/workflows/ci.yml" };
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blacklisted as IReadOnlyList<string>);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.BlacklistedFilesDetected.Should().Contain(".kiro/steering/rule.md");
        run.BlacklistedFilesDetected.Should().Contain(".github/workflows/ci.yml");
    }

    [Fact]
    public async Task StartPipeline_FailMode_TransitionsToFailed()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), BlacklistMode = BlacklistMode.Fail });

        var blacklisted = new List<string> { ".github/workflows/ci.yml" };
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blacklisted as IReadOnlyList<string>);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("Blacklisted files detected");
        run.BlacklistedFilesDetected.Should().Contain(".github/workflows/ci.yml");
    }

    [Fact]
    public async Task StartPipeline_AllFilesBlacklisted_HandlesGracefully()
    {
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace."));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().BeOneOf(PipelineStep.Completed, PipelineStep.Failed);
    }

    // --- Config defaults ---

    [Fact]
    public void CleanupSuccessfulWorkspaces_DefaultsToTrue()
    {
        var config = new PipelineConfiguration();
        config.CleanupSuccessfulWorkspaces.Should().BeTrue();
    }

    [Fact]
    public void FailedWorkspaceRetentionDays_DefaultsToSeven()
    {
        var config = new PipelineConfiguration();
        config.FailedWorkspaceRetentionDays.Should().Be(7);
    }

    // --- Workspace cleanup ---

    [Fact]
    public async Task SuccessfulPr_DeletesWorkspace_WhenCleanupEnabled()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-cleanup-{Guid.NewGuid()}");
        Directory.CreateDirectory(workspaceBase);
        try
        {
            _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, CleanupSuccessfulWorkspaces = true });

            var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
            run.CurrentStep.Should().Be(PipelineStep.Completed);
            if (run.WorkspacePath != null)
                Directory.Exists(run.WorkspacePath).Should().BeFalse();
        }
        finally { if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    [Fact]
    public async Task SuccessfulPr_RetainsWorkspace_WhenCleanupDisabled()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-cleanup-{Guid.NewGuid()}");
        Directory.CreateDirectory(workspaceBase);
        try
        {
            _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, CleanupSuccessfulWorkspaces = false });

            var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
            run.CurrentStep.Should().Be(PipelineStep.Completed);
            if (run.WorkspacePath != null)
                Directory.Exists(run.WorkspacePath).Should().BeTrue();
        }
        finally { if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    [Fact]
    public async Task DraftPr_RetainsWorkspace_WhenCleanupEnabled()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-draft-{Guid.NewGuid()}");
        Directory.CreateDirectory(workspaceBase);
        try
        {
            _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, CleanupSuccessfulWorkspaces = true, MaxRetries = 0 });

            _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QualityGateReport
                {
                    Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build failed" },
                    Tests = new GateResult { GateName = "Tests", Passed = false, Details = "Tests failed" }
                });

            var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
            run.CurrentStep.Should().Be(PipelineStep.Failed);
            run.IsDraftPr.Should().BeTrue();
            if (run.WorkspacePath != null)
                Directory.Exists(run.WorkspacePath).Should().BeTrue();
        }
        finally { if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    [Fact]
    public void CleanupExpiredWorkspaces_DeletesExpiredFailedRunWorkspaces()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-retention-{Guid.NewGuid()}");
        var expiredRunId = Guid.NewGuid().ToString();
        var recentRunId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(workspaceBase, expiredRunId));
        Directory.CreateDirectory(Path.Combine(workspaceBase, recentRunId));
        var runsDir = Path.Combine(Path.GetTempPath(), $"test-runs-retention-{Guid.NewGuid()}");
        Directory.CreateDirectory(runsDir);
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            File.WriteAllText(Path.Combine(runsDir, $"{expiredRunId}.json"), System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary { RunId = expiredRunId, IssueIdentifier = "1", IssueTitle = "Expired", FinalStep = PipelineStep.Failed, StartedAt = DateTime.UtcNow.AddDays(-10), CompletedAt = DateTime.UtcNow.AddDays(-10) }, jsonOptions));
            File.WriteAllText(Path.Combine(runsDir, $"{recentRunId}.json"), System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary { RunId = recentRunId, IssueIdentifier = "2", IssueTitle = "Recent", FinalStep = PipelineStep.Failed, StartedAt = DateTime.UtcNow.AddDays(-1), CompletedAt = DateTime.UtcNow.AddDays(-1) }, jsonOptions));

            var service = new PipelineOrchestrationService(_mockConfigStore.Object, _mockFactory.Object, new IssueDescriptionParser(), _mockValidator.Object, new CiLogWriter(_mockLogger.Object), _mockLogger.Object, runsDirectory: runsDir);
            service.CleanupExpiredWorkspaces(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, FailedWorkspaceRetentionDays = 7 });

            Directory.Exists(Path.Combine(workspaceBase, expiredRunId)).Should().BeFalse();
            Directory.Exists(Path.Combine(workspaceBase, recentRunId)).Should().BeTrue();
        }
        finally { if (Directory.Exists(runsDir)) Directory.Delete(runsDir, true); if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    [Fact]
    public void CleanupExpiredWorkspaces_RetainsAll_WhenRetentionIsNegativeOne()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-retain-{Guid.NewGuid()}");
        var oldRunId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(workspaceBase, oldRunId));
        var runsDir = Path.Combine(Path.GetTempPath(), $"test-runs-retain-{Guid.NewGuid()}");
        Directory.CreateDirectory(runsDir);
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            File.WriteAllText(Path.Combine(runsDir, $"{oldRunId}.json"), System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary { RunId = oldRunId, IssueIdentifier = "1", IssueTitle = "Old", FinalStep = PipelineStep.Failed, StartedAt = DateTime.UtcNow.AddDays(-100), CompletedAt = DateTime.UtcNow.AddDays(-100) }, jsonOptions));

            var service = new PipelineOrchestrationService(_mockConfigStore.Object, _mockFactory.Object, new IssueDescriptionParser(), _mockValidator.Object, new CiLogWriter(_mockLogger.Object), _mockLogger.Object, runsDirectory: runsDir);
            service.CleanupExpiredWorkspaces(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, FailedWorkspaceRetentionDays = -1 });

            Directory.Exists(Path.Combine(workspaceBase, oldRunId)).Should().BeTrue();
        }
        finally { if (Directory.Exists(runsDir)) Directory.Delete(runsDir, true); if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    [Fact]
    public void CleanupExpiredWorkspaces_DeletesImmediately_WhenRetentionIsZero()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-zero-{Guid.NewGuid()}");
        var runId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(workspaceBase, runId));
        var runsDir = Path.Combine(Path.GetTempPath(), $"test-runs-zero-{Guid.NewGuid()}");
        Directory.CreateDirectory(runsDir);
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            File.WriteAllText(Path.Combine(runsDir, $"{runId}.json"), System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary { RunId = runId, IssueIdentifier = "1", IssueTitle = "Recent", FinalStep = PipelineStep.Failed, StartedAt = DateTime.UtcNow.AddSeconds(-5), CompletedAt = DateTime.UtcNow.AddSeconds(-1) }, jsonOptions));

            var service = new PipelineOrchestrationService(_mockConfigStore.Object, _mockFactory.Object, new IssueDescriptionParser(), _mockValidator.Object, new CiLogWriter(_mockLogger.Object), _mockLogger.Object, runsDirectory: runsDir);
            service.CleanupExpiredWorkspaces(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, FailedWorkspaceRetentionDays = 0 });

            Directory.Exists(Path.Combine(workspaceBase, runId)).Should().BeFalse();
        }
        finally { if (Directory.Exists(runsDir)) Directory.Delete(runsDir, true); if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    [Fact]
    public void CleanupExpiredWorkspaces_IncludesCancelledRuns()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-cancel-{Guid.NewGuid()}");
        var runId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(workspaceBase, runId));
        var runsDir = Path.Combine(Path.GetTempPath(), $"test-runs-cancel-{Guid.NewGuid()}");
        Directory.CreateDirectory(runsDir);
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            File.WriteAllText(Path.Combine(runsDir, $"{runId}.json"), System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary { RunId = runId, IssueIdentifier = "1", IssueTitle = "Cancelled", FinalStep = PipelineStep.Cancelled, StartedAt = DateTime.UtcNow.AddDays(-10), CompletedAt = DateTime.UtcNow.AddDays(-10) }, jsonOptions));

            var service = new PipelineOrchestrationService(_mockConfigStore.Object, _mockFactory.Object, new IssueDescriptionParser(), _mockValidator.Object, new CiLogWriter(_mockLogger.Object), _mockLogger.Object, runsDirectory: runsDir);
            service.CleanupExpiredWorkspaces(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, FailedWorkspaceRetentionDays = 7 });

            Directory.Exists(Path.Combine(workspaceBase, runId)).Should().BeFalse();
        }
        finally { if (Directory.Exists(runsDir)) Directory.Delete(runsDir, true); if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    // --- Stall detection ---

    [Fact]
    public async Task StallMonitor_StaleLastOutputTime_AddsSystemChatWarning()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = false },
                StallWarningInterval = TimeSpan.FromMilliseconds(100),
                StallPollInterval = TimeSpan.FromMilliseconds(200),
                AgentTimeout = TimeSpan.FromMinutes(5)
            });

        _mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 12345, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow.AddMinutes(-5) });

        var agentTcs = new TaskCompletionSource<AgentResult>();
        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) =>
            {
                callCount++;
                if (callCount <= 1) return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                return agentTcs.Task;
            });

        var pipelineTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1));

        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await pipelineTask;

        var run = _service.ActiveRun!;
        run.ChatHistory.Where(c => c.Role == ChatRole.System).Select(c => c.Content)
            .Should().Contain(msg => msg.Contains("No agent output for"));
    }

    [Fact]
    public async Task StallMonitor_ProcessDead_AddsSystemChatError()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = false },
                StallWarningInterval = TimeSpan.FromMilliseconds(100),
                StallPollInterval = TimeSpan.FromMilliseconds(200),
                AgentTimeout = TimeSpan.FromMinutes(5)
            });

        _mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 99999, IsProcessAlive = false, LastOutputTime = DateTime.UtcNow });

        var agentTcs = new TaskCompletionSource<AgentResult>();
        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) =>
            {
                callCount++;
                if (callCount <= 1) return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                return agentTcs.Task;
            });

        var pipelineTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1));

        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await pipelineTask;

        var run = _service.ActiveRun!;
        run.ChatHistory.Where(c => c.Role == ChatRole.System).Select(c => c.Content)
            .Should().Contain(msg => msg.Contains("Agent process is no longer alive") && msg.Contains("99999"));
    }

    [Fact]
    public async Task StallMonitor_WarningResetsAfterEachWarning()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = false },
                StallWarningInterval = TimeSpan.FromMilliseconds(100),
                StallPollInterval = TimeSpan.FromMilliseconds(200),
                AgentTimeout = TimeSpan.FromMinutes(5)
            });

        _mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 12345, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow.AddMinutes(-10) });

        var agentTcs = new TaskCompletionSource<AgentResult>();
        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) =>
            {
                callCount++;
                if (callCount <= 1) return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                return agentTcs.Task;
            });

        var pipelineTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2));

        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await pipelineTask;

        var run = _service.ActiveRun!;
        run.ChatHistory.Where(c => c.Role == ChatRole.System && c.Content.Contains("No agent output for"))
            .Should().HaveCountGreaterThanOrEqualTo(2);
    }

    // --- Provider validation ---

    [Fact]
    public async Task StartPipeline_ValidatesAllProvidersBeforeClone()
    {
        var callOrder = new List<string>();
        _mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Callback(() => callOrder.Add("IssueProvider.ValidateAsync")).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Callback(() => callOrder.Add("RepoProvider.ValidateAsync")).Returns(Task.CompletedTask);
        _mockAgentProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Callback(() => callOrder.Add("AgentProvider.ValidateAsync")).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback(() => callOrder.Add("RepoProvider.CloneAsync")).Returns(Task.CompletedTask);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        var lastValidateIndex = Math.Max(Math.Max(callOrder.IndexOf("IssueProvider.ValidateAsync"), callOrder.IndexOf("RepoProvider.ValidateAsync")), callOrder.IndexOf("AgentProvider.ValidateAsync"));
        callOrder.IndexOf("RepoProvider.CloneAsync").Should().BeGreaterThan(lastValidateIndex);
    }

    [Theory]
    [InlineData("Issue")]
    [InlineData("Repository")]
    [InlineData("Agent")]
    public async Task StartPipeline_WhenProviderValidationFails_FailsWithClearErrorNamingProvider(string providerKind)
    {
        var failureMessage = $"Invalid credentials for {providerKind}";
        switch (providerKind)
        {
            case "Issue": _mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException(failureMessage)); break;
            case "Repository": _mockRepoProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException(failureMessage)); break;
            case "Agent": _mockAgentProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException(failureMessage)); break;
        }

        var act = () => _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("validation failed");
        ex.Which.Message.Should().Contain(failureMessage);
        _service.ActiveRun!.CurrentStep.Should().Be(PipelineStep.Failed);
        _mockRepoProvider.Verify(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WhenExternalCiDisabled_SkipsPipelineProviderValidation()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), ExternalCiEnabled = false });

        var mockPipelineProvider = new Mock<IPipelineProvider>();
        _mockFactory.Setup(f => f.CreatePipelineProvider(It.IsAny<ProviderConfig>())).Returns(mockPipelineProvider.Object);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        _mockFactory.Verify(f => f.CreatePipelineProvider(It.IsAny<ProviderConfig>()), Times.Never);
        run.CurrentStep.Should().NotBe(PipelineStep.Failed);
    }

    [Fact]
    public async Task StartPipeline_WhenExternalCiEnabled_ValidatesPipelineProvider()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), ExternalCiEnabled = true });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "pipeline-1", Kind = ProviderKind.Pipeline, ProviderType = "GitHubActions", DisplayName = "CI" } });

        var mockPipelineProvider = new Mock<IPipelineProvider>();
        mockPipelineProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = Array.Empty<PipelineJobResult>() });
        _mockFactory.Setup(f => f.CreatePipelineProvider(It.IsAny<ProviderConfig>())).Returns(mockPipelineProvider.Object);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        mockPipelineProvider.Verify(p => p.ValidateAsync(It.IsAny<CancellationToken>()), Times.Once);
        run.CurrentStep.Should().NotBe(PipelineStep.Failed);
    }

    [Fact]
    public async Task StartPipeline_WhenPipelineProviderValidationFails_FailsWithClearError()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), ExternalCiEnabled = true });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "pipeline-1", Kind = ProviderKind.Pipeline, ProviderType = "GitHubActions", DisplayName = "CI" } });

        var mockPipelineProvider = new Mock<IPipelineProvider>();
        mockPipelineProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("GitHub API returned 401"));
        _mockFactory.Setup(f => f.CreatePipelineProvider(It.IsAny<ProviderConfig>())).Returns(mockPipelineProvider.Object);

        var act = () => _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Pipeline provider");
        ex.Which.Message.Should().Contain("validation failed");
        _service.ActiveRun!.CurrentStep.Should().Be(PipelineStep.Failed);
    }

    // --- Provider disposal ---

    [Fact]
    public async Task StartPipeline_DisposesPreviousProvidersBeforeCreatingNewOnes()
    {
        var firstIssueProvider = new Mock<IIssueProvider>();
        var firstRepoProvider = new Mock<IRepositoryProvider>();
        var firstAgentProvider = new Mock<IAgentProvider>();

        firstIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = Array.Empty<string>() });
        firstIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<IssueComment>());
        firstIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        firstRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        firstRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        firstRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        firstRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);
        firstRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        firstRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        firstRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        firstAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>())).ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        firstAgentProvider.Setup(p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        firstAgentProvider.Setup(p => p.GetHealthStatus()).Returns(new AgentHealthStatus { IsExecuting = false });

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(firstIssueProvider.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(firstRepoProvider.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(firstAgentProvider.Object);

        // Run first pipeline to completion
        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        var callOrder = new List<string>();
        firstIssueProvider.Setup(p => p.DisposeAsync()).Callback(() => callOrder.Add("Dispose:Issue")).Returns(ValueTask.CompletedTask);
        firstRepoProvider.Setup(p => p.DisposeAsync()).Callback(() => callOrder.Add("Dispose:Repository")).Returns(ValueTask.CompletedTask);
        firstAgentProvider.Setup(p => p.DisposeAsync()).Callback(() => callOrder.Add("Dispose:Agent")).Returns(ValueTask.CompletedTask);

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Callback(() => callOrder.Add("Create:Issue")).Returns(_mockIssueProvider.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Callback(() => callOrder.Add("Create:Repository")).Returns(_mockRepoProvider.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Callback(() => callOrder.Add("Create:Agent")).Returns(_mockAgentProvider.Object);

        await _service.StartPipelineAsync("issue-1", "repo-1", "99", "agent-1", CancellationToken.None);

        var lastDisposeIndex = new[] { "Dispose:Issue", "Dispose:Repository", "Dispose:Agent" }.Select(d => callOrder.IndexOf(d)).Max();
        var firstCreateIndex = new[] { "Create:Issue", "Create:Repository", "Create:Agent" }.Select(c => callOrder.IndexOf(c)).Min();
        firstCreateIndex.Should().BeGreaterThan(lastDisposeIndex);
    }

    // --- Risk Tier Tests ---

    // --- Multi-agent code review ---

    [Fact]
    public async Task StartPipeline_WithMultipleAgents_RunsAllSequentially()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, Agents = new[] { new ReviewAgentConfig { Name = "Correctness", Prompt = "Check correctness." }, new ReviewAgentConfig { Name = "DotNetSpecialist", Prompt = "Check .NET issues." } } }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewIterationsCompleted.Should().Be(1);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Check correctness.") && r.UseResume), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Check .NET issues.") && r.UseResume), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task StartPipeline_WithMultipleAgents_AggregatesSeverityCounts()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, Agents = new[] { new ReviewAgentConfig { Name = "Agent1", Prompt = "Agent1 prompt" }, new ReviewAgentConfig { Name = "Agent2", Prompt = "Agent2 prompt" } } }
            });

        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Agent1 prompt") || r.Prompt.Contains("Agent2 prompt")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                callCount++;
                var findings = callCount == 1
                    ? "[CRITICAL] Bug\n[WARNING] W1\n[WARNING] W2"
                    : "[SUGGESTION] S1";
                WriteReviewFindingsFile(req.WorkspacePath, findings);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewCriticalCount.Should().Be(1);
        run.CodeReviewWarningCount.Should().Be(2);
        run.CodeReviewSuggestionCount.Should().Be(1);
    }

    [Fact]
    public async Task StartPipeline_WithMultipleAgents_FixPromptSentOnceAfterAllAgents()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, FixPrompt = PipelineConfiguration.DefaultFixPrompt, Agents = new[] { new ReviewAgentConfig { Name = "Agent1", Prompt = "Agent1 prompt" }, new ReviewAgentConfig { Name = "Agent2", Prompt = "Agent2 prompt" } } }
            });

        var agentCallCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Agent1 prompt") || r.Prompt.Contains("Agent2 prompt")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                agentCallCount++;
                var findings = agentCallCount == 1
                    ? "[CRITICAL] Bug found"
                    : "[WARNING] Minor issue";
                WriteReviewFindingsFile(req.WorkspacePath, findings);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Fix only") && r.Prompt.Contains("[CRITICAL]") && r.UseResume), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task StartPipeline_WithMultipleAgents_NoCriticals_NoFixPrompt()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, FixPrompt = PipelineConfiguration.DefaultFixPrompt, Agents = new[] { new ReviewAgentConfig { Name = "Agent1", Prompt = "Agent1 prompt" }, new ReviewAgentConfig { Name = "Agent2", Prompt = "Agent2 prompt" } } }
            });

        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Agent1 prompt") || r.Prompt.Contains("Agent2 prompt")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                WriteReviewFindingsFile(req.WorkspacePath, "[WARNING] Minor");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Fix only")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WithNullAgents_FallsBackToSinglePrompt()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, Prompt = "Single review prompt.", Agents = null }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewIterationsCompleted.Should().Be(1);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Single review prompt.") && r.UseResume), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task StartPipeline_WithEmptyAgents_FallsBackToSinglePrompt()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, Prompt = "Fallback prompt.", Agents = Array.Empty<ReviewAgentConfig>() }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewIterationsCompleted.Should().Be(1);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Fallback prompt.") && r.UseResume), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task StartPipeline_WithMultipleAgents_TracksAgentNames()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, Agents = new[] { new ReviewAgentConfig { Name = "Correctness", Prompt = "Check correctness." }, new ReviewAgentConfig { Name = "DotNetSpecialist", Prompt = "Check .NET." } } }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewAgentsRun.Should().BeEquivalentTo(new[] { "Correctness", "DotNetSpecialist" });
    }

    [Fact]
    public async Task StartPipeline_WithMultipleAgents_SecondAgentFails_FirstAgentCountsPreserved()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, Agents = new[] { new ReviewAgentConfig { Name = "Agent1", Prompt = "Agent1 prompt" }, new ReviewAgentConfig { Name = "Agent2", Prompt = "Agent2 prompt" } } }
            });

        var agentCallCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Agent1 prompt") || r.Prompt.Contains("Agent2 prompt")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                agentCallCount++;
                if (agentCallCount == 2) throw new InvalidOperationException("Agent2 crashed");
                WriteReviewFindingsFile(req.WorkspacePath, "[CRITICAL] Bug\n[WARNING] W1");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewCriticalCount.Should().Be(1);
        run.CodeReviewWarningCount.Should().Be(1);
        run.CodeReviewAgentsRun.Should().BeEquivalentTo(new[] { "Agent1" });
        run.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task StartPipeline_WithMultipleAgentsAndIterations_RunsAgentsPerIteration()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 2, Agents = new[] { new ReviewAgentConfig { Name = "A1", Prompt = "A1 prompt" }, new ReviewAgentConfig { Name = "A2", Prompt = "A2 prompt" } } }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewIterationsCompleted.Should().Be(2);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("A1 prompt")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("A2 prompt")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
    }

    [Fact]
    public void CodeReviewDefaults_IncludeDefaultAgents()
    {
        var agents = PipelineConfiguration.DefaultReviewAgents;
        agents.Should().HaveCount(2);
        agents[0].Name.Should().Be("Correctness");
        agents[1].Name.Should().Be("DotNetSpecialist");
    }

    [Fact]
    public async Task StartPipeline_WithNullAgents_TracksReviewFallbackName()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, MaxIterations = 1, Agents = null }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewAgentsRun.Should().BeEquivalentTo(new[] { "Review" });
    }
}
