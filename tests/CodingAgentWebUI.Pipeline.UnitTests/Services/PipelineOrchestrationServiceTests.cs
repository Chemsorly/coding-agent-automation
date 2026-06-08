using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using KiroCliLib.Core;
using CodingAgentWebUI.Pipeline; // TODO: Redundant — namespace is implicitly accessible from child namespace CodingAgentWebUI.Pipeline.UnitTests

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for PipelineOrchestrationService.
/// </summary>
public class PipelineOrchestrationServiceTests : IDisposable
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

        var runHistory = new List<PipelineRunSummary>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistory()).Returns(() => runHistory.AsReadOnly());
        mockHistoryService.Setup(h => h.AddRunToHistory(It.IsAny<PipelineRun>()))
            .Callback<PipelineRun>(run => runHistory.Add(run.ToSummary()));
        mockHistoryService.Setup(h => h.TryDeleteWorkspace(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string?, string, string>((path, _, _) =>
            {
                if (path != null && Directory.Exists(path))
                    Directory.Delete(path, true);
            });

        _service = new PipelineOrchestrationService(
            _mockConfigStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            new AgentPhaseExecutor(_mockLogger.Object),
            new QualityGateExecutor(_mockValidator.Object, new PullRequestOrchestrator(_mockLogger.Object), _mockLogger.Object),
            _mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistoryService.Object);
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
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = _mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });
            _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QualityGateConfiguration>
                {
                    new() { Id = "default", DisplayName = "Default", CompilationCommand = "dotnet", CompilationArguments = ["build"], TestCommand = "dotnet", TestArguments = ["test"], Enabled = true }
                });
            _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ReviewerConfiguration>());

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
        _mockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        _mockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);

        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                // Write default analysis artifacts when the analysis prompt is detected
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                }
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });
        _mockAgentProvider.Setup(p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
        _mockAgentProvider.Setup(p => p.GetLatestSessionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
            });

        _mockRepoProvider.Setup(p => p.GetAgentPullRequestsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LinkedPullRequest>() as IReadOnlyList<LinkedPullRequest>);

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
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                return agentTcs.Task; // code generation blocks
            });

        var task = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        return (task, agentTcs);
    }

    /// <summary>
    /// Helper: writes a review findings file into the workspace so the orchestrator can read it.
    /// Extracts the per-agent findings file path from the prompt.
    /// </summary>
    private static void WriteReviewFindingsFile(string workspacePath, string content, string prompt)
    {
        // Extract the findings file path from the prompt (e.g., ".agent/review-findings-agentname.md")
        var match = System.Text.RegularExpressions.Regex.Match(prompt, @"Write your findings to the file `([^`]+)`");
        var relativePath = match.Success ? match.Groups[1].Value : ".agent/review-findings.md";
        var findingsPath = Path.Combine(workspacePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(findingsPath)!);
        File.WriteAllText(findingsPath, content);
    }

    /// <summary>
    /// Helper: sets up a mock review agent that writes findings to the workspace file and returns an empty AgentResult.
    /// </summary>
    private void SetupReviewAgentWithFindings(string promptMatch, string findingsContent)
    {
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains(promptMatch) && !r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                WriteReviewFindingsFile(req.WorkspacePath, findingsContent, req.Prompt);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });
    }

    [Fact]
    public async Task StartPipeline_WhenAlreadyRunning_ThrowsInvalidOperationException()
    {
        var (pipelineTask, agentTcs) = StartBlockingPipeline();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_service.ActiveRun?.CurrentStep != PipelineStep.GeneratingCode && DateTime.UtcNow < deadline)
            await Task.Delay(50);

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
                    Settings = new Dictionary<string, string> { [ProviderSettingKeys.Model] = "claude-sonnet-4.6" }
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
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_service.ActiveRun?.CurrentStep != PipelineStep.GeneratingCode && DateTime.UtcNow < deadline)
            await Task.Delay(50);

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
                    Settings = new Dictionary<string, string> { [ProviderSettingKeys.Model] = "claude-opus-4.6" }
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
            PipelineStep.PreparingForPullRequest,
            PipelineStep.RunningQualityGates,
            PipelineStep.CreatingPullRequest,
            PipelineStep.Completed);
    }

    [Fact]
    public async Task StartPipeline_WhenQualityGatesFail_CreatesDraftPrAfterRetries()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), MaxRetries = 1 });

        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
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
                AnalysisReviewEnabled = false,
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1 }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Default", Agents = new[] { new ReviewAgent { Name = "Correctness", Prompt = "Review the changes as a sub-agent." } } }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.CodeReviewIterationsCompleted.Should().Be(1);
    }

    [Fact]
    public async Task StartPipeline_WithCodeReviewDisabled_SkipsReview()
    {
        // No reviewer configs resolved → review is skipped
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>());

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
                AnalysisReviewEnabled = false,
                CodeReview = new CodeReviewConfiguration { MaxIterations = 3 }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Default", Agents = new[] { new ReviewAgent { Name = "Correctness", Prompt = "Review the changes." } } }
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
                AnalysisReviewEnabled = false,
                CodeReview = new CodeReviewConfiguration { MaxIterations = 3 }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Review", Prompt = "Review the changes." } } }
            });

        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Review the changes") && !r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                callCount++;
                if (callCount >= 2) throw new InvalidOperationException("Agent crashed");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.CodeReviewIterationsCompleted.Should().Be(1);
    }

    [Fact]
    public async Task CodeReviewPrompt_IsConfigurableViaReviewerConfig()
    {
        var customPrompt = "Custom review: check for bugs only.";
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                AnalysisReviewEnabled = false,
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1 }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Custom", Agents = new[] { new ReviewAgent { Name = "CustomReviewer", Prompt = customPrompt } } }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains(customPrompt) && r.Prompt.Contains("Test Issue") && !r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Once);
    }

    [Fact]
    public void CodeReviewDefaults_AreCorrect()
    {
        var config = new PipelineConfiguration();
        config.CodeReview.MaxIterations.Should().Be(2);
        config.CodeReview.FixPrompt.Should().BeNull();
        config.CodeReview.ReviewIsolation.Should().Be(ReviewIsolation.Isolated);
        PipelineConfiguration.DefaultReviewAgents.Should().NotBeNull();
        PipelineConfiguration.DefaultReviewAgents.Count.Should().Be(4);
        PipelineConfiguration.DefaultReviewAgents[0].Name.Should().Be("Correctness");
        PipelineConfiguration.DefaultReviewAgents[1].Name.Should().Be("DotNetSpecialist");
        PipelineConfiguration.DefaultReviewAgents[2].Name.Should().Be("SecurityReviewer");
        PipelineConfiguration.DefaultReviewAgents[3].Name.Should().Be("AcceptanceCriteria");
    }

    // --- Fix prompt tests ---

    [Fact]
    public async Task StartPipeline_WithFixPromptAndCriticals_SendsFixPrompt()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                AnalysisReviewEnabled = false,
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = PipelineConfiguration.DefaultFixPrompt }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Review", Prompt = "Review the changes." } } }
            });

        SetupReviewAgentWithFindings("Review the changes", "[CRITICAL] Missing null check\n[WARNING] Consider renaming");

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
                AnalysisReviewEnabled = false,
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = PipelineConfiguration.DefaultFixPrompt }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Review", Prompt = "Review the changes." } } }
            });

        SetupReviewAgentWithFindings("Review the changes", "[WARNING] Consider renaming\n[SUGGESTION] Use var");

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
                AnalysisReviewEnabled = false,
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = null }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Review", Prompt = "Review the changes." } } }
            });

        SetupReviewAgentWithFindings("Review the changes", "[CRITICAL] Missing null check");

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
                AnalysisReviewEnabled = false,
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1 }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Review", Prompt = "Review the changes." } } }
            });

        SetupReviewAgentWithFindings("Review the changes",
            "[CRITICAL] Bug A\n[CRITICAL] Bug B\n[WARNING] Style issue\n[SUGGESTION] Rename X\n[SUGGESTION] Rename Y\n[SUGGESTION] Rename Z");

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CodeReviewCriticalCount.Should().Be(2);
        run.CodeReviewWarningCount.Should().Be(1);
        run.CodeReviewSuggestionCount.Should().Be(3);
        run.CodeReviewAgentFindings.Should().NotBeEmpty();
    }

    // --- Blacklist enforcement ---

    [Fact]
    public async Task StartPipeline_WarnAndExclude_PopulatesBlacklistedFilesAndCompletes()
    {
        var blacklisted = new List<string> { ".agent/steering/rule.md", ".github/workflows/ci.yml" };
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blacklisted as IReadOnlyList<string>);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.BlacklistedFilesDetected.Should().Contain(".agent/steering/rule.md");
        run.BlacklistedFilesDetected.Should().Contain(".github/workflows/ci.yml");
    }

    [Fact]
    public async Task StartPipeline_WarnMode_RecordsBlacklistedFilesAndContinues()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), BlacklistMode = BlacklistMode.WarnAndExclude });

        var blacklisted = new List<string> { ".github/workflows/ci.yml" };
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blacklisted as IReadOnlyList<string>);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
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
    public void FailedWorkspaceRetentionDays_DefaultsToSeven()
    {
        var config = new PipelineConfiguration();
        config.FailedWorkspaceRetentionDays.Should().Be(7);
    }

    // --- Workspace cleanup ---

    [Fact]
    public async Task SuccessfulPr_DeletesWorkspace()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-cleanup-{Guid.NewGuid()}");
        Directory.CreateDirectory(workspaceBase);
        try
        {
            _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase });

            var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
            run.CurrentStep.Should().Be(PipelineStep.Completed);
            if (run.WorkspacePath != null)
                Directory.Exists(run.WorkspacePath).Should().BeFalse();
        }
        finally { if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    [Fact]
    public async Task DraftPr_RetainsWorkspace()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-draft-{Guid.NewGuid()}");
        Directory.CreateDirectory(workspaceBase);
        try
        {
            _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, MaxRetries = 0 });

            _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
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

    // --- Stall detection ---

    [Fact]
    public async Task StallMonitor_StaleLastOutputTime_AddsSystemChatWarning()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                StallWarningInterval = TimeSpan.FromMilliseconds(100),
                StallPollInterval = TimeSpan.FromMilliseconds(200),
                AgentTimeout = TimeSpan.FromMinutes(5),
                AnalysisReviewEnabled = false
            });

        _mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 12345, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow.AddMinutes(-5) });

        var agentTcs = new TaskCompletionSource<AgentResult>();
        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) =>
            {
                callCount++;
                if (callCount <= 1)
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                return agentTcs.Task;
            });

        var pipelineTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_service.ActiveRun?.ChatHistory.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await pipelineTask;

        var run = _service.ActiveRun!;
        run.ChatHistory.Where(c => c.Role == ChatRole.System).Select(c => c.Content)
            .Should().Contain(msg => msg.Contains("no output for"));
    }

    [Fact]
    public async Task StallMonitor_ProcessDead_AddsSystemChatError()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                StallWarningInterval = TimeSpan.FromMilliseconds(100),
                StallPollInterval = TimeSpan.FromMilliseconds(200),
                AgentTimeout = TimeSpan.FromMinutes(5),
                AnalysisReviewEnabled = false
            });

        _mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 99999, IsProcessAlive = false, LastOutputTime = DateTime.UtcNow });

        var agentTcs = new TaskCompletionSource<AgentResult>();
        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) =>
            {
                callCount++;
                if (callCount <= 1)
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                return agentTcs.Task;
            });

        var pipelineTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_service.ActiveRun?.ChatHistory.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await pipelineTask;

        var run = _service.ActiveRun!;
        run.ChatHistory.Where(c => c.Role == ChatRole.System).Select(c => c.Content)
            .Should().Contain(msg => msg.Contains("agent process is no longer alive") && msg.Contains("99999"));
    }

    [Fact]
    public async Task StallMonitor_WarningResetsAfterEachWarning()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                StallWarningInterval = TimeSpan.FromMilliseconds(100),
                StallPollInterval = TimeSpan.FromMilliseconds(200),
                AgentTimeout = TimeSpan.FromHours(1),
                AnalysisReviewEnabled = false
            });

        _mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 12345, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow.AddMinutes(-10) });

        var agentTcs = new TaskCompletionSource<AgentResult>();
        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) =>
            {
                callCount++;
                if (callCount <= 1)
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                return agentTcs.Task;
            });

        var pipelineTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((_service.ActiveRun?.ChatHistory.Where(c => c.Role == ChatRole.System && c.Content.Contains("no output for")).Count() ?? 0) < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await pipelineTask;

        var run = _service.ActiveRun!;
        run.ChatHistory.Where(c => c.Role == ChatRole.System && c.Content.Contains("no output for"))
            .Should().HaveCountGreaterThanOrEqualTo(2);
    }

    // --- Provider validation ---

    [Fact]
    public async Task StartPipeline_ValidatesAllProvidersBeforeClone()
    {
        var callOrder = new List<string>();
        _mockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>())).Callback(() => callOrder.Add("IssueProvider.InitializeAsync")).ReturnsAsync(true);
        _mockRepoProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Callback(() => callOrder.Add("RepoProvider.ValidateAsync")).Returns(Task.CompletedTask);
        _mockAgentProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Callback(() => callOrder.Add("AgentProvider.ValidateAsync")).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback(() => callOrder.Add("RepoProvider.CloneAsync")).Returns(Task.CompletedTask);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        var lastValidateIndex = Math.Max(Math.Max(callOrder.IndexOf("IssueProvider.InitializeAsync"), callOrder.IndexOf("RepoProvider.ValidateAsync")), callOrder.IndexOf("AgentProvider.ValidateAsync"));
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
            case "Issue":
                _mockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException(failureMessage));
                break;
            case "Repository": _mockRepoProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException(failureMessage)); break;
            case "Agent": _mockAgentProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException(failureMessage)); break;
        }

        var act = () => _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain(failureMessage);
        _service.ActiveRun!.CurrentStep.Should().Be(PipelineStep.Failed);
        _mockRepoProvider.Verify(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WhenNoPipelineProviderConfigured_SkipsPipelineProviderCreation()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var mockPipelineProvider = new Mock<IPipelineProvider>();
        _mockFactory.Setup(f => f.CreatePipelineProvider(It.IsAny<ProviderConfig>())).Returns(mockPipelineProvider.Object);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        _mockFactory.Verify(f => f.CreatePipelineProvider(It.IsAny<ProviderConfig>()), Times.Never);
        run.CurrentStep.Should().NotBe(PipelineStep.Failed);
    }

    [Fact]
    public async Task StartPipeline_WhenPipelineProviderConfigured_ValidatesPipelineProvider()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() });
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
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() });
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
        firstIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
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
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1 }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Correctness", Prompt = "Check correctness." }, new ReviewAgent { Name = "DotNetSpecialist", Prompt = "Check .NET issues." } } }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewIterationsCompleted.Should().Be(1);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Check correctness.") && !r.UseResume), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Check .NET issues.") && !r.UseResume), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task StartPipeline_WithMultipleAgents_AggregatesSeverityCounts()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1 }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Agent1", Prompt = "Agent1 prompt" }, new ReviewAgent { Name = "Agent2", Prompt = "Agent2 prompt" } } }
            });

        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Agent1 prompt") || r.Prompt.Contains("Agent2 prompt")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                callCount++;
                var findings = callCount == 1
                    ? "[CRITICAL] Bug\n[WARNING] W1\n[WARNING] W2"
                    : "[SUGGESTION] S1";
                WriteReviewFindingsFile(req.WorkspacePath, findings, req.Prompt);
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
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = PipelineConfiguration.DefaultFixPrompt }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Agent1", Prompt = "Agent1 prompt" }, new ReviewAgent { Name = "Agent2", Prompt = "Agent2 prompt" } } }
            });

        var agentCallCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Agent1 prompt") || r.Prompt.Contains("Agent2 prompt")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                agentCallCount++;
                var findings = agentCallCount == 1
                    ? "[CRITICAL] Bug found"
                    : "[WARNING] Minor issue";
                WriteReviewFindingsFile(req.WorkspacePath, findings, req.Prompt);
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
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = PipelineConfiguration.DefaultFixPrompt }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Agent1", Prompt = "Agent1 prompt" }, new ReviewAgent { Name = "Agent2", Prompt = "Agent2 prompt" } } }
            });

        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Agent1 prompt") || r.Prompt.Contains("Agent2 prompt")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                WriteReviewFindingsFile(req.WorkspacePath, "[WARNING] Minor", req.Prompt);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Fix only")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WithNoReviewerConfigs_SkipsReview()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1 }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewIterationsCompleted.Should().Be(0);
    }

    [Fact]
    public async Task StartPipeline_WithMultipleAgents_TracksAgentNames()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1 }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Correctness", Prompt = "Check correctness." }, new ReviewAgent { Name = "DotNetSpecialist", Prompt = "Check .NET." } } }
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
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1 }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Agent1", Prompt = "Agent1 prompt" }, new ReviewAgent { Name = "Agent2", Prompt = "Agent2 prompt" } } }
            });

        var agentCallCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Agent1 prompt") || r.Prompt.Contains("Agent2 prompt")), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                agentCallCount++;
                if (agentCallCount == 2) throw new InvalidOperationException("Agent2 crashed");
                WriteReviewFindingsFile(req.WorkspacePath, "[CRITICAL] Bug\n[WARNING] W1", req.Prompt);
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
                CodeReview = new CodeReviewConfiguration { MaxIterations = 2 }
            });
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "A1", Prompt = "A1 prompt" }, new ReviewAgent { Name = "A2", Prompt = "A2 prompt" } } }
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
        agents.Should().HaveCount(4);
        agents[0].Name.Should().Be("Correctness");
        agents[1].Name.Should().Be("DotNetSpecialist");
        agents[2].Name.Should().Be("SecurityReviewer");
        agents[3].Name.Should().Be("AcceptanceCriteria");
    }

    [Fact]
    public async Task StartPipeline_WithNoReviewerConfigs_TracksNoReview()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { MaxIterations = 1 }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CodeReviewIterationsCompleted.Should().Be(0);
    }

    [Fact]
    public async Task StartPipeline_CallsInitializeAsync()
    {
        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        _mockIssueProvider.Verify(p => p.InitializeAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartPipeline_InitializeAsyncLabelFailure_PipelineContinues()
    {
        _mockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task StartPipeline_InitializeAsyncCredentialFailure_PipelineFails()
    {
        _mockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Authentication failed: installation token was rejected"));

        var act = () => _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Issue provider initialization failed");
        _service.ActiveRun!.CurrentStep.Should().Be(PipelineStep.Failed);
    }

    [Fact]
    public async Task StartPipeline_SwapsToInProgressAtCloningRepository()
    {
        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Should add agent:in-progress during the clone step
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:in-progress", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        // Other labels should be removed (at least once across all swaps)
        _mockIssueProvider.Verify(p => p.RemoveLabelAsync("42", AgentLabels.Next, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _mockIssueProvider.Verify(p => p.RemoveLabelAsync("42", AgentLabels.Error, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartPipeline_AddsAgentDoneLabelOnCompletion()
    {
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        // On successful completion, agent:done label should be applied
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", AgentLabels.Done, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        // All other agent labels should be removed (SwapAgentLabelAsync removes all before adding the new one)
        foreach (var label in AgentLabels.All)
            _mockIssueProvider.Verify(p => p.RemoveLabelAsync("42", label, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartPipeline_SwapsToErrorOnFailure()
    {
        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Clone failed"));

        try
        {
            await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        }
        catch { }

        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CancelPipeline_RemovesAgentLabels()
    {
        // Use a blocking agent to keep pipeline running
        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);

        var pipelineTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Wait for pipeline to reach a running state
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_service.IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        await _service.CancelPipelineAsync();
        tcs.TrySetCanceled();

        try { await pipelineTask; } catch { }

        _service.ActiveRun!.CurrentStep.Should().Be(PipelineStep.Cancelled);
        // Verify agent:cancelled label is applied
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:cancelled", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        // Verify removal of other labels was attempted
        _mockIssueProvider.Verify(p => p.RemoveLabelAsync("42", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartPipeline_LabelOperationFailure_PipelineContinues()
    {
        _mockIssueProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API rate limit"));
        _mockIssueProvider.Setup(p => p.AddLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API rate limit"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Pipeline should complete despite label failures
        run.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task StartPipeline_QualityGateFailure_SwapsToErrorLabel()
    {
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Validation error"));

        try
        {
            await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        }
        catch { }

        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // --- Confidence gate tests ---

    /// <summary>
    /// Helper: writes an analysis.md file into the workspace so the orchestrator can read it.
    /// </summary>
    private static void WriteAnalysisFile(string workspacePath, string content)
    {
        var dir = Path.Combine(workspacePath, ".agent");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "analysis.md"), content);
    }

    /// <summary>
    /// Helper: writes an analysis-assessment.json file into the workspace so the orchestrator can read it.
    /// </summary>
    private static void WriteAssessmentFile(string workspacePath, string recommendation, string? reason = null,
        string[]? concerns = null, string[]? blockingIssues = null)
    {
        var dir = Path.Combine(workspacePath, ".agent");
        Directory.CreateDirectory(dir);
        var obj = new
        {
            recommendation,
            reason = reason ?? "Test reason",
            concerns = concerns ?? Array.Empty<string>(),
            blockingIssues = blockingIssues ?? Array.Empty<string>()
        };
        File.WriteAllText(Path.Combine(dir, "analysis-assessment.json"),
            System.Text.Json.JsonSerializer.Serialize(obj, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    }

    /// <summary>
    /// Helper: sets up the analysis agent call to write an analysis file and assessment file with the given recommendation.
    /// </summary>
    private void SetupAnalysisAgentWithAssessment(string recommendation, string? reason = null,
        string[]? concerns = null, string[]? blockingIssues = null)
    {
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                WriteAssessmentFile(req.WorkspacePath, recommendation, reason, concerns, blockingIssues);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });
    }

    /// <summary>
    /// Holds all mocks and the service instance for brain-related tests (REQ-3, REQ-4, REQ-8, REQ-9).
    /// </summary>
    private record BrainTestContext(
        PipelineOrchestrationService Service,
        Mock<IIssueProvider> MockIssueProvider,
        Mock<IRepositoryProvider> MockRepoProvider,
        Mock<IRepositoryProvider> MockBrainProvider,
        Mock<IAgentProvider> MockAgentProvider,
        Mock<IQualityGateValidator> MockValidator,
        Mock<IBrainUpdateService> MockBrainUpdateService,
        Mock<IPipelineRunHistoryService> MockHistoryService);

    /// <summary>
    /// Creates a fully-configured service instance for brain tests.
    /// All mocks are set up for the happy-path brain sync scenario (REQ-3).
    /// Individual tests override only the 1-3 mocks that differ.
    /// </summary>
    private static BrainTestContext CreateBrainTestService()
    {
        var mockConfigStore = new Mock<IConfigurationStore>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockIssueProvider = new Mock<IIssueProvider>();
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        var mockBrainProvider = new Mock<IRepositoryProvider>();
        var mockAgentProvider = new Mock<IAgentProvider>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockBrainUpdateService = new Mock<IBrainUpdateService>();
        var mockLogger = new Mock<Serilog.ILogger>();

        // Config store: pipeline config with BrainReadOnly = false
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" }
            });
        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" },
                new() { Id = "brain-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Brain" }
            });
        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test" }
            });
        mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });
        mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>
            {
                new() { Id = "default", DisplayName = "Default", CompilationCommand = "dotnet", CompilationArguments = ["build"], TestCommand = "dotnet", TestArguments = ["test"], Enabled = true }
            });
        mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>());

        // Issue provider
        mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42", Title = "Test Issue", Description = "Test description",
                Labels = Array.Empty<string>()
            });
        mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        mockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Main repo provider
        mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        mockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);

        // Brain provider: CloneAsync succeeds
        mockBrainProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockBrainProvider.Setup(p => p.PullAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Agent provider: writes analysis files for analysis prompt, succeeds for all others
        mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                }
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });
        mockAgentProvider.Setup(p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });

        // Quality gate validator: all pass
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
            });

        // Factory: config-specific matching to distinguish brain provider from main repo provider
        mockFactory.Setup(f => f.CreateRepositoryProvider(
            It.Is<ProviderConfig>(c => c.Id == "brain-1")))
            .Returns(mockBrainProvider.Object);
        mockFactory.Setup(f => f.CreateRepositoryProvider(
            It.Is<ProviderConfig>(c => c.Id == "repo-1")))
            .Returns(mockRepoProvider.Object);
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockIssueProvider.Object);
        mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(mockAgentProvider.Object);

        // IBrainUpdateService: DetectChangesAsync returns changed files, Validate returns success, CommitAndPushAsync succeeds
        mockBrainUpdateService.Setup(b => b.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "sessions/session-1.md" } as IReadOnlyList<string>);
        mockBrainUpdateService.Setup(b => b.Validate(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(new BrainValidationResult { SessionLogCreated = true, OperationLogUpdated = true, EntryFormatValid = true });
        mockBrainUpdateService.Setup(b => b.CommitAndPushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrainSyncResult { Success = true, FilesCommitted = 1 });

        // History service
        var runHistory = new List<PipelineRunSummary>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistory()).Returns(() => runHistory.AsReadOnly());
        mockHistoryService.Setup(h => h.AddRunToHistory(It.IsAny<PipelineRun>()))
            .Callback<PipelineRun>(run => runHistory.Add(run.ToSummary()));
        mockHistoryService.Setup(h => h.TryDeleteWorkspace(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string?, string, string>((path, _, _) =>
            {
                if (path != null && Directory.Exists(path))
                    Directory.Delete(path, true);
            });

        var service = new PipelineOrchestrationService(
            mockConfigStore.Object,
            mockFactory.Object,
            new IssueDescriptionParser(),
            new AgentPhaseExecutor(mockLogger.Object),
            new QualityGateExecutor(mockValidator.Object, new PullRequestOrchestrator(mockLogger.Object), mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: mockBrainUpdateService.Object,
            historyService: mockHistoryService.Object);

        return new BrainTestContext(service, mockIssueProvider, mockRepoProvider, mockBrainProvider,
            mockAgentProvider, mockValidator, mockBrainUpdateService, mockHistoryService);
    }

    [Fact]
    public async Task ConfidenceGate_ReadyAssessment_ProceedsToCodeGeneration()
    {
        SetupAnalysisAgentWithAssessment("ready", "Issue is well-scoped");

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
        run.PullRequestUrl.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ConfidenceGate_NotReadyAssessment_AbortsPipelineWithNeedsRefinement()
    {
        SetupAnalysisAgentWithAssessment("not_ready", "Issue is too vague",
            blockingIssues: new[] { "No acceptance criteria" });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("needs refinement");
        run.AnalysisRecommendation.Should().Be(AnalysisGateResult.NotReady);
        run.AnalysisBlockingIssues.Should().Contain("No acceptance criteria");
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:needs-refinement", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _mockIssueProvider.Verify(p => p.PostCommentAsync("42", It.Is<string>(s => s.Contains("<!-- agent:gate-rejection -->")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfidenceGate_WontDoAssessment_CompletesWithWontDoLabel()
    {
        SetupAnalysisAgentWithAssessment("wont_do", "Bug already fixed in PR #134");

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.FailureReason.Should().Contain("won't do");
        run.AnalysisRecommendation.Should().Be(AnalysisGateResult.WontDo);
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:wont-do", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _mockIssueProvider.Verify(p => p.PostCommentAsync("42", It.Is<string>(s => s.Contains("<!-- agent:gate-wont-do -->")), It.IsAny<CancellationToken>()), Times.Once);
        // Should NOT proceed to code generation
        _mockAgentProvider.Verify(p => p.ExecuteAsync(
            It.Is<AgentRequest>(r => r.Prompt.Contains("Implement")),
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task ConfidenceGate_MissingAssessmentFile_FailsPipeline()
    {
        // Agent writes analysis.md but not assessment.json — should retry then fail
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                // No assessment file written
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("analysis-assessment.json");
    }

    [Fact]
    public async Task ConfidenceGate_MalformedJson_FailsPipeline()
    {
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                var dir = Path.Combine(req.WorkspacePath, ".agent");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "analysis-assessment.json"), "{ invalid json }}}");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("malformed JSON");
    }

    [Fact]
    public async Task ConfidenceGate_BlockingIssuesOverridesReady()
    {
        SetupAnalysisAgentWithAssessment("ready", "Looks good",
            blockingIssues: new[] { "Missing API endpoint" });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("needs refinement");
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:needs-refinement", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ConfidenceGate_BlockingIssuesOverridesWontDo()
    {
        SetupAnalysisAgentWithAssessment("wont_do", "Not needed",
            blockingIssues: new[] { "Contradictory requirements" });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("needs refinement");
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:needs-refinement", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        // Should NOT get wont-do label
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:wont-do", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfidenceGate_ScopeRelatedBlockingIssues_TriggersNotReady()
    {
        SetupAnalysisAgentWithAssessment("not_ready", "Scope too broad for a single agent run",
            blockingIssues: new[] { "Issue affects 50+ files across 5 projects — split by project: UI components, Infrastructure, Tests" });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("needs refinement");
        run.AnalysisBlockingIssues.Should().Contain(b => b.Contains("split by project"));
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:needs-refinement", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ConfidenceGate_UnexpectedRecommendation_ProceedsAsReady()
    {
        SetupAnalysisAgentWithAssessment("maybe", "Not sure about this");

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.AnalysisRecommendation.Should().BeNull();
        run.PullRequestUrl.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ConfidenceGate_WontDoRunInHistory_ShowsCompleted()
    {
        SetupAnalysisAgentWithAssessment("wont_do", "Already implemented");

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        var history = _service.GetRunHistory();
        history.Should().HaveCount(1);
        history[0].FinalStep.Should().Be(PipelineStep.Completed);
        history[0].AnalysisRecommendation.Should().Be(AnalysisGateResult.WontDo);
    }

    [Fact]
    public async Task ConfidenceGate_RequeuedAfterRejection_ForcesRefreshAnalysis()
    {
        // Simulate a previous gate rejection comment that is newer than the analysis comment
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>
            {
                new() { Id = "1", Body = "## 🤖 Agent Analysis\n\nOld analysis.", Author = "bot", CreatedAt = DateTime.UtcNow.AddHours(-2) },
                new() { Id = "2", Body = "## ⚠️ Analysis Gate: Needs Refinement\n\n<!-- agent:gate-rejection -->", Author = "bot", CreatedAt = DateTime.UtcNow.AddHours(-1) }
            });

        SetupAnalysisAgentWithAssessment("ready", "Now it's good");

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Should have run fresh analysis (not skipped)
        run.AnalysisSkipped.Should().BeFalse();
        run.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
        _mockAgentProvider.Verify(p => p.ExecuteAsync(
            It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task ConfidenceGate_RequeuedAfterWontDo_ForcesRefreshAnalysis()
    {
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>
            {
                new() { Id = "1", Body = "## 🤖 Agent Analysis\n\nOld analysis.", Author = "bot", CreatedAt = DateTime.UtcNow.AddHours(-2) },
                new() { Id = "2", Body = "## 🚫 Analysis Gate: Won't Do\n\n<!-- agent:gate-wont-do -->", Author = "bot", CreatedAt = DateTime.UtcNow.AddHours(-1) }
            });

        SetupAnalysisAgentWithAssessment("ready", "Re-evaluated");

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.AnalysisSkipped.Should().BeFalse();
        _mockAgentProvider.Verify(p => p.ExecuteAsync(
            It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task ConfidenceGate_ExistingAnalysisWithNoGateMarker_SkipsAnalysis()
    {
        // Analysis comment exists, no gate markers — should reuse existing analysis
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>
            {
                new() { Id = "1", Body = "## 🤖 Agent Analysis\n\nExisting analysis.", Author = "bot", CreatedAt = DateTime.UtcNow.AddHours(-1) }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.AnalysisSkipped.Should().BeTrue();
        run.AnalysisContent.Should().Contain("Existing analysis.");
    }

    [Fact]
    public async Task ConfidenceGate_NotReadyPostsAnalysisCommentFirst()
    {
        SetupAnalysisAgentWithAssessment("not_ready", "Vague issue",
            blockingIssues: new[] { "No AC" });

        var commentOrder = new List<string>();
        _mockIssueProvider.Setup(p => p.PostCommentAsync("42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, body, _) => commentOrder.Add(body.Contains("Agent Analysis") ? "analysis" : "gate"))
            .Returns(Task.CompletedTask);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        commentOrder.Should().ContainInOrder("analysis", "gate");
    }

    [Fact]
    public async Task ConfidenceGate_WontDoPostsAnalysisCommentFirst()
    {
        SetupAnalysisAgentWithAssessment("wont_do", "Already fixed");

        var commentOrder = new List<string>();
        _mockIssueProvider.Setup(p => p.PostCommentAsync("42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, body, _) => commentOrder.Add(body.Contains("Agent Analysis") ? "analysis" : "gate"))
            .Returns(Task.CompletedTask);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        commentOrder.Should().ContainInOrder("analysis", "gate");
    }

    // --- Pipeline event output tests ---

    [Fact]
    public async Task PipelineEvents_HappyPath_EmitsExpectedOutputLines()
    {
        var outputLines = new List<string>();
        _service.OnOutputLine += line => outputLines.Add(line);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);

        outputLines.Should().Contain(l => l.StartsWith("🚀 Pipeline started"));
        outputLines.Should().Contain(l => l.StartsWith("📋 Cloning repository"));
        outputLines.Should().Contain(l => l.StartsWith("🌿 Creating branch"));
        outputLines.Should().Contain(l => l.StartsWith("🌿 Created branch"));
        outputLines.Should().Contain(l => l.StartsWith("🔍 Starting analysis"));
        outputLines.Should().Contain(l => l.StartsWith("⚙️ Starting code generation"));
        outputLines.Should().Contain(l => l.StartsWith("🏗️ Running quality gates"));
        outputLines.Should().Contain(l => l.StartsWith("🏗️ Quality gates:"));
        outputLines.Should().Contain(l => l.StartsWith("✅ Pipeline completed"));
    }

    [Fact]
    public async Task PipelineEvents_StartMessage_ContainsIssueIdentifierAndTitle()
    {
        var outputLines = new List<string>();
        _service.OnOutputLine += line => outputLines.Add(line);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        outputLines.Should().Contain(l => l.Contains("#42") && l.Contains("Test Issue"));
    }

    [Fact]
    public async Task PipelineEvents_QualityGateSummary_ContainsGateStatuses()
    {
        var outputLines = new List<string>();
        _service.OnOutputLine += line => outputLines.Add(line);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        var qgLine = outputLines.FirstOrDefault(l => l.StartsWith("🏗️ Quality gates:"));
        qgLine.Should().NotBeNull();
        qgLine.Should().Contain("Compilation ✅");
        qgLine.Should().Contain("Tests ✅");
    }

    [Fact]
    public async Task PipelineEvents_FailedRun_EmitsFailureMessage()
    {
        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "42", Title = "", Description = "desc", Labels = Array.Empty<string>() });

        var outputLines = new List<string>();
        _service.OnOutputLine += line => outputLines.Add(line);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        outputLines.Should().Contain(l => l.StartsWith("❌ Pipeline failed:"));
    }

    [Fact]
    public async Task PipelineEvents_CancelledRun_EmitsCancelMessage()
    {
        var (pipelineTask, agentTcs) = StartBlockingPipeline();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_service.ActiveRun?.CurrentStep != PipelineStep.GeneratingCode && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        var outputLines = new List<string>();
        _service.OnOutputLine += line => outputLines.Add(line);

        await _service.CancelPipelineAsync();
        agentTcs.TrySetCanceled();
        try { await pipelineTask; } catch { }

        outputLines.Should().Contain(l => l.StartsWith("🚫 Pipeline cancelled"));
    }

    [Fact]
    public async Task PipelineEvents_QualityGateRetry_EmitsRetryMessage()
    {
        var callCount = 0;
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? new QualityGateReport
                    {
                        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build failed" },
                        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
                    }
                    : new QualityGateReport
                    {
                        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
                    };
            });

        var outputLines = new List<string>();
        _service.OnOutputLine += line => outputLines.Add(line);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        outputLines.Should().Contain(l => l.StartsWith("🔄 Quality gates failed, retrying (attempt 1/"));
        // Should have two QG summary lines (initial fail + retry pass)
        outputLines.Where(l => l.StartsWith("🏗️ Quality gates:")).Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task PipelineEvents_CodeReview_EmitsReviewMessages()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.WithCodeReview());
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Default", Agents = new[] { new ReviewAgent { Name = "Correctness", Prompt = "Review the following changes." } } }
            });

        SetupReviewAgentWithFindings("Review the following", "[CRITICAL] Test finding\n[WARNING] Another finding");

        var outputLines = new List<string>();
        _service.OnOutputLine += line => outputLines.Add(line);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        outputLines.Should().Contain(l => l.StartsWith("🔍 Starting code review iteration"));
        outputLines.Should().Contain(l => l.StartsWith("📝 Code review:") && l.Contains("critical"));
    }

    // --- Analysis failure hardening tests (RES-06) ---

    [Fact]
    public async Task Analysis_MissingAnalysisMd_RetriesAndSucceeds()
    {
        var attempt = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                attempt++;
                if (attempt >= 2)
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                }
                // First attempt: no files written
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
    }

    [Fact]
    public async Task Analysis_MissingAssessmentJson_RetriesAndSucceeds()
    {
        var attempt = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                attempt++;
                WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                if (attempt >= 2)
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                // First attempt: analysis.md written but no assessment
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
    }

    [Fact]
    public async Task Analysis_PartialAnalysisMd_RetriesAndSucceeds()
    {
        var attempt = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                attempt++;
                if (attempt >= 2)
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                else
                    WriteAnalysisFile(req.WorkspacePath, "short"); // < 100 chars
                WriteAssessmentFile(req.WorkspacePath, "ready");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task Analysis_RetryBudgetExhausted_PipelineFails()
    {
        // Agent never writes analysis files
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("Analysis failed after 2 attempt(s)");
        run.CompletedAt.Should().NotBeNull();
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Analysis_StaleArtifactsDeletedBeforeRetry()
    {
        var attempt = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                attempt++;
                if (attempt == 1)
                {
                    // Write stale artifacts that should be cleaned up
                    WriteAnalysisFile(req.WorkspacePath, "stale short content");
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                // Second attempt: verify stale files were deleted, then write valid ones
                var analysisPath = Path.Combine(req.WorkspacePath, ".agent", "analysis.md");
                var assessmentPath = Path.Combine(req.WorkspacePath, ".agent", "analysis-assessment.json");
                File.Exists(analysisPath).Should().BeFalse("stale analysis.md should be deleted before retry");
                File.Exists(assessmentPath).Should().BeFalse("stale assessment.json should be deleted before retry");

                WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                WriteAssessmentFile(req.WorkspacePath, "ready");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        attempt.Should().Be(2);
    }

    [Fact]
    public async Task Analysis_CommentNotPostedWhenContentEmpty()
    {
        // Agent never writes files → pipeline fails → no analysis comment posted
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default() with { MaxAnalysisRetries = 0 });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        // Analysis comment should NOT have been posted (no content)
        _mockIssueProvider.Verify(
            p => p.PostCommentAsync("42", It.Is<string>(s => s.Contains("Agent Analysis")), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Analysis_ChatHistoryUpdatedOnRetry()
    {
        var attempt = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                attempt++;
                if (attempt >= 2)
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                }
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.ChatHistory.Where(c => c.Role == ChatRole.System)
            .Should().Contain(c => c.Content.Contains("Analysis attempt 1 failed") && c.Content.Contains("Retrying"));
    }

    [Fact]
    public async Task Analysis_AgentExceptionTriggersRetry()
    {
        var attempt = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                attempt++;
                if (attempt == 1)
                    throw new InvalidOperationException("Agent crashed");
                WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                WriteAssessmentFile(req.WorkspacePath, "ready");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        attempt.Should().Be(2);
    }

    [Fact]
    public async Task Analysis_MaxAnalysisRetriesZero_FailsOnFirstFailure()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default() with { MaxAnalysisRetries = 0 });

        // Agent doesn't write analysis files
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Analyze the codebase") && r.UseResume),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("Analysis failed after 1 attempt(s)");
    }

    [Fact]
    public async Task StartPipeline_WhenBranchCreationFails_TransitionsToFailed()
    {
        // Track all state transitions
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        // Override CreateBranchAsync to throw after clone succeeds
        _mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ref already exists"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Verify full transition sequence: Created → CloningRepository → CreatingBranch → Failed
        transitions.Should().ContainInOrder(
            PipelineStep.CloningRepository,
            PipelineStep.CreatingBranch,
            PipelineStep.Failed);

        // Verify terminal state and metadata
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("Branch creation failed");
        run.CompletedAt.Should().NotBeNull();

        // Verify agent:in-progress label was set during CloningRepository (before failure)
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:in-progress", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        // Verify agent:error label was set after failure
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Verify run added to history with Failed status
        _service.GetRunHistory().Should().HaveCount(1);
        _service.GetRunHistory()[0].FinalStep.Should().Be(PipelineStep.Failed);
    }

    [Fact]
    public async Task StartPipeline_WhenNoCommitsAhead_FailsWithNoChangesMessage()
    {
        // Track all state transitions
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        // Override HasCommitsAheadAsync to return false (no changes produced)
        _mockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Verify pipeline reaches CreatingPullRequest then transitions to Failed
        transitions.Should().ContainInOrder(
            PipelineStep.CreatingPullRequest,
            PipelineStep.Failed);

        // Verify terminal state and metadata
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("did not produce any changes");
        run.PullRequestUrl.Should().BeNull();
        run.CompletedAt.Should().NotBeNull();

        // Verify agent:error label was set
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartPipeline_WithBrainProvider_IncludesBrainSyncSteps()
    {
        // REQ-3: Happy path with brain sync — no overrides needed (baseline)
        var ctx = CreateBrainTestService();

        // Track all state transitions
        var transitions = new List<PipelineStep>();
        ctx.Service.OnChange += () =>
        {
            if (ctx.Service.ActiveRun != null)
                transitions.Add(ctx.Service.ActiveRun.CurrentStep);
        };

        var run = await ctx.Service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None, brainProviderId: "brain-1");

        // Assert: transition sequence includes brain sync steps in correct order
        transitions.Should().ContainInOrder(
            PipelineStep.CloningRepository,
            PipelineStep.SyncingBrainRepoPreRun,
            PipelineStep.CreatingBranch);

        transitions.Should().ContainInOrder(
            PipelineStep.CreatingPullRequest,
            PipelineStep.ReflectingOnRun,
            PipelineStep.SyncingBrainRepoPostRun,
            PipelineStep.Completed);

        run.BrainContextLoaded.Should().BeTrue();
        run.CurrentStep.Should().Be(PipelineStep.Completed);

        ctx.MockBrainProvider.Verify(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        ctx.MockAgentProvider.Verify(p => p.ExecuteAsync(
            It.Is<AgentRequest>(r => r.Prompt.Contains("Reflect on This Run") && r.UseResume),
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);

        ctx.MockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.Never);
        foreach (var label in AgentLabels.All)
            ctx.MockIssueProvider.Verify(p => p.RemoveLabelAsync("42", label, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartPipeline_WhenBrainPreRunSyncFails_ContinuesWithoutBrain()
    {
        // REQ-4: Brain pre-run sync failure — override brain provider CloneAsync to throw
        var ctx = CreateBrainTestService();
        ctx.MockBrainProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Brain repo unavailable"));

        var transitions = new List<PipelineStep>();
        ctx.Service.OnChange += () =>
        {
            if (ctx.Service.ActiveRun != null)
                transitions.Add(ctx.Service.ActiveRun.CurrentStep);
        };

        var run = await ctx.Service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None, brainProviderId: "brain-1");

        transitions.Should().ContainInOrder(
            PipelineStep.SyncingBrainRepoPreRun,
            PipelineStep.CreatingBranch);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.BrainContextLoaded.Should().BeFalse();
        transitions.Should().NotContain(PipelineStep.Failed);
        ctx.MockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WhenAgentTimesOut_TransitionsToFailed()
    {
        // Track all state transitions
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        // Override agent: analysis prompt succeeds (writes files, returns 0),
        // implementation prompt returns ExitCode timeout
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                // Implementation prompt: return ExitCode timeout
                return Task.FromResult(new AgentResult { ExitCode = ExitCodes.Timeout, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Verify transition sequence ends at Failed after GeneratingCode
        transitions.Should().ContainInOrder(
            PipelineStep.GeneratingCode,
            PipelineStep.Failed);

        // Verify terminal state and metadata
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("timed out");
        run.CompletedAt.Should().NotBeNull();

        // Verify pipeline did NOT reach RunningQualityGates
        transitions.Should().NotContain(PipelineStep.RunningQualityGates);

        // Verify agent:error label was set
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartPipeline_WhenAgentExitsNonZero_ContinuesToQualityGates()
    {
        // Track all state transitions
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        // Override agent: analysis prompt succeeds (writes files, returns 0),
        // implementation prompt returns ExitCode 1 (non-zero but not timeout)
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                // Implementation prompt: return ExitCode 1 (non-zero, not timeout)
                return Task.FromResult(new AgentResult { ExitCode = ExitCodes.GeneralFailure, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Verify pipeline continued past GeneratingCode to RunningQualityGates and Completed
        transitions.Should().ContainInOrder(
            PipelineStep.GeneratingCode,
            PipelineStep.RunningQualityGates,
            PipelineStep.Completed);

        // Verify terminal state: quality gates pass, pipeline completes
        run.CurrentStep.Should().Be(PipelineStep.Completed);

        // Verify transitions contain RunningQualityGates (pipeline continued past non-zero exit)
        transitions.Should().Contain(PipelineStep.RunningQualityGates);

        // Verify ChatHistory contains system message about non-zero exit code
        run.ChatHistory.Where(c => c.Role == ChatRole.System).Select(c => c.Content)
            .Should().Contain(msg => msg.Contains("exited with code 1"));

        // Verify no agent:error label set
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WhenReflectionFails_StillCompletes()
    {
        // REQ-8: Reflection step failure — override agent to throw on reflection prompt
        var ctx = CreateBrainTestService();
        ctx.MockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                if (req.Prompt.Contains("Reflect on This Run"))
                    throw new InvalidOperationException("Reflection crashed");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var transitions = new List<PipelineStep>();
        ctx.Service.OnChange += () =>
        {
            if (ctx.Service.ActiveRun != null)
                transitions.Add(ctx.Service.ActiveRun.CurrentStep);
        };

        var run = await ctx.Service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None, brainProviderId: "brain-1");

        transitions.Should().ContainInOrder(
            PipelineStep.ReflectingOnRun,
            PipelineStep.SyncingBrainRepoPostRun,
            PipelineStep.Completed);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        ctx.MockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WhenBrainPostRunSyncFails_StillCompletes()
    {
        // REQ-9: Brain post-run sync failure — override CommitAndPushAsync to throw
        var ctx = CreateBrainTestService();
        ctx.MockBrainUpdateService.Setup(b => b.CommitAndPushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Push rejected"));

        var transitions = new List<PipelineStep>();
        ctx.Service.OnChange += () =>
        {
            if (ctx.Service.ActiveRun != null)
                transitions.Add(ctx.Service.ActiveRun.CurrentStep);
        };

        var run = await ctx.Service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None, brainProviderId: "brain-1");

        transitions.Should().ContainInOrder(
            PipelineStep.SyncingBrainRepoPostRun,
            PipelineStep.Completed);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.BrainUpdatesPushed.Should().BeFalse();
        ctx.MockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WhenQualityGateValidatorThrows_TransitionsToFailedWithReason()
    {
        // Track all state transitions
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        // Override validator to throw IOException("Disk full")
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Disk full"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Verify transition sequence: ... → RunningQualityGates → Failed
        transitions.Should().ContainInOrder(
            PipelineStep.RunningQualityGates,
            PipelineStep.Failed);

        // Verify terminal state and metadata
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("Quality gate validation error");
        run.FailureReason.Should().Contain("Disk full");

        // Note: CompletedAt is not set by QualityGateExecutor's exception handler
        // (unlike FailRunAsync which sets it). This is the actual production behavior.
        // NOTE: Consider fixing production code to set CompletedAt on all terminal states.

        // Verify agent:error label was set
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Verify run added to history with Failed status
        _service.GetRunHistory().Should().HaveCount(1);
        _service.GetRunHistory()[0].FinalStep.Should().Be(PipelineStep.Failed);
    }

    [Fact]
    public async Task StartPipeline_WhenExternalCiFails_CreatesDraftPr()
    {
        // Track all state transitions
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        // Configure MaxRetries = 0 (pipeline provider will be auto-resolved)
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default() with { MaxRetries = 0 });

        // Add pipeline provider config to config store
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "pipeline-1", Kind = ProviderKind.Pipeline, ProviderType = "GitHubActions", DisplayName = "CI" }
            });

        // Create mock pipeline provider returning failed CI status
        var mockPipelineProvider = new Mock<IPipelineProvider>();
        mockPipelineProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Failed,
                Jobs = Array.Empty<PipelineJobResult>()
            });
        _mockFactory.Setup(f => f.CreatePipelineProvider(It.IsAny<ProviderConfig>())).Returns(mockPipelineProvider.Object);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Verify transition sequence: ... → RunningQualityGates → CreatingPullRequest → Failed
        transitions.Should().ContainInOrder(
            PipelineStep.RunningQualityGates,
            PipelineStep.CreatingPullRequest,
            PipelineStep.Failed);

        // Verify draft PR was created
        run.IsDraftPr.Should().BeTrue();
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.PullRequestUrl.Should().NotBeNull();

        // Verify agent:error label was set
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CancelPipeline_DuringQualityGates_TransitionsToCancelled()
    {
        // Track all state transitions
        var transitions = new List<PipelineStep>();
        var reachedQualityGates = new TaskCompletionSource();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
            {
                transitions.Add(_service.ActiveRun.CurrentStep);
                if (_service.ActiveRun.CurrentStep == PipelineStep.RunningQualityGates)
                    reachedQualityGates.TrySetResult();
            }
        };

        // Block the validator with a TaskCompletionSource so the pipeline stays in RunningQualityGates
        var validatorTcs = new TaskCompletionSource<QualityGateReport>();
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(validatorTcs.Task);

        // Start the pipeline (analysis writes files and succeeds, code gen succeeds, then blocks at quality gates)
        var pipelineTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Wait for pipeline to reach RunningQualityGates using event-based synchronization (not Task.Delay)
        await reachedQualityGates.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel the pipeline while quality gates are running
        await _service.CancelPipelineAsync();

        // Complete the validator TCS with TrySetCanceled() — NOT SetResult — to propagate cancellation
        validatorTcs.TrySetCanceled();

        // Await the pipeline task (may throw due to cancellation)
        try { await pipelineTask; } catch { }

        var run = _service.ActiveRun!;

        // Verify transition sequence: ... → RunningQualityGates → Cancelled
        transitions.Should().ContainInOrder(
            PipelineStep.RunningQualityGates,
            PipelineStep.Cancelled);

        // Verify terminal state
        run.CurrentStep.Should().Be(PipelineStep.Cancelled);

        // Verify labels cleaned up (agent:cancelled set, other labels removed)
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:cancelled", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _mockIssueProvider.Verify(p => p.RemoveLabelAsync("42", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Verify run added to history
        _service.GetRunHistory().Should().HaveCount(1);
        _service.GetRunHistory()[0].FinalStep.Should().Be(PipelineStep.Cancelled);
    }

    // --- REQ-12: HighWaterMark monotonicity during quality gate retries ---

    [Fact]
    public async Task QualityGateRetry_HighWaterMark_DoesNotRegress()
    {
        // Quality gates fail on first call, pass on second
        var callCount = 0;
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var idx = Interlocked.Increment(ref callCount);
                return idx == 1
                    ? new QualityGateReport
                    {
                        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build failed" },
                        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
                    }
                    : new QualityGateReport
                    {
                        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
                    };
            });

        // Track HighWaterMark and CurrentStep at every OnChange event
        var highWaterMarks = new List<PipelineStep>();
        var transitions = new List<(PipelineStep Current, PipelineStep HWM)>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
            {
                highWaterMarks.Add(_service.ActiveRun.HighWaterMark);
                transitions.Add((_service.ActiveRun.CurrentStep, _service.ActiveRun.HighWaterMark));
            }
        };

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // 1. HighWaterMark values form a monotonically non-decreasing sequence
        for (var i = 1; i < highWaterMarks.Count; i++)
            ((int)highWaterMarks[i]).Should().BeGreaterThanOrEqualTo((int)highWaterMarks[i - 1],
                $"HighWaterMark at index {i} ({highWaterMarks[i]}) should not regress below index {i - 1} ({highWaterMarks[i - 1]})");

        // 2. After stepping back to GeneratingCode, HighWaterMark stays at RunningQualityGates (or higher)
        var retryEntries = transitions.Where(t => t.Current == PipelineStep.GeneratingCode
            && (int)t.HWM >= (int)PipelineStep.RunningQualityGates).ToList();
        retryEntries.Should().NotBeEmpty("pipeline should step back to GeneratingCode with HighWaterMark at RunningQualityGates during retry");
        foreach (var entry in retryEntries)
            ((int)entry.HWM).Should().BeGreaterThanOrEqualTo((int)PipelineStep.RunningQualityGates,
                "HighWaterMark should stay at RunningQualityGates (or higher) when stepping back to GeneratingCode");

        // 3. After CreatingPullRequest, HighWaterMark advances to CreatingPullRequest (or higher)
        var prEntries = transitions.Where(t => t.Current == PipelineStep.CreatingPullRequest).ToList();
        prEntries.Should().NotBeEmpty("pipeline should reach CreatingPullRequest");
        foreach (var entry in prEntries)
            ((int)entry.HWM).Should().BeGreaterThanOrEqualTo((int)PipelineStep.CreatingPullRequest,
                "HighWaterMark should advance to CreatingPullRequest after reaching that step");

        // 4. Final state should be Completed (quality gates passed on retry)
        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.HighWaterMark.Should().Be(PipelineStep.Completed);
    }

    // --- REQ-13: Agent CancellationToken timeout during code generation ---

    [Fact]
    public async Task StartPipeline_WhenAgentCancellationTokenTimesOut_TransitionsToFailed()
    {
        // Track all state transitions
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        // Override agent: analysis prompt succeeds (writes files, returns 0),
        // implementation prompt throws OperationCanceledException (agent-level timeout)
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                // Implementation prompt: throw OperationCanceledException (agent-level timeout, NOT orchestrator cancellation)
                throw new OperationCanceledException("The operation was canceled.");
            });

        // Pass CancellationToken.None — orchestrator CTS is NOT cancelled (simulates agent-level timeout)
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Verify transition sequence ends at Failed after GeneratingCode
        transitions.Should().ContainInOrder(
            PipelineStep.GeneratingCode,
            PipelineStep.Failed);

        // Verify terminal state and metadata
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("timed out");
        run.CompletedAt.Should().NotBeNull();

        // Verify pipeline did NOT reach RunningQualityGates
        transitions.Should().NotContain(PipelineStep.RunningQualityGates);

        // Verify agent:error label was set
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartPipeline_WhenCodeGenThrowsGenericException_ContinuesToQualityGates()
    {
        // Track all state transitions
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        // Override agent: analysis prompt succeeds (writes files, returns 0),
        // implementation prompt throws InvalidOperationException (generic exception, not timeout/cancellation)
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    WriteAnalysisFile(req.WorkspacePath, new string('x', 200));
                    WriteAssessmentFile(req.WorkspacePath, "ready");
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                // Implementation prompt: throw InvalidOperationException (generic exception)
                throw new InvalidOperationException("Agent process crashed");
            });

        // Simulate partial file changes so the pipeline continues to quality gates
        _mockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new FileChangeSummary("Modified", "src/file.cs", 10, 2) } as IReadOnlyList<FileChangeSummary>);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Verify pipeline continued past GeneratingCode to RunningQualityGates and Completed
        transitions.Should().ContainInOrder(
            PipelineStep.GeneratingCode,
            PipelineStep.RunningQualityGates,
            PipelineStep.Completed);

        // Verify terminal state: quality gates pass, pipeline completes
        run.CurrentStep.Should().Be(PipelineStep.Completed);

        // Verify transitions contain RunningQualityGates (pipeline continued past generic exception)
        transitions.Should().Contain(PipelineStep.RunningQualityGates);

        // Verify ChatHistory contains system message about agent failure
        run.ChatHistory.Where(c => c.Role == ChatRole.System).Select(c => c.Content)
            .Should().Contain(msg => msg.Contains("Agent process failed"));

        // Verify no agent:error label set
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_WhenPrCreationThrows_TransitionsToFailed()
    {
        // Track all state transitions
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        // Override PushBranchAsync to throw (called inside PullRequestOrchestrator.CreatePullRequestAsync)
        _mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Remote rejected push"));
        _mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Remote rejected push"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Verify transition sequence: ... → CreatingPullRequest → Failed
        transitions.Should().ContainInOrder(
            PipelineStep.CreatingPullRequest,
            PipelineStep.Failed);

        // Verify terminal state and metadata
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("PR creation failed");
        run.CompletedAt.Should().NotBeNull();

        // Verify agent:error label was set
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // --- PreparingForPullRequest (cleanup step) tests ---

    [Fact]
    public async Task StartPipeline_HappyPath_TransitionsThroughPreparingForPullRequest()
    {
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        transitions.Should().ContainInOrder(
            PipelineStep.RunningQualityGates,
            PipelineStep.PreparingForPullRequest,
            PipelineStep.RunningQualityGates,
            PipelineStep.CreatingPullRequest);
    }

    [Fact]
    public async Task StartPipeline_CleanupAgent_ReceivesCleanupPrompt()
    {
        var capturedPrompts = new List<string>();
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => !r.Prompt.Contains("Analyze the codebase")),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedPrompts.Add(req.Prompt);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        capturedPrompts.Should().Contain(p => p.Contains("Pre-Pull Request Cleanup"));
        var cleanupPrompt = capturedPrompts.First(p => p.Contains("Pre-Pull Request Cleanup"));
        cleanupPrompt.Should().Contain("Do NOT make functional changes");
        cleanupPrompt.Should().Contain("Do NOT run git write commands");
    }

    [Fact]
    public async Task StartPipeline_FinalQualityGateFail_ReEntersRetryLoop()
    {
        var callCount = 0;
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                // First call (initial QG): pass. Second call (final QG after cleanup): fail. Third call (retry): pass.
                var testsPassed = callCount != 2;
                return new QualityGateReport
                {
                    Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                    Tests = new GateResult { GateName = "Tests", Passed = testsPassed, Details = testsPassed ? "OK" : "1 test failed" }
                };
            });

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), MaxRetries = 3 });

        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.RetryCount.Should().Be(1);
        // Should have gone: QG(pass) → PreparingForPullRequest → QG(fail) → GeneratingCode → QG(pass) → CreatingPullRequest
        transitions.Should().ContainInOrder(
            PipelineStep.PreparingForPullRequest,
            PipelineStep.RunningQualityGates,
            PipelineStep.GeneratingCode,
            PipelineStep.RunningQualityGates,
            PipelineStep.CreatingPullRequest);
    }

    [Fact]
    public async Task StartPipeline_FinalQualityGateFail_RetriesExhausted_CreatesDraftPr()
    {
        var callCount = 0;
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                // First call: pass (initial QG). All subsequent calls: fail.
                var testsPassed = callCount == 1;
                return new QualityGateReport
                {
                    Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                    Tests = new GateResult { GateName = "Tests", Passed = testsPassed, Details = testsPassed ? "OK" : "1 test failed" }
                };
            });

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), MaxRetries = 1 });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.IsDraftPr.Should().BeTrue();
        run.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task StartPipeline_CleanupAgentFails_ContinuesToFinalQualityGates()
    {
        // Make the cleanup agent throw — pipeline should still proceed to final QG
        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => !r.Prompt.Contains("Analyze the codebase")),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                callCount++;
                if (req.Prompt.Contains("Pre-Pull Request Cleanup"))
                    throw new InvalidOperationException("Agent crashed during cleanup");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Pipeline should complete despite cleanup agent failure
        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.PullRequestUrl.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task StartPipeline_FinalQualityGateRun_EmitsDistinctLogMessage()
    {
        var outputLines = new List<string>();
        _service.OnOutputLine += line => outputLines.Add(line);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        outputLines.Should().Contain(l => l.Contains("Preparing for pull request"));
        outputLines.Should().Contain(l => l.Contains("Running final quality gates after cleanup"));
    }

    [Fact]
    public async Task StartPipeline_NoChangesAfterCleanup_WithExternalCi_SkipsCiAndCompletes()
    {
        // Configure pipeline (provider will be auto-resolved)
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());

        // Add pipeline provider config
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "pipeline-1", Kind = ProviderKind.Pipeline, ProviderType = "GitHubActions", DisplayName = "CI" }
            });

        // Create mock pipeline provider — CI passes on initial QG run
        var mockPipelineProvider = new Mock<IPipelineProvider>();
        mockPipelineProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Passed,
                Jobs = new[] { new PipelineJobResult { Name = "build", State = PipelineRunState.Passed } }
            });
        _mockFactory.Setup(f => f.CreatePipelineProvider(It.IsAny<ProviderConfig>())).Returns(mockPipelineProvider.Object);

        // CommitAllAsync (4-param with blacklist): succeeds on first call (initial QG),
        // throws "No changes to commit" on second call (final QG after cleanup — no changes)
        var commitCallCount = 0;
        _mockRepoProvider.Setup(p => p.CommitAllAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, IReadOnlyList<string>?, CancellationToken>((_, _, _, _) =>
            {
                commitCallCount++;
                if (commitCallCount > 1)
                    throw new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace.");
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            });

        _mockRepoProvider.Setup(p => p.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");

        var outputLines = new List<string>();
        _service.OnOutputLine += line => outputLines.Add(line);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Pipeline should complete successfully — not fail with draft PR
        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.IsDraftPr.Should().BeFalse();

        // External CI should have been called once (initial QG) but NOT for the final QG after cleanup
        mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);

        // The 5-param CommitAllAsync (allowEmpty) should never be called — we skip CI instead
        _mockRepoProvider.Verify(p => p.CommitAllAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        // Output should contain the skip message
        outputLines.Should().Contain(l => l.Contains("skipped"));
    }

    // --- Rework mode tests ---

    /// <summary>
    /// Sets up mocks for rework mode: linked PR with review comments, checkout, merge, and update PR.
    /// </summary>
    private void SetupReworkMocks()
    {
        _mockRepoProvider.Setup(p => p.GetAgentPullRequestsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LinkedPullRequest>
            {
                new()
                {
                    Number = 42,
                    BranchName = "feature/auto-7-fix-bug-abc12345",
                    Url = "https://github.com/test/repo/pull/42",
                    IsDraft = false,
                    IsMergeable = true,
                    ReviewComments = new List<PullRequestReviewComment>
                    {
                        new()
                        {
                            Id = "1", Body = "Fix the null check", Author = "reviewer",
                            CreatedAt = DateTime.UtcNow, Path = "src/Service.cs"
                        }
                    }
                }
            });

        _mockRepoProvider.Setup(p => p.CheckoutRemoteBranchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockRepoProvider.Setup(p => p.MergeFromBaseAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult
            {
                Success = true, HasConflicts = false, ConflictFiles = Array.Empty<string>()
            });

        _mockRepoProvider.Setup(p => p.UpdatePullRequestAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task StartPipeline_WhenAgentPrExists_EntersReworkMode()
    {
        SetupReworkMocks();

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.LinkedPullRequest.Should().NotBeNull();
        run.LinkedPullRequest!.Number.Should().Be(42);
        run.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_CallsCheckoutInsteadOfCreateBranch()
    {
        SetupReworkMocks();

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        _mockRepoProvider.Verify(p => p.CheckoutRemoteBranchAsync(
            It.IsAny<string>(), It.Is<string>(b => b == "feature/auto-7-fix-bug-abc12345"), It.IsAny<CancellationToken>()), Times.Once);
        _mockRepoProvider.Verify(p => p.CreateBranchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_CallsMergeFromBaseAfterCheckout()
    {
        SetupReworkMocks();

        var callOrder = new List<string>();
        _mockRepoProvider.Setup(p => p.CheckoutRemoteBranchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("Checkout"))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.MergeFromBaseAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("Merge"))
            .ReturnsAsync(new MergeResult { Success = true, HasConflicts = false, ConflictFiles = Array.Empty<string>() });

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        callOrder.Should().ContainInOrder("Checkout", "Merge");
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_UsesPushNotCreatePr()
    {
        SetupReworkMocks();

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        _mockRepoProvider.Verify(p => p.PushBranchAsync(
            It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
        _mockRepoProvider.Verify(p => p.CreatePullRequestAsync(
            It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_CallsUpdatePullRequestAsync()
    {
        SetupReworkMocks();

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        _mockRepoProvider.Verify(p => p.UpdatePullRequestAsync(
            42, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_WithConflicts_PromptIncludesConflictFiles()
    {
        SetupReworkMocks();

        _mockRepoProvider.Setup(p => p.MergeFromBaseAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult
            {
                Success = false,
                HasConflicts = true,
                ConflictFiles = new List<string> { "src/File1.cs", "src/File2.cs" }
            });

        var capturedPrompts = new List<string>();
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => !r.Prompt.Contains("Analyze the codebase")),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedPrompts.Add(req.Prompt);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        var reworkPrompt = capturedPrompts.First(p => !p.Contains("Pre-Pull Request Cleanup"));
        reworkPrompt.Should().Contain("src/File1.cs");
        reworkPrompt.Should().Contain("src/File2.cs");
        reworkPrompt.Should().Contain("Merge Conflicts");
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_WithReviewComments_PromptIncludesFeedback()
    {
        SetupReworkMocks();

        var capturedPrompts = new List<string>();
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => !r.Prompt.Contains("Analyze the codebase")),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedPrompts.Add(req.Prompt);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        var reworkPrompt = capturedPrompts.First(p => !p.Contains("Pre-Pull Request Cleanup"));
        reworkPrompt.Should().Contain("reviewer");
        reworkPrompt.Should().Contain("Fix the null check");
        reworkPrompt.Should().Contain("src/Service.cs");
        reworkPrompt.Should().Contain("Review Feedback");
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_WhenCheckoutThrows_TransitionsToFailed()
    {
        SetupReworkMocks();

        _mockRepoProvider.Setup(p => p.CheckoutRemoteBranchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Branch not found"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("checkout");
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartPipeline_WhenGetAgentPrThrows_FallsBackToNewIssueFlow()
    {
        _mockRepoProvider.Setup(p => p.GetAgentPullRequestsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("API error"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.LinkedPullRequest.Should().BeNull();
        _mockRepoProvider.Verify(p => p.CreateBranchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_WhenPushFailsNonFastForward_TransitionsToFailed()
    {
        SetupReworkMocks();

        _mockRepoProvider.Setup(p => p.PushBranchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("non-fast-forward"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_WhenMergeThrows_TransitionsToFailed()
    {
        SetupReworkMocks();

        _mockRepoProvider.Setup(p => p.MergeFromBaseAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Base branch not found"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Contain("rebase");
        _mockIssueProvider.Verify(p => p.AddLabelAsync("42", "agent:error", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_WhenUpdatePrThrows_ContinuesSuccessfully()
    {
        SetupReworkMocks();

        _mockRepoProvider.Setup(p => p.UpdatePullRequestAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PR not found"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Pipeline should complete successfully despite UpdatePullRequestAsync failure
        run.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_DraftPrWithNoFeedback_EntersCodeGeneration()
    {
        SetupReworkMocks();

        // Override: draft PR with no review comments
        _mockRepoProvider.Setup(p => p.GetAgentPullRequestsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LinkedPullRequest>
            {
                new()
                {
                    Number = 42,
                    BranchName = "feature/auto-7-fix-bug-abc12345",
                    Url = "https://github.com/test/repo/pull/42",
                    IsDraft = true,
                    IsMergeable = true,
                    ReviewComments = Array.Empty<PullRequestReviewComment>()
                }
            });

        var capturedPrompts = new List<string>();
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => !r.Prompt.Contains("Analyze the codebase")),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedPrompts.Add(req.Prompt);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        var reworkPrompt = capturedPrompts.First(p => !p.Contains("Pre-Pull Request Cleanup"));
        reworkPrompt.Should().Contain("Draft PR");
        reworkPrompt.Should().Contain("previous failed run");
    }

    [Fact]
    public async Task StartPipeline_InReworkMode_NoConflictsNoCommentsNotDraft_SkipsCodeGen()
    {
        SetupReworkMocks();

        // Override: non-draft PR with no review comments and no conflicts
        _mockRepoProvider.Setup(p => p.GetAgentPullRequestsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LinkedPullRequest>
            {
                new()
                {
                    Number = 42,
                    BranchName = "feature/auto-7-fix-bug-abc12345",
                    Url = "https://github.com/test/repo/pull/42",
                    IsDraft = false,
                    IsMergeable = true,
                    ReviewComments = Array.Empty<PullRequestReviewComment>()
                }
            });

        var capturedPrompts = new List<string>();
        _mockAgentProvider.Setup(p => p.ExecuteAsync(
                It.Is<AgentRequest>(r => !r.Prompt.Contains("Analyze the codebase")),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedPrompts.Add(req.Prompt);
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Code generation agent should NOT be called — only the cleanup agent and feedback collection
        var nonFeedbackPrompts = capturedPrompts.Where(p => !p.Contains("Pipeline Success Feedback")).ToList();
        nonFeedbackPrompts.Should().AllSatisfy(p => p.Should().Contain("Pre-Pull Request Cleanup"));
    }

    // ── Integration tests: OrchestratorCallbacks routing to lifecycle service ──

    [Fact]
    public async Task OrchestratorCallbacks_TransitionTo_RoutesToLifecycleService()
    {
        // TransitionTo is routed through lifecycle — verify by observing OnChange fires
        var transitions = new List<PipelineStep>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                transitions.Add(_service.ActiveRun.CurrentStep);
        };

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // TransitionTo was called multiple times during pipeline execution
        transitions.Should().NotBeEmpty();
        transitions.Should().Contain(PipelineStep.CloningRepository);
    }

    [Fact]
    public async Task OrchestratorCallbacks_EmitOutputLine_RoutesToLifecycleService()
    {
        var outputLines = new List<string>();
        _service.OnOutputLine += line => outputLines.Add(line);

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // EmitOutputLine is called during pipeline execution
        outputLines.Should().NotBeEmpty();
    }

    [Fact]
    public async Task OrchestratorCallbacks_NotifyChange_RoutesToLifecycleService()
    {
        var changeCount = 0;
        _service.OnChange += () => changeCount++;

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // NotifyChange fires on every TransitionTo call
        changeCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task OrchestratorCallbacks_AddRunToHistory_RoutesToLifecycleService()
    {
        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // AddRunToHistory is called when pipeline completes
        _service.GetRunHistory().Should().HaveCount(1);
        _service.GetRunHistory()[0].FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task CancelActiveAgentRunsAsync_CallsMarkAgentRunsCancelled_ThenPerformsLabelSwaps()
    {
        // Set up a service with a run service that has active agent runs
        var mockRunService = new Mock<IOrchestratorRunService>();
        var agentRun = new PipelineRun
        {
            RunId = "agent-run-1",
            IssueIdentifier = "99",
            IssueTitle = "Agent Issue",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1",
            CurrentStep = PipelineStep.GeneratingCode,
            HighWaterMark = PipelineStep.GeneratingCode,
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-worker-1"
        };
        mockRunService.Setup(r => r.GetActiveRuns()).Returns(new List<PipelineRun> { agentRun }.AsReadOnly());
        mockRunService.Setup(r => r.HasActiveRuns).Returns(true);

        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        var lifecycle = new PipelineRunLifecycleService(
            mockHistoryService.Object, mockRunService.Object, _mockLogger.Object);

        var service = new PipelineOrchestrationService(
            _mockConfigStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            new AgentPhaseExecutor(_mockLogger.Object),
            new QualityGateExecutor(_mockValidator.Object, new PullRequestOrchestrator(_mockLogger.Object), _mockLogger.Object),
            _mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistoryService.Object,
            runService: mockRunService.Object,
            lifecycle: lifecycle,
            labelSwapper: new Orchestration.LabelSwapper(_mockConfigStore.Object, _mockFactory.Object, _mockLogger.Object));

        await service.CancelActiveAgentRunsAsync();

        // Verify state changes happened via lifecycle
        agentRun.CurrentStep.Should().Be(PipelineStep.Cancelled);
        agentRun.CompletedAt.Should().NotBeNull();
        mockHistoryService.Verify(h => h.AddRunToHistory(agentRun), Times.Once);

        // Verify label swap was attempted (RemoveLabelAsync + AddLabelAsync)
        _mockIssueProvider.Verify(
            p => p.RemoveLabelAsync("99", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _mockIssueProvider.Verify(
            p => p.AddLabelAsync("99", "agent:cancelled", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    public void Dispose()
    {
        _service.Dispose();
    }
}