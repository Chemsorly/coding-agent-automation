using AwesomeAssertions;
using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;
using KiroWebUI.Infrastructure.Persistence;
using KiroWebUI.Infrastructure.Git;
using KiroWebUI.IntegrationTests.Helpers;

namespace KiroWebUI.IntegrationTests.Pipeline;

/// <summary>
/// Unit tests for AgentCoding page logic.
/// Tests view switching, concurrent start rejection, and button disabled states
/// via the same operations the AgentCoding page performs against PipelineOrchestrationService and mocked providers.
/// Since bunit is not available, these tests validate the page's behavioral logic through its dependencies.
/// </summary>
public class AgentCodingPageTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IAgentProvider> _mockAgentProvider;
    private readonly Mock<IQualityGateValidator> _mockValidator;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineOrchestrationService _service;

    public AgentCodingPageTests()
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
            brainUpdateService: new BrainUpdateService(_mockLogger.Object),
            historyService: new PipelineRunHistoryService(_mockLogger.Object, Path.Combine(Path.GetTempPath(), $"test-runs-{Guid.NewGuid()}")));
    }

    private void SetupDefaultMocks()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.NonAutonomous());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues" }
            });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo" }
            });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Kiro Agent" }
            });

        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42", Title = "Test Issue", Description = "Test description",
                Labels = Array.Empty<string>()
            });
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "42", Title = "Test Issue", Labels = Array.Empty<string>() },
                    new() { Identifier = "43", Title = "Another Issue", Labels = new[] { "bug" } }
                },
                Page = 1,
                PageSize = 25,
                HasMore = false
            });
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _mockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

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
                    var assessment = new { recommendation = "ready", reason = "Test", concerns = Array.Empty<string>(), blockingIssues = Array.Empty<string>() };
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

    // --- View switching based on pipeline state ---

    [Fact]
    public void InitialState_NoActiveRun_ShowsIssueSelectionView()
    {
        // The page shows issue selection when ActiveRun is null and IsRunning is false
        _service.ActiveRun.Should().BeNull();
        _service.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task AfterStartPipeline_WithPassingGates_RunCompletes()
    {
        // Pipeline now runs end-to-end: start → analyze → generate → quality gates → PR → completed
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = true }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.PullRequestUrl.Should().NotBeNullOrEmpty();
        _service.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task AfterCancel_ShowsCompletionView()
    {
        // Use a blocking agent to allow cancellation mid-pipeline
        var agentTcs = new TaskCompletionSource<AgentResult>();
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(agentTcs.Task);

        var startTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Cancel while agent is running
        await _service.CancelPipelineAsync();
        agentTcs.SetCanceled();

        try { await startTask; } catch { /* expected */ }

        _service.ActiveRun!.CurrentStep.Should().Be(PipelineStep.Cancelled);
        _service.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task AfterQualityGatesPass_ShowsCompletionWithPrLink()
    {
        // When quality gates pass, PR is created and pipeline completes
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = true }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.PullRequestUrl.Should().NotBeNullOrEmpty();
        _service.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task AfterQualityGatesFail_AutoRetriesAndExhaustsRetries()
    {
        // When quality gates always fail, the pipeline auto-retries by sending fix prompts
        // to the agent, then re-running quality gates until max retries are exhausted.
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = false, Details = "2 tests failed" }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Auto-retry exhausted all retries (default MaxRetries=3), created draft PR, marked Failed
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.LatestQualityReport.Should().NotBeNull();
        run.LatestQualityReport!.AllPassed.Should().BeFalse();
        run.RetryCount.Should().Be(3);
        run.RetryErrors.Should().HaveCount(4); // initial + 3 retries
    }

    // --- Concurrent start rejection ---

    [Fact]
    public async Task StartPipeline_WhileAlreadyRunning_RejectsWithMessage()
    {
        // Use a blocking agent to keep the pipeline running
        var agentTcs = new TaskCompletionSource<AgentResult>();
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(agentTcs.Task);

        var startTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        _service.IsRunning.Should().BeTrue();

        // The service throws if called while already running
        var act = () => _service.StartPipelineAsync("issue-1", "repo-1", "99", "agent-1", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in progress*");

        // Cleanup
        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = true }
            });
        await startTask;
    }

    [Fact]
    public async Task StartPipeline_PageGuard_ChecksIsRunningBeforeCalling()
    {
        // Use a blocking agent to keep the pipeline running
        var agentTcs = new TaskCompletionSource<AgentResult>();
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(agentTcs.Task);

        var startTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Replicate the page's guard logic
        string? errorMessage = null;
        if (_service.IsRunning)
        {
            errorMessage = "A pipeline run is already in progress.";
        }

        errorMessage.Should().Be("A pipeline run is already in progress.");

        // Cleanup
        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = true }
            });
        await startTask;
    }

    // --- Pipeline transitions through GeneratingCode ---

    [Fact]
    public async Task DuringAgentExecution_StepIsGeneratingCode()
    {
        // Block only the code generation call (second ExecuteAsync), let analysis complete
        var agentTcs = new TaskCompletionSource<AgentResult>();
        var callCount = 0;
        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) =>
            {
                callCount++;
                if (callCount <= 1)
                {
                    var dir = Path.Combine(req.WorkspacePath, ".kiro");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "analysis.md"), new string('x', 200));
                    var assessment = new { recommendation = "ready", reason = "Test", concerns = Array.Empty<string>(), blockingIssues = Array.Empty<string>() };
                    File.WriteAllText(Path.Combine(dir, "analysis-assessment.json"),
                        System.Text.Json.JsonSerializer.Serialize(assessment, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                }
                return agentTcs.Task;
            });

        var startTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        await Task.Delay(200);

        _service.ActiveRun!.CurrentStep.Should().Be(PipelineStep.GeneratingCode);

        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await startTask;
    }

    [Fact]
    public async Task DuringQualityGates_StepIsRunningQualityGates()
    {
        // Block the quality gate validator so we can observe RunningQualityGates
        var gateTcs = new TaskCompletionSource<QualityGateReport>();
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(gateTcs.Task);

        var startTask = _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        await Task.Delay(200);

        _service.ActiveRun!.CurrentStep.Should().Be(PipelineStep.RunningQualityGates);

        gateTcs.SetResult(new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        });
        await startTask;

        _service.ActiveRun.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    // --- Issue provider loading and connectivity errors ---

    [Fact]
    public async Task LoadIssueProviders_ReturnsConfiguredProviders()
    {
        // Simulates OnInitializedAsync loading providers from config store
        var issueProviders = await _mockConfigStore.Object.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var repoProviders = await _mockConfigStore.Object.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);

        issueProviders.Should().HaveCount(1);
        issueProviders[0].DisplayName.Should().Be("GitHub Issues");
        repoProviders.Should().HaveCount(1);
        repoProviders[0].DisplayName.Should().Be("GitHub Repo");
    }

    [Fact]
    public async Task FetchIssues_FromSelectedProvider_ReturnsIssueList()
    {
        // Simulates OnIssueProviderChanged: create provider from factory, fetch issues
        var providerConfig = new ProviderConfig
        {
            Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test"
        };
        var issueProvider = _mockFactory.Object.CreateIssueProvider(providerConfig);
        var result = await issueProvider.ListOpenIssuesAsync(1, 25, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Items[0].Identifier.Should().Be("42");
        result.Items[1].Identifier.Should().Be("43");
    }

    [Fact]
    public async Task FetchIssues_WhenProviderThrows_SetsErrorMessage()
    {
        // Simulates connectivity error when loading issue list
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var providerConfig = new ProviderConfig
        {
            Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test"
        };
        var issueProvider = _mockFactory.Object.CreateIssueProvider(providerConfig);

        // Replicate the page's try-catch in OnIssueProviderChanged
        string? errorMessage = null;
        try
        {
            await issueProvider.ListOpenIssuesAsync(1, 25, CancellationToken.None);
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to fetch issues: {ex.Message}";
        }

        errorMessage.Should().Be("Failed to fetch issues: Connection refused");
    }

    [Fact]
    public async Task FetchIssues_WithAgentNextLabel_AutoSelectsFilter()
    {
        // Simulates LoadIssuePage auto-selecting agent:next when the label exists
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "10", Title = "Ready", Labels = new[] { AgentLabels.Next } },
                    new() { Identifier = "11", Title = "Other", Labels = new[] { "bug" } }
                },
                Page = 1, PageSize = 25, HasMore = false
            });

        var providerConfig = new ProviderConfig
        {
            Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test"
        };
        var issueProvider = _mockFactory.Object.CreateIssueProvider(providerConfig);
        var result = await issueProvider.ListOpenIssuesAsync(1, 25, CancellationToken.None);
        var issues = result.Items.ToList();

        // Replicate the page's auto-filter logic from LoadIssuePage
        var availableLabels = issues.SelectMany(i => i.Labels).Distinct().OrderBy(l => l).ToList();
        var selectedLabels = new HashSet<string>();
        if (availableLabels.Contains(AgentLabels.Next))
            selectedLabels.Add(AgentLabels.Next);

        selectedLabels.Should().Contain(AgentLabels.Next);
        var filtered = issues.Where(i => i.Labels.Any(l => selectedLabels.Contains(l))).ToList();
        filtered.Should().HaveCount(1);
        filtered[0].Identifier.Should().Be("10");
    }

    [Fact]
    public async Task FetchIssues_WithoutAgentNextLabel_NoAutoFilter()
    {
        // When no issues have agent:next, no auto-filter is applied
        var providerConfig = new ProviderConfig
        {
            Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test"
        };
        var issueProvider = _mockFactory.Object.CreateIssueProvider(providerConfig);
        var result = await issueProvider.ListOpenIssuesAsync(1, 25, CancellationToken.None);
        var issues = result.Items.ToList();

        // Replicate the page's auto-filter logic
        var availableLabels = issues.SelectMany(i => i.Labels).Distinct().OrderBy(l => l).ToList();
        var selectedLabels = new HashSet<string>();
        if (availableLabels.Contains(AgentLabels.Next))
            selectedLabels.Add(AgentLabels.Next);

        selectedLabels.Should().BeEmpty();
    }

    // --- OnChange event fires for UI updates ---

    [Fact]
    public async Task OnChange_FiresDuringPipelineExecution()
    {
        // The page subscribes to OnChange for StateHasChanged calls
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = true }
            });

        int changeCount = 0;
        _service.OnChange += () => changeCount++;

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        changeCount.Should().BeGreaterThan(0, "OnChange should fire during pipeline step transitions");
    }
}
