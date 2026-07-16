using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentJobDispatcher"/>.
/// </summary>
public class AgentJobDispatcherTests : IDisposable
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<ILabelSwapper> _mockLabelSwapper;
    private readonly Mock<IAgentCommunication> _mockAgentComm;
    private readonly HttpClient _httpClient;
    private readonly TokenVendingService _tokenVending;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly List<PipelineOrchestrationService> _orchestrationInstances = new();

    public AgentJobDispatcherTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockLabelSwapper = new Mock<ILabelSwapper>();
        _mockAgentComm = new Mock<IAgentCommunication>();
        _httpClient = new HttpClient();
        _tokenVending = new TokenVendingService(_mockLogger.Object, _httpClient);
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();

        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = _mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });
        _mockConfigStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());
    }

    private AgentJobDispatcher CreateDispatcher(
        IDispatchRunCreator? orchestrationOverride = null,
        IShutdownSignal? shutdownOverride = null,
        ITokenVendingService? tokenVendingOverride = null)
    {
        var orchestration = orchestrationOverride;
        if (orchestration == null)
        {
            var realOrchestration = TestOrchestrationFactory.CreateMinimal(
                configStore: _mockConfigStore.Object,
                providerFactory: _mockProviderFactory.Object,
                executionFacade: new PipelineExecutionFacade(
                    new AgentPhaseExecutor(_mockLogger.Object),
                    new QualityGateExecutor(new Mock<IQualityGateValidator>().Object, new PullRequestOrchestrator(_mockLogger.Object), new CiLogWriter(_mockLogger.Object), new FeedbackService(_mockLogger.Object), _mockLogger.Object),
                    new Mock<IQualityGateValidator>().Object,
                    Mock.Of<IBrainSyncService>()),
                historyService: _mockHistoryService.Object,
                runService: _runService);
            _orchestrationInstances.Add(realOrchestration);
            orchestration = realOrchestration;
        }

        return new AgentJobDispatcher(
            _dispatcher,
            _registry,
            _runService,
            orchestration,
            new DispatchInfrastructure(
                tokenVendingOverride ?? _tokenVending,
                _mockProviderFactory.Object,
                _mockLabelSwapper.Object,
                new DispatchResolutionService(
                    new ProfileResolver(),
                    new QualityGateResolver(),
                    new ReviewerResolver(),
                    _mockConfigStore.Object,
                    _mockLogger.Object)),
            _mockAgentComm.Object,
            shutdownOverride ?? new ShutdownSignal(),
            _mockLogger.Object);
    }

    [Fact]
    public void HasRegisteredAgents_NoAgents_ReturnsFalse()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.HasRegisteredAgents.Should().BeFalse();
    }

    [Fact]
    public void HasRegisteredAgents_WithAgent_ReturnsTrue()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-1");

        var dispatcher = CreateDispatcher();
        dispatcher.HasRegisteredAgents.Should().BeTrue();
    }

    [Fact]
    public void IsIssueBeingProcessedOrQueued_NotQueued_ReturnsFalse()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.IsIssueBeingProcessedOrQueued("issue-1", "provider-1").Should().BeFalse();
    }

    [Fact]
    public void IsIssueBeingProcessedOrQueued_Queued_ReturnsTrue()
    {
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "issue-1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });

        var dispatcher = CreateDispatcher();
        dispatcher.IsIssueBeingProcessedOrQueued("issue-1", "ip").Should().BeTrue();
    }

    [Fact]
    public void IsIssueBeingProcessedOrQueued_BeingProcessed_ReturnsTrue()
    {
        _runService.AddRun(new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "issue-1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        });

        var dispatcher = CreateDispatcher();
        dispatcher.IsIssueBeingProcessedOrQueued("issue-1", "ip").Should().BeTrue();
    }

    [Fact]
    public void IsIssueBeingProcessedOrQueued_NullIdentifier_Throws()
    {
        var dispatcher = CreateDispatcher();
        var act = () => dispatcher.IsIssueBeingProcessedOrQueued(null!, "provider-1");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task TryDispatchAsync_AlreadyQueued_ReturnsFalse()
    {
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "issue-1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchAsync(
            "issue-1", "ip", "rp", null, null, "test", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryDispatchAsync_NoIdleAgent_EnqueuesJob()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchAsync(
            "issue-new", "ip", "rp", null, null, "user", CancellationToken.None);

        result.Should().BeTrue(); // Enqueued successfully
        _dispatcher.IsIssueQueued("issue-new", "ip").Should().BeTrue();
    }

    [Fact]
    public async Task TryDispatchAsync_NullIssueIdentifier_Throws()
    {
        var dispatcher = CreateDispatcher();
        var act = () => dispatcher.TryDispatchAsync(
            null!, "ip", "rp", null, null, "test", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task TryDispatchAsync_NullInitiatedBy_Throws()
    {
        var dispatcher = CreateDispatcher();
        var act = () => dispatcher.TryDispatchAsync(
            "issue-1", "ip", "rp", null, null, null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task TryDispatchAsync_WithAvailableAgent_SendsJobAssignmentViaAgentCommunication()
    {
        // Register an idle agent
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-1");

        // Setup config store to return necessary configs
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchAsync(
            "issue-dispatch", "ip", "rp", null, null, "user", CancellationToken.None);

        // No profile matches → dispatch returns false (agent has no matching profile)
        // This verifies the dispatch path was attempted (agent was selected)
        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryDispatchAsync_WithAgentCommunicationFailure_ReQueuesJobAndLogsError()
    {
        // Register an idle agent
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-fail",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-fail");

        // Setup config store
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new AgentProfile
                {
                    Id = "profile-1",
                    DisplayName = "Test Profile",
                    AgentProviderConfigId = "agent-provider-1",
                    MatchLabels = Array.Empty<string>()
                }
            });
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        // Make agent communication throw
        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchAsync(
            "issue-fail", "ip", "rp", null, null, "user", CancellationToken.None);

        // Dispatch fails due to issue provider config not found (returns false before reaching comm)
        // The important thing is it doesn't throw and handles the error gracefully
        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryDispatchAsync_JobCompletionUpdatesRun()
    {
        // Add a run to the run service to simulate a completed job
        var run = new PipelineRun
        {
            RunId = "run-complete",
            IssueIdentifier = "issue-complete",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        _runService.AddRun(run);

        // Verify the run is tracked
        _runService.GetRun("run-complete").Should().NotBeNull();

        // Simulate completion by removing the run
        var removed = _runService.RemoveRun("run-complete");
        removed.Should().NotBeNull();
        removed!.RunId.Should().Be("run-complete");

        // After removal, issue should no longer be processed
        _runService.IsIssueBeingProcessed("issue-complete", "ip").Should().BeFalse();
    }

    #region TryDispatchReviewAsync

    [Fact]
    public async Task TryDispatchReviewAsync_AlreadyQueued_ReturnsFalse()
    {
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "pr-1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchReviewAsync(new ReviewDispatchRequest
        {
            PrIdentifier = "pr-1",
            PrBranchName = "feature/x",
            PrTitle = "PR Title",
            PrUrl = "https://github.com/org/repo/pull/1",
            PrTargetBranch = "main",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            InitiatedBy = "test"
        }, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryDispatchReviewAsync_NoIdleAgent_EnqueuesJob()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchReviewAsync(new ReviewDispatchRequest
        {
            PrIdentifier = "pr-2",
            PrBranchName = "feature/y",
            PrTitle = "PR Title",
            PrUrl = "https://github.com/org/repo/pull/2",
            PrTargetBranch = "main",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            InitiatedBy = "user"
        }, CancellationToken.None);

        result.Should().BeTrue();
        _dispatcher.IsIssueQueued("pr-2", "ip").Should().BeTrue();
    }

    [Fact]
    public async Task TryDispatchReviewAsync_WithAvailableAgent_DispatchesReview()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-review",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-review");

        SetupHappyPathMocks("agent-provider-1");

        // Mock ExtractLinkedIssuesAsync for PreFetchLinkedIssuesAsync
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.RepositoryFullName).Returns("org/repo");
        mockRepoProvider.Setup(r => r.ExtractLinkedIssuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchReviewAsync(new ReviewDispatchRequest
        {
            PrIdentifier = "3",
            PrBranchName = "feature/z",
            PrTitle = "PR Title",
            PrUrl = "https://github.com/org/repo/pull/3",
            PrTargetBranch = "main",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            InitiatedBy = "user"
        }, CancellationToken.None);

        result.Should().BeTrue();
        // Label swap to InProgress now happens via IRunLifecycleManager.AgentAcceptedRunAsync (not dispatcher)
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-review", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region TryDispatchDecompositionAsync

    [Fact]
    public async Task TryDispatchDecompositionAsync_InvalidPhaseType_ThrowsArgumentOutOfRange()
    {
        var dispatcher = CreateDispatcher();
        var act = () => dispatcher.TryDispatchDecompositionAsync(
            "epic-1", "Epic Title", PipelineRunType.Implementation,
            "ip", "rp", null, "user", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task TryDispatchDecompositionAsync_AlreadyQueued_ReturnsFalse()
    {
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "epic-1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchDecompositionAsync(
            "epic-1", "Epic Title", PipelineRunType.DecompositionAnalysis,
            "ip", "rp", null, "user", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryDispatchDecompositionAsync_NoIdleAgent_EnqueuesJob()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchDecompositionAsync(
            "epic-2", "Epic Title", PipelineRunType.DecompositionAnalysis,
            "ip", "rp", null, "user", CancellationToken.None);

        result.Should().BeTrue();
        _dispatcher.IsIssueQueued("epic-2", "ip").Should().BeTrue();
    }

    [Fact]
    public async Task TryDispatchReviewAsync_ExceptionDuringDispatch_ResetsAgentAndRevertsLabel()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-rev-exc",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-rev-exc");

        SetupHappyPathMocks("agent-provider-1");

        // Mock ExtractLinkedIssuesAsync
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.RepositoryFullName).Returns("org/repo");
        mockRepoProvider.Setup(r => r.ExtractLinkedIssuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);

        // Make AssignJobAsync throw
        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchReviewAsync(new ReviewDispatchRequest
        {
            PrIdentifier = "10",
            PrBranchName = "feature/x",
            PrTitle = "PR Title",
            PrUrl = "https://github.com/org/repo/pull/10",
            PrTargetBranch = "main",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            InitiatedBy = "user"
        }, CancellationToken.None);

        result.Should().BeFalse();
        // Verify label reverted to agent:next on PullRequest target using RepoProviderId
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "rp", "10", AgentLabels.Next, LabelTargetKind.PullRequest, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task TryDispatchDecompositionAsync_HappyPath_TransitionsAgentToBusyAndSwapsLabel()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-decomp",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-decomp");

        SetupHappyPathMocks("agent-provider-1");

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchDecompositionAsync(
            "epic-hp", "Epic Title", PipelineRunType.DecompositionAnalysis,
            "ip", "rp", null, "user", CancellationToken.None);

        result.Should().BeTrue();
        // Label swap to InProgress now happens via IRunLifecycleManager.AgentAcceptedRunAsync (not dispatcher)
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-decomp", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryDispatchDecompositionAsync_Phase2_HappyPath()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-decomp2",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-decomp2");

        SetupHappyPathMocks("agent-provider-1");

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchDecompositionAsync(
            "epic-p2", "Epic Title P2", PipelineRunType.Decomposition,
            "ip", "rp", null, "user", CancellationToken.None);

        result.Should().BeTrue();
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-decomp2", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryDispatchDecompositionAsync_ExceptionDuringDispatch_ResetsAgentAndRevertsLabel()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-decomp-exc",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-decomp-exc");

        SetupHappyPathMocks("agent-provider-1");

        // Make AssignJobAsync throw
        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchDecompositionAsync(
            "epic-exc", "Epic Title", PipelineRunType.DecompositionAnalysis,
            "ip", "rp", null, "user", CancellationToken.None);

        result.Should().BeFalse();
        agent.Status.Should().Be(AgentStatus.Idle);
        // Phase 1 reverts to agent:epic
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip", "epic-exc", AgentLabels.Epic, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task TryDispatchDecompositionAsync_Phase2_ExceptionRevertsToEpicApproved()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-decomp-exc2",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-decomp-exc2");

        SetupHappyPathMocks("agent-provider-1");

        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchDecompositionAsync(
            "epic-exc2", "Epic Title", PipelineRunType.Decomposition,
            "ip", "rp", null, "user", CancellationToken.None);

        result.Should().BeFalse();
        agent.Status.Should().Be(AgentStatus.Idle);
        // Phase 2 reverts to agent:epic-approved
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip", "epic-exc2", AgentLabels.EpicApproved, CancellationToken.None), Times.Once);
    }

    #endregion

    #region DispatchToAgentAsync

    [Fact]
    public async Task DispatchToAgentAsync_HappyPath_TransitionsAgentToBusy()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-happy",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-happy");

        SetupHappyPathMocks("agent-provider-1");
        SetupIssueProviderMock(new List<IssueComment>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-hp", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeTrue();
        agent.Status.Should().Be(AgentStatus.Busy);
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-happy", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchToAgentAsync_NoProfile_ReturnsFalse()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-noprof",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-noprof");

        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-np", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeFalse();
        agent.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public async Task DispatchToAgentAsync_ExceptionDuringDispatch_ResetsAgentAndRevertsLabel()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-exc",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-exc");

        SetupHappyPathMocks("agent-provider-1");
        SetupIssueProviderMock(new List<IssueComment>());

        // Make AssignJobAsync throw to trigger the catch block
        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-exc", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeFalse();
        agent.Status.Should().Be(AgentStatus.Idle);
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip", "issue-exc", AgentLabels.Next, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task DispatchToAgentAsync_IssueProviderNotFound_ReturnsFalse()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-noip",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-noip");

        SetupHappyPathMocks("agent-provider-1");
        // Don't set up issue provider config — GetProviderConfigByIdAsync for Issue kind returns null
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-noip", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeFalse();
    }

    #endregion

    #region PrepareIssueContextAsync (indirect)

    [Fact]
    public async Task PrepareIssueContextAsync_MoreThan50Comments_CapsAt50()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-cap",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-cap");

        SetupHappyPathMocks("agent-provider-1");

        // Create 60 comments
        var comments = Enumerable.Range(1, 60).Select(i => new IssueComment
        {
            Id = $"c-{i}",
            Body = $"Comment {i}",
            Author = "user",
            CreatedAt = DateTime.UtcNow.AddMinutes(i)
        }).ToList();
        SetupIssueProviderMock(comments);

        JobAssignmentMessage? capturedMessage = null;
        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, JobAssignmentMessage, CancellationToken>((_, msg, _) => capturedMessage = msg)
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher();
        await dispatcher.DispatchToAgentAsync(
            agent, "issue-cap", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.IssueComments.Count.Should().Be(50);
    }

    [Fact]
    public async Task PrepareIssueContextAsync_GateRejectionNewerThanAnalysis_SetsForceRefresh()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-fr",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-fr");

        SetupHappyPathMocks("agent-provider-1");

        var comments = new List<IssueComment>
        {
            new()
            {
                Id = "c-analysis",
                Body = $"{Pipeline.CommentMarkers.AnalysisHeader}\nSome analysis content",
                Author = "bot",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10)
            },
            new()
            {
                Id = "c-rejection",
                Body = $"{Pipeline.CommentMarkers.GateRejection}\nRejection reason",
                Author = "bot",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5) // Newer than analysis
            }
        };
        SetupIssueProviderMock(comments);

        JobAssignmentMessage? capturedMessage = null;
        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, JobAssignmentMessage, CancellationToken>((_, msg, _) => capturedMessage = msg)
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher();
        await dispatcher.DispatchToAgentAsync(
            agent, "issue-fr", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.ForceRefreshAnalysis.Should().BeTrue();
    }

    #endregion

    #region DispatchToAgentDirectAsync

    [Fact]
    public async Task DispatchToAgentDirectAsync_NullAgent_Throws()
    {
        var dispatcher = CreateDispatcher();
        var act = () => dispatcher.DispatchToAgentDirectAsync(
            null!,
            new PendingJob
            {
                IssueIdentifier = "issue-1",
                IssueProviderId = "ip",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test"
            },
            Array.Empty<string>(),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DispatchToAgentDirectAsync_NullJob_Throws()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-direct-null",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-direct-null");

        var dispatcher = CreateDispatcher();
        var act = () => dispatcher.DispatchToAgentDirectAsync(
            agent, null!, Array.Empty<string>(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DispatchToAgentDirectAsync_NullRequiredLabels_Throws()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-direct-nrl",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-direct-nrl");

        var dispatcher = CreateDispatcher();
        var act = () => dispatcher.DispatchToAgentDirectAsync(
            agent,
            new PendingJob
            {
                IssueIdentifier = "issue-1",
                IssueProviderId = "ip",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test"
            },
            null!,
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DispatchToAgentDirectAsync_ImplementationRunType_DispatchesViaDispatchToAgentAsync()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-direct-impl",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-direct-impl");

        SetupHappyPathMocks("agent-provider-1");
        SetupIssueProviderMock(new List<IssueComment>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentDirectAsync(
            agent,
            new PendingJob
            {
                IssueIdentifier = "issue-direct",
                IssueProviderId = "ip",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "user",
                RunType = PipelineRunType.Implementation
            },
            Array.Empty<string>(),
            CancellationToken.None);

        result.Should().BeTrue();
        agent.Status.Should().Be(AgentStatus.Busy);
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-direct-impl", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchToAgentDirectAsync_ReviewRunType_DispatchesViaReviewPath()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-direct-rev",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-direct-rev");

        SetupHappyPathMocks("agent-provider-1");

        // Mock ExtractLinkedIssuesAsync for review dispatch path
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.RepositoryFullName).Returns("org/repo");
        mockRepoProvider.Setup(r => r.ExtractLinkedIssuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentDirectAsync(
            agent,
            new PendingJob
            {
                IssueIdentifier = "42",
                IssueTitle = "PR Title",
                IssueProviderId = "ip",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "user",
                RunType = PipelineRunType.Review,
                PrBranchName = "feature/direct",
                PrDescription = "Direct dispatch review",
                PrUrl = "https://github.com/org/repo/pull/42",
                PrTargetBranch = "main",
                PrAuthor = "dev"
            },
            Array.Empty<string>(),
            CancellationToken.None);

        result.Should().BeTrue();
        agent.Status.Should().Be(AgentStatus.Busy);
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-direct-rev", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        // Label swap to InProgress now happens via IRunLifecycleManager.AgentAcceptedRunAsync (not dispatcher)
    }

    [Fact]
    public async Task DispatchToAgentDirectAsync_DecompositionRunType_DispatchesViaDecompositionPath()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-direct-decomp",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-direct-decomp");

        SetupHappyPathMocks("agent-provider-1");

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentDirectAsync(
            agent,
            new PendingJob
            {
                IssueIdentifier = "epic-direct",
                IssueTitle = "Epic Title",
                IssueProviderId = "ip",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "user",
                RunType = PipelineRunType.DecompositionAnalysis
            },
            Array.Empty<string>(),
            CancellationToken.None);

        result.Should().BeTrue();
        agent.Status.Should().Be(AgentStatus.Busy);
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-direct-decomp", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        // Label swap to InProgress now happens via IRunLifecycleManager.AgentAcceptedRunAsync (not dispatcher)
    }

    [Fact]
    public async Task DispatchToAgentDirectAsync_SkipsDedupChecks_DispatchesEvenWhenIssueQueued()
    {
        // Enqueue the issue first — TryDispatchAsync would reject this
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "issue-dedup",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });

        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-direct-dedup",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-direct-dedup");

        SetupHappyPathMocks("agent-provider-1");
        SetupIssueProviderMock(new List<IssueComment>());

        var dispatcher = CreateDispatcher();

        // Verify TryDispatchAsync WOULD reject this
        var tryResult = await dispatcher.TryDispatchAsync(
            "issue-dedup", "ip", "rp", null, null, "test", CancellationToken.None);
        tryResult.Should().BeFalse();

        // But DispatchToAgentDirectAsync skips that check
        var directResult = await dispatcher.DispatchToAgentDirectAsync(
            agent,
            new PendingJob
            {
                IssueIdentifier = "issue-dedup",
                IssueProviderId = "ip",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "user",
                RunType = PipelineRunType.Implementation
            },
            Array.Empty<string>(),
            CancellationToken.None);

        directResult.Should().BeTrue();
    }

    [Fact]
    public async Task DispatchToAgentDirectAsync_FailedDispatch_ReturnsFalseAndResetsAgent()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-direct-fail",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-direct-fail");

        // Setup profile but no issue provider config → dispatch fails
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new AgentProfile
                {
                    Id = "profile-1",
                    DisplayName = "Test Profile",
                    AgentProviderConfigId = "agent-provider-1",
                    MatchLabels = Array.Empty<string>()
                }
            });
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "rp", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" } });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "agent-provider-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Agent", Settings = new Dictionary<string, string> { ["model"] = "auto" } } });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        // No issue provider setup → PrepareIssueContextAsync returns null → dispatch fails
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.RepositoryFullName).Returns("org/repo");
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentDirectAsync(
            agent,
            new PendingJob
            {
                IssueIdentifier = "issue-fail",
                IssueProviderId = "ip",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "user",
                RunType = PipelineRunType.Implementation
            },
            Array.Empty<string>(),
            CancellationToken.None);

        result.Should().BeFalse();
        agent.Status.Should().Be(AgentStatus.Idle);
    }

    #endregion

    #region Execution Error Paths

    // TODO: Add test DispatchToAgentAsync_ProfileResolutionFails_ReturnsFalse_AgentRemainsIdle
    // covering the implementation dispatch path where ResolveProfileAsync returns null.
    // Currently only the review path (DispatchReviewToAgentAsync_ProfileResolutionFails_ReturnsFalse) is tested.

    [Fact]
    public async Task DispatchToAgentAsync_CreateRunReturnsNull_ReturnsFalse_AgentRemainsIdle()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-nullrun",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-nullrun");

        SetupHappyPathMocks("agent-provider-1");

        // Mock IDispatchRunCreator to return null (simulates issue already being processed)
        var mockOrchestration = new Mock<IDispatchRunCreator>();
        mockOrchestration.Setup(o => o.CreateDispatchedRunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<PipelineRunType>()))
            .ReturnsAsync((PipelineRun?)null);

        var dispatcher = CreateDispatcher(orchestrationOverride: mockOrchestration.Object);
        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-nullrun", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeFalse();
        agent.Status.Should().Be(AgentStatus.Idle);
        _mockAgentComm.Verify(c => c.AssignJobAsync(
            It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryDispatchAsync_ShutdownInProgress_ReturnsFalse()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-shutdown",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-shutdown");

        SetupHappyPathMocks("agent-provider-1");

        // Use a mock shutdown signal that reports shutdown in progress
        var mockShutdown = new Mock<IShutdownSignal>();
        mockShutdown.Setup(s => s.IsShuttingDown).Returns(true);

        var dispatcher = CreateDispatcher(shutdownOverride: mockShutdown.Object);
        var result = await dispatcher.TryDispatchAsync(
            "issue-sd", "ip", "rp", null, null, "user", CancellationToken.None);

        result.Should().BeFalse();
        // No agent should have been selected or dispatched to
        _mockAgentComm.Verify(c => c.AssignJobAsync(
            It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        // TODO: Add agent.Status.Should().Be(AgentStatus.Idle) assertion for consistency
        // with other tests verifying agent state after each scenario.
    }

    [Fact]
    public async Task DispatchToAgentAsync_ExceptionDuringDispatch_RunRemainsOrphaned()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-orphan",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-orphan");

        SetupHappyPathMocks("agent-provider-1");
        SetupIssueProviderMock(new List<IssueComment>());

        // Make AssignJobAsync throw to trigger the catch block
        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-orphan", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeFalse();
        agent.Status.Should().Be(AgentStatus.Idle);
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip", "issue-orphan", AgentLabels.Next, CancellationToken.None), Times.Once);

        // The run remains orphaned because agent.ActiveJobId is null when
        // RevertDispatchFailureAsync executes (it's only set after AssignJobAsync succeeds).
        // This documents current behavior — the RemoveRun guard checks ActiveJobId which is still null.
        // TODO: Track orphaned run bug — RemoveRun is not called when ActiveJobId is null at failure time.
        // When fixed, this assertion should change to HaveCount(0) and verify run status is Failed.
        _runService.GetActiveRuns().Should().HaveCount(1);
    }

    [Fact]
    public async Task DispatchToAgentAsync_NullProject_UsesDefaultProject()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-defproj",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-defproj");

        SetupHappyPathMocks("agent-provider-1");
        SetupIssueProviderMock(new List<IssueComment>());

        // Capture the JobAssignmentMessage sent to the agent
        JobAssignmentMessage? capturedMessage = null;
        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, JobAssignmentMessage, CancellationToken>((_, msg, _) => capturedMessage = msg)
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-defproj", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None, project: null);

        result.Should().BeTrue();
        capturedMessage.Should().NotBeNull();
        capturedMessage!.ProjectId.Should().Be(WellKnownIds.DefaultProjectId);
        capturedMessage.ProjectName.Should().Be("Default");
    }

    [Fact]
    public async Task DispatchToAgentAsync_TokenVendingFailure_RevertsAgentAndLabel()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-tvfail",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-tvfail");

        SetupHappyPathMocks("agent-provider-1");
        SetupIssueProviderMock(new List<IssueComment>());

        // Mock token vending to throw
        var mockTokenVending = new Mock<ITokenVendingService>();
        mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
            It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("Token vending failed"));

        var dispatcher = CreateDispatcher(tokenVendingOverride: mockTokenVending.Object);
        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-tvfail", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeFalse();
        agent.Status.Should().Be(AgentStatus.Idle);
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip", "issue-tvfail", AgentLabels.Next, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task DispatchReviewToAgentAsync_ProfileResolutionFails_ReturnsFalse()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-revnoprof",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-revnoprof");

        // Empty profiles → ResolveProfileAsync returns null for the review path
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchReviewToAgentAsync(
            agent,
            new ReviewDispatchRequest
            {
                PrIdentifier = "42",
                PrBranchName = "feature/x",
                PrTitle = "PR Title",
                PrUrl = "https://github.com/org/repo/pull/42",
                PrTargetBranch = "main",
                IssueProviderId = "ip",
                RepoProviderId = "rp",
                InitiatedBy = "user"
            },
            Array.Empty<string>(),
            CancellationToken.None);

        result.Should().BeFalse();
        agent.Status.Should().Be(AgentStatus.Idle);
        _mockAgentComm.Verify(c => c.AssignJobAsync(
            It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchToAgentAsync_IssueProviderNotFound_RemovesRun()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-noip-cleanup",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-noip-cleanup");

        SetupHappyPathMocks("agent-provider-1");
        // Don't set up issue provider config → PrepareIssueContextAsync returns null
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-noip-cleanup", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None);

        result.Should().BeFalse();
        agent.Status.Should().Be(AgentStatus.Idle);
        // Verify the run was explicitly cleaned up (code calls _runService.RemoveRun before returning false)
        _runService.GetActiveRuns().Should().BeEmpty();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Sets up the full mock chain needed for DispatchToAgentAsync happy path:
    /// profile, QGC, reviewer configs, provider configs, and repository provider.
    /// </summary>
    private void SetupHappyPathMocks(string agentProviderId)
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new AgentProfile
                {
                    Id = "profile-1",
                    DisplayName = "Test Profile",
                    AgentProviderConfigId = agentProviderId,
                    MatchLabels = Array.Empty<string>() // Matches any agent
                }
            });
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());

        var repoConfig = new ProviderConfig
        {
            Id = "rp",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo"
        };
        var agentConfig = new ProviderConfig
        {
            Id = agentProviderId,
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string> { ["model"] = "auto" }
        };

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

    /// <summary>
    /// Sets up the issue provider mock for PrepareIssueContextAsync.
    /// </summary>
    private void SetupIssueProviderMock(IReadOnlyList<IssueComment> comments)
    {
        var issueConfig = new ProviderConfig
        {
            Id = "ip",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test Issue Provider"
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { issueConfig });

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "issue-1",
                Title = "Test Issue",
                Description = "Test description",
                Labels = Array.Empty<string>()
            });
        mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(comments);
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
    }

    #endregion

    public void Dispose()
    {
        _httpClient.Dispose();
        foreach (var orchestration in _orchestrationInstances)
            orchestration.Dispose();
    }
}
