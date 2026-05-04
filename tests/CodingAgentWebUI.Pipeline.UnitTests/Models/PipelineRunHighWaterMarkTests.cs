using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.UnitTests.Helpers;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Tests for PipelineRun.HighWaterMark tracking through TransitionTo.
/// </summary>
public class PipelineRunHighWaterMarkTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IAgentProvider> _mockAgentProvider;
    private readonly Mock<IQualityGateValidator> _mockValidator;
    private readonly PipelineOrchestrationService _service;

    public PipelineRunHighWaterMarkTests()
    {
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockAgentProvider = new Mock<IAgentProvider>();
        _mockValidator = new Mock<IQualityGateValidator>();
        var mockLogger = new Mock<Serilog.ILogger>();

        SetupDefaultMocks();

        _service = new PipelineOrchestrationService(
            _mockConfigStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            new AgentExecutionOrchestrator(mockLogger.Object),
            new QualityGateOrchestrator(_mockValidator.Object, new PullRequestOrchestrator(mockLogger.Object), mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: new Mock<IPipelineRunHistoryService>().Object);
    }

    [Fact]
    public void HighWaterMark_DefaultsToCreated()
    {
        var run = new PipelineRun
        {
            RunId = "r1", IssueIdentifier = "1", IssueTitle = "Test",
            IssueProviderConfigId = "ip", RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        run.HighWaterMark.Should().Be(PipelineStep.Created);
    }

    [Fact]
    public async Task HighWaterMark_AdvancesForwardDuringPipeline()
    {
        SetupAllGatesPass();
        var highWaterMarks = new List<PipelineStep>();

        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                highWaterMarks.Add(_service.ActiveRun.HighWaterMark);
        };

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // HighWaterMark should have advanced monotonically
        for (var i = 1; i < highWaterMarks.Count; i++)
            ((int)highWaterMarks[i]).Should().BeGreaterThanOrEqualTo((int)highWaterMarks[i - 1]);

        // Final high-water mark should be Completed
        run.HighWaterMark.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task HighWaterMark_DoesNotRegressDuringRetry()
    {
        // QG fails → retry → QG fails again → draft PR
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { Workspace = new WorkspaceConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() }, Retry = new RetryConfiguration { MaxRetries = 1 } });

        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = false, Details = "Failed" }
            });

        var hwmLog = new List<(PipelineStep Current, PipelineStep HWM)>();
        _service.OnChange += () =>
        {
            if (_service.ActiveRun != null)
                hwmLog.Add((_service.ActiveRun.CurrentStep, _service.ActiveRun.HighWaterMark));
        };

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // After reaching RunningQualityGates, the HWM should never drop below it
        var reachedQg = false;
        foreach (var entry in hwmLog)
        {
            if ((int)entry.HWM >= (int)PipelineStep.RunningQualityGates)
                reachedQg = true;
            if (reachedQg)
                ((int)entry.HWM).Should().BeGreaterThanOrEqualTo((int)PipelineStep.RunningQualityGates);
        }
        reachedQg.Should().BeTrue("pipeline should have reached RunningQualityGates");
    }

    [Fact]
    public async Task HighWaterMark_NotAdvancedByFailedTerminalState()
    {
        // Force a failure during cloning
        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Clone failed"));

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.HighWaterMark.Should().NotBe(PipelineStep.Failed);
        run.HighWaterMark.Should().NotBe(PipelineStep.Cancelled);
    }

    [Fact]
    public async Task HighWaterMark_NotAdvancedByCancelledTerminalState()
    {
        // Set up a slow agent so we can cancel
        var agentTcs = new TaskCompletionSource<AgentResult>();
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns((AgentRequest _, CancellationToken ct, Action<string>? _) =>
            {
                ct.Register(() => agentTcs.TrySetCanceled(ct));
                return agentTcs.Task;
            });

        var runTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Wait for the pipeline to reach the agent step
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_service.IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await _service.CancelPipelineAsync();

        var run = await runTask;

        run.CurrentStep.Should().Be(PipelineStep.Cancelled);
        run.HighWaterMark.Should().NotBe(PipelineStep.Cancelled);
        run.HighWaterMark.Should().NotBe(PipelineStep.Failed);
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

        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        _mockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);

        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    var dir = Path.Combine(req.WorkspacePath, ".kiro");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "analysis.md"), new string('x', 200));
                    var assessment = new { recommendation = "ready", reason = "Test reason", concerns = Array.Empty<string>(), blockingIssues = Array.Empty<string>() };
                    File.WriteAllText(Path.Combine(dir, "analysis-assessment.json"),
                        System.Text.Json.JsonSerializer.Serialize(assessment, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
                }
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });
        _mockAgentProvider.Setup(p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(_mockIssueProvider.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(_mockRepoProvider.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(_mockAgentProvider.Object);
    }

    private void SetupAllGatesPass()
    {
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
            });
    }
}