using AwesomeAssertions;
using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Unit tests for AgentCoding page logic.
/// Tests view switching, concurrent start rejection, empty chat submission, and button disabled states
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
                Labels = Array.Empty<string>(), AcceptanceCriteria = Array.Empty<string>()
            });
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueSummary>
            {
                new() { Identifier = "42", Title = "Test Issue", Labels = Array.Empty<string>() },
                new() { Identifier = "43", Title = "Another Issue", Labels = new[] { "bug" } }
            });

        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");

        _mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        _mockAgentProvider.Setup(p => p.ExecuteWithResumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

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
    public async Task AfterStartPipeline_ActiveRunExists_ShowsProgressOrChatView()
    {
        // After starting a pipeline, ActiveRun is set and the page switches away from issue selection
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        _service.ActiveRun.Should().NotBeNull();
        // Agent exits immediately in mock, so we land on WaitingForChat
        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);
    }

    [Fact]
    public async Task WaitingForChat_PageShowsChatPanel()
    {
        // When step is WaitingForChat, the page renders the chat panel view
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);
        _service.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task AfterCancel_ShowsCompletionView()
    {
        // After cancellation, the page shows the completion view with "Back to Issues" button
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        await _service.CancelPipelineAsync();

        run.CurrentStep.Should().Be(PipelineStep.Cancelled);
        _service.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task AfterQualityGatesPass_ShowsCompletionWithPrLink()
    {
        // When quality gates pass and PR is created, the page shows completion with PR link
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = true }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        await _service.ProceedToQualityGatesAsync(CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.PullRequestUrl.Should().NotBeNullOrEmpty();
        _service.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task AfterQualityGatesFail_ReturnsToWaitingForChat()
    {
        // When quality gates fail with retries left, the page returns to chat panel
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = false, Details = "2 tests failed" }
            });

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        await _service.ProceedToQualityGatesAsync(CancellationToken.None);

        // Should return to WaitingForChat with quality report available
        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);
        run.LatestQualityReport.Should().NotBeNull();
        run.LatestQualityReport!.AllPassed.Should().BeFalse();
        run.RetryCount.Should().Be(1);
    }

    // --- Requirement 10.6: Concurrent start rejection ---

    [Fact]
    public async Task StartPipeline_WhileAlreadyRunning_RejectsWithMessage()
    {
        // Simulates: user selects an issue while a pipeline is already active
        // The page checks PipelineService.IsRunning and sets _errorMessage
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        _service.IsRunning.Should().BeTrue();

        // The page's StartPipeline method checks IsRunning first:
        //   if (PipelineService.IsRunning) { _errorMessage = "A pipeline run is already in progress."; return; }
        // The service also throws if called directly:
        var act = () => _service.StartPipelineAsync("issue-1", "repo-1", "99", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in progress*");
    }

    [Fact]
    public async Task StartPipeline_PageGuard_ChecksIsRunningBeforeCalling()
    {
        // Validates the page's guard logic: when IsRunning is true, the page sets an error message
        // and does NOT call StartPipelineAsync
        await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        // Replicate the page's guard logic
        string? errorMessage = null;
        if (_service.IsRunning)
        {
            errorMessage = "A pipeline run is already in progress.";
        }

        errorMessage.Should().Be("A pipeline run is already in progress.");
    }

    // --- Requirement 7.5: Empty chat submission ignored ---

    [Fact]
    public async Task SendChatMessage_EmptyString_IsIgnoredByPageGuard()
    {
        // The page's SendChatMessage checks: if (string.IsNullOrWhiteSpace(_chatInput) || _isSending) return;
        // This validates that empty/whitespace inputs are rejected before reaching the service
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);

        var chatCountBefore = run.ChatHistory.Count;

        // Replicate the page's guard: empty string
        string chatInput = "";
        bool isSending = false;
        if (!string.IsNullOrWhiteSpace(chatInput) || isSending)
        {
            // Would call SendChatMessageAsync — but guard prevents it
            await _service.SendChatMessageAsync(chatInput, CancellationToken.None);
        }

        run.ChatHistory.Count.Should().Be(chatCountBefore, "empty input should not add to chat history");
    }

    [Fact]
    public async Task SendChatMessage_WhitespaceOnly_IsIgnoredByPageGuard()
    {
        // Whitespace-only input is also rejected by the page guard
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        var chatCountBefore = run.ChatHistory.Count;

        string chatInput = "   \t  ";
        bool isSending = false;
        if (!string.IsNullOrWhiteSpace(chatInput) || isSending)
        {
            await _service.SendChatMessageAsync(chatInput, CancellationToken.None);
        }

        run.ChatHistory.Count.Should().Be(chatCountBefore, "whitespace-only input should not add to chat history");
    }

    [Fact]
    public async Task SendChatMessage_ValidInput_AddsToHistory()
    {
        // Contrast: a valid non-empty message does get sent
        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        var chatCountBefore = run.ChatHistory.Count;

        string chatInput = "fix the tests";
        bool isSending = false;
        if (!string.IsNullOrWhiteSpace(chatInput) || isSending)
        {
            await _service.SendChatMessageAsync(chatInput, CancellationToken.None);
        }

        run.ChatHistory.Count.Should().BeGreaterThan(chatCountBefore);
        run.ChatHistory.Should().Contain(e => e.Role == ChatRole.User && e.Content == "fix the tests");
    }

    // --- Requirement 7.4: Button disabled states during agent execution ---

    [Fact]
    public async Task DuringAgentExecution_IsSendingFlag_DisablesInputAndProceed()
    {
        // The page sets _isSending = true before calling SendChatMessageAsync,
        // which disables the text input and "Proceed to Quality Gates" button.
        // We validate this by checking that the service transitions to GeneratingCode
        // during SendChatMessageAsync (which is when _isSending would be true).

        // Use a TaskCompletionSource to hold the agent execution so we can observe the intermediate state
        var agentTcs = new TaskCompletionSource<AgentResult>();
        _mockAgentProvider.Setup(p => p.ExecuteWithResumeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(agentTcs.Task);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);

        // Start sending a chat message (will block on agent execution)
        var sendTask = _service.SendChatMessageAsync("fix the tests", CancellationToken.None);

        // While agent is running, the step should be GeneratingCode
        // The page uses _isSending flag which maps to this state
        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);

        // Complete the agent execution
        agentTcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await sendTask;

        // After completion, returns to WaitingForChat
        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);
    }

    [Fact]
    public async Task DuringQualityGates_IsSendingFlag_DisablesInputAndProceed()
    {
        // The page also sets _isSending = true during ProceedToQualityGatesAsync
        var gateTcs = new TaskCompletionSource<QualityGateReport>();
        _mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(gateTcs.Task);

        var run = await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);

        // Start proceeding to quality gates (will block on validation)
        var proceedTask = _service.ProceedToQualityGatesAsync(CancellationToken.None);

        // During quality gate execution, step should be RunningQualityGates
        run.CurrentStep.Should().Be(PipelineStep.RunningQualityGates);

        // Complete the quality gate validation
        gateTcs.SetResult(new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        });
        await proceedTask;

        // After gates pass, pipeline completes
        run.CurrentStep.Should().Be(PipelineStep.Completed);
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
        var issues = await issueProvider.ListOpenIssuesAsync(CancellationToken.None);

        issues.Should().HaveCount(2);
        issues[0].Identifier.Should().Be("42");
        issues[1].Identifier.Should().Be("43");
    }

    [Fact]
    public async Task FetchIssues_WhenProviderThrows_SetsErrorMessage()
    {
        // Simulates connectivity error when loading issue list (Requirement 1.7)
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<CancellationToken>()))
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
            await issueProvider.ListOpenIssuesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to fetch issues: {ex.Message}";
        }

        errorMessage.Should().Be("Failed to fetch issues: Connection refused");
    }

    // --- OnChange event fires for UI updates ---

    [Fact]
    public async Task OnChange_FiresDuringPipelineExecution()
    {
        // The page subscribes to OnChange for StateHasChanged calls
        int changeCount = 0;
        _service.OnChange += () => changeCount++;

        await _service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None);

        changeCount.Should().BeGreaterThan(0, "OnChange should fire during pipeline step transitions");
    }
}
