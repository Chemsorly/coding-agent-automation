using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// End-to-end tests for the K8s dispatch pipeline.
/// Wires real <see cref="DispatchOrchestrationService"/> + real <see cref="KubernetesWorkDistributor"/>
/// (with InMemory EF) + real <see cref="OrchestratorRunService"/> to verify the full ID chain:
///
///   PrepareDistributionRequestAsync → DistributeAsync → WorkItemId → DB state
///
/// These tests verify key dispatch invariants:
/// 1. Provider configs resolved correctly
/// 2. RunId consistent between PipelineRun and WorkItem
/// 3. HeartbeatMonitor does not orphan dispatch-window runs
/// </summary>
public sealed class DispatchPipelineEndToEndTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private static readonly ProviderConfig RepoConfig = new()
    {
        Id = "repo-1",
        DisplayName = "Test Repo",
        ProviderType = "GitHub",
        Kind = ProviderKind.Repository,
        RequiredLabels = ["dotnet"],
        Settings = new Dictionary<string, string>
        {
            ["owner"] = "org",
            ["repo"] = "test-repo",
            ["privateKeyBase64"] = "dGVzdA=="
        }
    };

    private static readonly ProviderConfig AgentConfig = new()
    {
        Id = "agent-1",
        DisplayName = "Test Agent",
        ProviderType = "KiroCli",
        Kind = ProviderKind.Agent
    };

    private static readonly AgentProfile TestProfile = new()
    {
        Id = "profile-1",
        DisplayName = "Test Profile",
        AgentProviderConfigId = "agent-1",
        Enabled = true,
        MatchLabels = ["dotnet"],
        McpServers = []
    };

    private static readonly PipelineProject TestProject = new()
    {
        Id = "proj-1",
        Name = "TestProject",
        Enabled = true
    };

    public DispatchPipelineEndToEndTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DispatchE2E-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        SetupMocks();
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    private void SetupMocks()
    {
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { TestProfile });
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" });
        _mockConfigStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { RepoConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { AgentConfig });
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RepoConfig);
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync("agent-1", ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentConfig);

        var issueConfig = new ProviderConfig { Id = "issue-1", DisplayName = "Issues", ProviderType = "GitHub", Kind = ProviderKind.Issue };
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync("issue-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.GetIssueAsync("org/repo#42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "org/repo#42", Title = "Test", Description = "Do thing", Labels = [] });
        mockIssueProvider.Setup(p => p.ListCommentsAsync("org/repo#42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueComment>());
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockIssueProvider.Object);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.RepositoryFullName).Returns("org/test-repo");
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(mockRepoProvider.Object);

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>((c, _, _, _) => Task.FromResult(c));
    }

    private DispatchOrchestrationService CreateOrchestrationService()
    {
        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockConfigStore.Object,
            providerFactory: _mockProviderFactory.Object,
            lifecycle: new PipelineRunLifecycleService(new Mock<IPipelineRunHistoryService>().Object, _runService, _mockLogger.Object));

        return new DispatchOrchestrationService(
            new DispatchOrchestrationServiceDependencies(
                new DispatchInfrastructure(
                    _mockTokenVending.Object, _mockProviderFactory.Object,
                    _mockLabelService.Object,
                    new DispatchResolutionService(new ProfileResolver(), new QualityGateResolver(), new ReviewerResolver(), _mockConfigStore.Object, _mockLogger.Object)),
                runCreator,
                _runService,
                new Mock<IWorkDistributor>().Object,
                _mockConfigStore.Object,
                _mockConfigStore.Object,
                _mockConfigStore.Object),
            _mockLogger.Object);
    }

    private KubernetesWorkDistributor CreateDistributor()
    {
        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        return new KubernetesWorkDistributor(_dbFactory, transitionService, NullLogger<KubernetesWorkDistributor>.Instance);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // END-TO-END: PrepareDistributionRequestAsync → DistributeAsync → consistent IDs
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullDispatch_RunIdChain_WorkItemIdMatchesPipelineRunId()
    {
        // Arrange
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        // Act: prepare (creates PipelineRun in OrchestratorRunService)
        var request = await orchestration.PrepareDistributionRequestAsync(
            new ImplementationDispatchOrchestrationRequest
            {
                IssueIdentifier = "org/repo#42",
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                InitiatedBy = "loop",
                Project = TestProject
            },
            CancellationToken.None);
        request.Should().NotBeNull();
        request!.RunId.Should().NotBeNullOrEmpty("orchestration must set RunId from PipelineRun.RunId");

        // Act: distribute (creates WorkItem in DB as Pending)
        var result = await distributor.DistributeAsync(request, CancellationToken.None);
        result.Success.Should().BeTrue();

        // Assert: WorkItem ID matches the PipelineRun RunId
        var runId = request.RunId!;
        result.WorkItemId.Should().Be(runId, "WorkItem ID must match PipelineRun.RunId");

        // Assert: hub can find the run by jobId
        var foundRun = _runService.GetRun(runId);
        foundRun.Should().NotBeNull("hub must find PipelineRun by the same jobId the agent uses");
        foundRun!.IssueIdentifier.Value.Should().Be("org/repo#42");
    }

    [Fact]
    public async Task FullDispatch_ProviderConfigsIncluded_InDistributionRequest()
    {
        // Arrange
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        // Act
        var request = await orchestration.PrepareDistributionRequestAsync(
            new ImplementationDispatchOrchestrationRequest
            {
                IssueIdentifier = "org/repo#42",
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                InitiatedBy = "loop",
                Project = TestProject
            },
            CancellationToken.None);

        request!.ProviderConfigs.Should().NotBeNullOrEmpty("orchestration must resolve provider configs");
        request.ProviderConfigs!.Should().Contain(c => c.Id == "repo-1", "repo config must be included");
        request.ProviderConfigs!.Should().Contain(c => c.Id == "agent-1", "agent config must be included");

        var result = await distributor.DistributeAsync(request, CancellationToken.None);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task FullDispatch_WorkItemInDb_HasCorrectState()
    {
        // Arrange
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        // Act
        var request = await orchestration.PrepareDistributionRequestAsync(
            new ImplementationDispatchOrchestrationRequest
            {
                IssueIdentifier = "org/repo#42",
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                InitiatedBy = "loop",
                Project = TestProject
            },
            CancellationToken.None);
        var result = await distributor.DistributeAsync(request!, CancellationToken.None);

        // Assert: WorkItem in DB has correct state (K8s queues as Pending)
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        var workItem = await db.WorkItems.FindAsync(Guid.Parse(result.WorkItemId!));
        workItem.Should().NotBeNull();
        workItem!.Status.Should().Be(WorkItemStatus.Pending);
        workItem.IssueIdentifier.Should().Be("org/repo#42");
        workItem.AssignedAgentId.Should().BeNull("K8s defers agent assignment to the pod scheduler — AssignedAgentId is set when the pod connects, not at dispatch time");
    }

    [Fact]
    public async Task FullDispatch_HeartbeatMonitor_DoesNotOrphanRunDuringDispatchWindow()
    {
        // This is the core race-condition test: HeartbeatMonitor fires DURING the dispatch
        // window (after PrepareDistributionRequestAsync creates the run with AgentId=null,
        // but BEFORE DistributeAsync assigns a real agent). The run must survive.
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        // Step 1: Create the run (AgentId=null during dispatch window)
        var request = await orchestration.PrepareDistributionRequestAsync(
            new ImplementationDispatchOrchestrationRequest
            {
                IssueIdentifier = "org/repo#42",
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                InitiatedBy = "loop",
                Project = TestProject
            },
            CancellationToken.None);
        request.Should().NotBeNull();

        var run = _runService.GetRun(request!.RunId!);
        run.Should().NotBeNull();
        run!.AgentId.Should().BeNull("run is in dispatch window — no agent assigned yet");

        // Step 2: HeartbeatMonitor fires during the dispatch window
        var registry = new AgentRegistryService(_mockLogger.Object);
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        var monitor = new HeartbeatMonitorService(new HeartbeatMonitorDependencies(
            registry, _runService, mockHistoryService.Object,
            _mockConfigStore.Object, _mockLogger.Object,
            LifecycleManager: new Mock<IRunLifecycleManager>().Object));

        await monitor.SweepAsync(CancellationToken.None);

        // Step 3: Verify the run was NOT orphaned
        var survivedRun = _runService.GetRun(request.RunId!);
        survivedRun.Should().NotBeNull("Phase 3 must skip runs with null AgentId — they're in the dispatch window");
        survivedRun!.CurrentStep.Should().NotBe(PipelineStep.Failed);

        // Step 4: Distribution still works after the sweep
        var result = await distributor.DistributeAsync(request, CancellationToken.None);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task FullDispatch_LabelConfirm_SwapsToInProgress()
    {
        // Verify label swap happens after ConfirmDistributionLabelAsync, not before.
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        var request = await orchestration.PrepareDistributionRequestAsync(
            new ImplementationDispatchOrchestrationRequest
            {
                IssueIdentifier = "org/repo#42",
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                InitiatedBy = "loop",
                Project = TestProject
            },
            CancellationToken.None);

        // After prepare: no label swap happened
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.InProgress, It.IsAny<CancellationToken>()),
            Times.Never);

        // Distribute (K8s queues as Pending)
        var result = await distributor.DistributeAsync(request!, CancellationToken.None);
        result.Success.Should().BeTrue();

        // Confirm label
        await orchestration.ConfirmDistributionLabelAsync(request!, CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync("issue-1", "org/repo#42", AgentLabels.InProgress, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test infrastructure
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
                var indexesToRemove = entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList();
                foreach (var index in indexesToRemove)
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
