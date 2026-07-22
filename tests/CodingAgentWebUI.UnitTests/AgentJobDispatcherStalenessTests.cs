using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Tests that <see cref="AgentJobDispatcher.DispatchToAgentAsync"/> correctly invokes
/// the <see cref="AnalysisStalenessDetector"/> via <see cref="DispatchInfrastructure.StalenessDetector"/>
/// when an existing analysis is present, and that it falls back to IssueContextBuilder-only signals
/// when the detector is null (backward compatibility with legacy/no-DB mode).
/// </summary>
// TODO: Add test for commit_threshold staleness signal (signal 3). The mock IRepositoryProvider
// never sets up GetCommitCountSinceAsync, so the delegate wiring for commit-count-based refresh
// is unreachable. A test should verify that when GetCommitCountSinceAsync returns a value at or
// above AnalysisCommitThreshold, ForceRefreshAnalysis is set with signal "commit_threshold".
//
// TODO: Add test for refresh count suppression (≥3 cap). Verify that when 3+ analysis comments
// with hash markers exist, ForceRefreshAnalysis remains false even when body_changed or agent_error
// would otherwise fire, and that the refresh count is correctly propagated.
public class AgentJobDispatcherStalenessTests : IDisposable
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly AgentRegistryService _registry;
    private readonly JobDeduplicationGuardService _dedup;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<IWorkItemQueryService> _mockWorkItemQuery = new();
    private readonly HttpClient _httpClient;
    private readonly TokenVendingService _tokenVending;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();

    private JobAssignmentMessage? _capturedMessage;

    private static readonly DateTime AnalysisTime = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    public AgentJobDispatcherStalenessTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dedup = new JobDeduplicationGuardService(_registry, _mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _httpClient = new HttpClient();
        _tokenVending = new TokenVendingService(_mockLogger.Object, _httpClient);

        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = _mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });
        _mockConfigStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        _mockAgentComm
            .Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, JobAssignmentMessage, CancellationToken>((_, msg, _) => _capturedMessage = msg)
            .Returns(Task.CompletedTask);

        // Default: no errors, no prior successes
        _mockWorkItemQuery
            .Setup(q => q.GetLastSuccessfulCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset?)null);
        _mockWorkItemQuery
            .Setup(q => q.HasAgentErrorSinceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
    }

    private AgentJobDispatcher CreateDispatcher(bool withStalenessDetector)
    {
        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockConfigStore.Object,
            providerFactory: _mockProviderFactory.Object,
            historyService: _mockHistoryService.Object,
            runService: _runService);

        var infra = new DispatchInfrastructure(
            _tokenVending,
            _mockProviderFactory.Object,
            _mockLabelService.Object,
            new DispatchResolutionService(
                new ProfileResolver(),
                new QualityGateResolver(),
                new ReviewerResolver(),
                _mockConfigStore.Object,
                _mockLogger.Object));

        if (withStalenessDetector)
        {
            infra.StalenessDetector = new AnalysisStalenessDetector(
                _mockWorkItemQuery.Object, _mockLogger.Object);
        }

        return new AgentJobDispatcher(
            _dedup,
            _registry,
            _runService,
            runCreator,
            infra,
            _mockAgentComm.Object,
            new ShutdownSignal(),
            _mockLogger.Object);
    }

    private void SetupHappyPathMocks()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AnalysisCommitThreshold = 30 });
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new AgentProfile
                {
                    Id = "profile-1",
                    DisplayName = "Test Profile",
                    AgentProviderConfigId = "agent-1",
                    MatchLabels = Array.Empty<string>(),
                    McpServers = new List<McpServerConfig>()
                }
            });
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());

        var repoConfig = new ProviderConfig { Id = "rp", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
        var agentConfig = new ProviderConfig { Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Agent" };

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { repoConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { agentConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.RepositoryFullName).Returns("org/repo");
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
    }

    private void SetupIssueProviderMock(IReadOnlyList<IssueComment> comments, string description = "original body")
    {
        var issueConfig = new ProviderConfig { Id = "ip", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Issue Provider" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { issueConfig });

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "issue-1",
                Title = "Test Issue",
                Description = description,
                Labels = Array.Empty<string>()
            });
        mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(comments);
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
    }

    private AgentEntry RegisterAgent()
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-staleness",
            Hostname = "host",
            Labels = Array.Empty<string>()
        }, "conn-staleness");
    }

    // ── When StalenessDetector is null, staleness fields come from IssueContextBuilder only ──

    [Fact]
    public async Task DispatchToAgentAsync_NoDetector_UsesIssueContextBuilderSignalsOnly()
    {
        SetupHappyPathMocks();
        // Create an analysis comment with a hash that differs from current body (would trigger body_changed if detector existed)
        var hash = AnalysisBodyHash.Compute("old body");
        var analysisComment = new IssueComment
        {
            Id = "ac-1",
            Author = "bot",
            Body = $"## 🤖 Agent Analysis\n\nContent\n<!-- agent:analysis-body-hash:{hash} -->",
            CreatedAt = AnalysisTime
        };
        SetupIssueProviderMock(new[] { analysisComment }, description: "new body");

        var agent = RegisterAgent();
        var dispatcher = CreateDispatcher(withStalenessDetector: false);

        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-1", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeTrue();
        _capturedMessage.Should().NotBeNull();

        // Without detector, ForceRefreshAnalysis comes from IssueContextBuilder only
        // (gate_rejection / gate_wont_do). No body_changed signal is detected.
        _capturedMessage!.ForceRefreshAnalysis.Should().BeFalse();
        _capturedMessage.StalenessSignal.Should().BeNull();
        _capturedMessage.AnalysisRefreshCount.Should().Be(0);
    }

    // ── When StalenessDetector is set and body_changed signal fires ──

    [Fact]
    public async Task DispatchToAgentAsync_WithDetector_BodyChanged_SetsForceRefreshAndSignal()
    {
        SetupHappyPathMocks();
        // Create an analysis comment whose hash doesn't match current body → body_changed signal
        var hash = AnalysisBodyHash.Compute("old body");
        var analysisComment = new IssueComment
        {
            Id = "ac-1",
            Author = "bot",
            Body = $"## 🤖 Agent Analysis\n\nContent\n<!-- agent:analysis-body-hash:{hash} -->",
            CreatedAt = AnalysisTime
        };
        SetupIssueProviderMock(new[] { analysisComment }, description: "new body");

        var agent = RegisterAgent();
        var dispatcher = CreateDispatcher(withStalenessDetector: true);

        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-1", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeTrue();
        _capturedMessage.Should().NotBeNull();

        _capturedMessage!.ForceRefreshAnalysis.Should().BeTrue();
        _capturedMessage.StalenessSignal.Should().Be("body_changed");
        // TODO: Assert _capturedMessage.AnalysisRefreshCount == 1 — currently unverified.
        // With one hash-marked analysis comment and GetLastSuccessfulCompletionAsync returning null,
        // the detector computes refreshCount=1 but this is never checked.
    }

    // ── When StalenessDetector is set and agent_error signal fires ──

    [Fact]
    public async Task DispatchToAgentAsync_WithDetector_AgentError_SetsForceRefreshAndSignal()
    {
        SetupHappyPathMocks();
        // Create an analysis comment whose hash matches current body (no body_changed)
        // but agent_error signal fires via DB query
        var hash = AnalysisBodyHash.Compute("current body");
        var analysisComment = new IssueComment
        {
            Id = "ac-1",
            Author = "bot",
            Body = $"## 🤖 Agent Analysis\n\nContent\n<!-- agent:analysis-body-hash:{hash} -->",
            CreatedAt = AnalysisTime
        };
        SetupIssueProviderMock(new[] { analysisComment }, description: "current body");

        _mockWorkItemQuery
            .Setup(q => q.HasAgentErrorSinceAsync("issue-1", "ip", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var agent = RegisterAgent();
        var dispatcher = CreateDispatcher(withStalenessDetector: true);

        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-1", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeTrue();
        _capturedMessage.Should().NotBeNull();

        _capturedMessage!.ForceRefreshAnalysis.Should().BeTrue();
        _capturedMessage.StalenessSignal.Should().Be("agent_error");
        // TODO: Assert _capturedMessage.AnalysisRefreshCount == 1 — currently unverified.
        // Same reasoning as BodyChanged test: one hash-marked analysis comment, lastSuccess=null → refreshCount=1.
    }

    // ── When StalenessDetector is set but no staleness signals fire ──

    [Fact]
    public async Task DispatchToAgentAsync_WithDetector_NoStaleness_KeepsOriginalValues()
    {
        SetupHappyPathMocks();
        // Create an analysis comment whose hash matches current body (no body_changed)
        // and no agent_error either
        var hash = AnalysisBodyHash.Compute("current body");
        var analysisComment = new IssueComment
        {
            Id = "ac-1",
            Author = "bot",
            Body = $"## 🤖 Agent Analysis\n\nContent\n<!-- agent:analysis-body-hash:{hash} -->",
            CreatedAt = AnalysisTime
        };
        SetupIssueProviderMock(new[] { analysisComment }, description: "current body");

        var agent = RegisterAgent();
        var dispatcher = CreateDispatcher(withStalenessDetector: true);

        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-1", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeTrue();
        _capturedMessage.Should().NotBeNull();

        _capturedMessage!.ForceRefreshAnalysis.Should().BeFalse();
        _capturedMessage.StalenessSignal.Should().BeNull();
        // TODO: Assert _capturedMessage.AnalysisRefreshCount == 1 — currently unverified.
        // The detector runs fully (no early return), computes refreshCount=1, and propagates to the message.
        // Also: this test implicitly exercises the commit_threshold path (signal 3) because the
        // repo provider config matches, creating a getCommitCount delegate. It relies on Moq's default
        // int return (0) from the un-setup GetCommitCountSinceAsync rather than explicit setup.
        // If the delegate wiring broke, this test would still pass.
    }

    // ── When IssueContextBuilder already set ForceRefresh (gate_rejection), detector is skipped ──

    [Fact]
    public async Task DispatchToAgentAsync_WithDetector_GateRejectionAlreadySet_DetectorSkipped()
    {
        SetupHappyPathMocks();
        // Create an analysis comment + a newer gate rejection comment
        var hash = AnalysisBodyHash.Compute("old body");
        var analysisComment = new IssueComment
        {
            Id = "ac-1",
            Author = "bot",
            Body = $"## 🤖 Agent Analysis\n\nContent\n<!-- agent:analysis-body-hash:{hash} -->",
            CreatedAt = AnalysisTime
        };
        var gateRejectionComment = new IssueComment
        {
            Id = "gr-1",
            Author = "bot",
            Body = $"{CommentMarkers.GateRejection}\nNot ready",
            CreatedAt = AnalysisTime.AddHours(1)
        };
        SetupIssueProviderMock(new[] { analysisComment, gateRejectionComment }, description: "new body");

        var agent = RegisterAgent();
        var dispatcher = CreateDispatcher(withStalenessDetector: true);

        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-1", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeTrue();
        _capturedMessage.Should().NotBeNull();

        // gate_rejection detected by IssueContextBuilder — detector is skipped
        _capturedMessage!.ForceRefreshAnalysis.Should().BeTrue();
        _capturedMessage.StalenessSignal.Should().Be("gate_rejection");

        // Verify detector was never called
        _mockWorkItemQuery.Verify(
            q => q.HasAgentErrorSinceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── No existing analysis → detector is skipped ──

    [Fact]
    public async Task DispatchToAgentAsync_WithDetector_NoExistingAnalysis_DetectorSkipped()
    {
        SetupHappyPathMocks();
        // No analysis comment at all
        SetupIssueProviderMock(Array.Empty<IssueComment>(), description: "some body");

        var agent = RegisterAgent();
        var dispatcher = CreateDispatcher(withStalenessDetector: true);

        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-1", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeTrue();
        _capturedMessage.Should().NotBeNull();

        _capturedMessage!.ForceRefreshAnalysis.Should().BeFalse();
        _capturedMessage.StalenessSignal.Should().BeNull();
        _capturedMessage.ExistingAnalysis.Should().BeNull();

        // Detector never invoked
        _mockWorkItemQuery.Verify(
            q => q.HasAgentErrorSinceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
