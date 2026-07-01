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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// End-to-end tests for the DB+SignalR dispatch pipeline.
/// Wires real <see cref="DispatchOrchestrationService"/> + real <see cref="SignalRWorkDistributor"/>
/// (with InMemory EF) + real <see cref="OrchestratorRunService"/> to verify the full ID chain:
///
///   PrepareDistributionRequestAsync → DistributeAsync → agent jobId → hub GetRun(jobId) → found
///
/// These tests would have caught the three bugs fixed in this session:
/// 1. Provider configs missing from JobAssignmentMessage
/// 2. RunId mismatch between PipelineRun and WorkItem
/// 3. HeartbeatMonitor orphaning runs with AgentId="pending"
/// </summary>
public sealed class DispatchPipelineEndToEndTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
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

        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-agent-1", "agent-dotnet-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private DispatchOrchestrationService CreateOrchestrationService()
    {
        var orchestration = new PipelineOrchestrationService(
            _mockConfigStore.Object, _mockProviderFactory.Object,
            new IssueDescriptionParser(), new Mock<IAgentPhaseExecutor>().Object,
            new Mock<IQualityGateExecutor>().Object, _mockLogger.Object,
            new Mock<IBrainUpdateService>().Object, new Mock<IPipelineRunHistoryService>().Object,
            _runService);

        return new DispatchOrchestrationService(
            new DispatchInfrastructure(
                _mockTokenVending.Object, _mockProviderFactory.Object,
                _mockLabelSwapper.Object,
                new DispatchResolutionService(new ProfileResolver(), new QualityGateResolver(), new ReviewerResolver(), _mockConfigStore.Object, _mockLogger.Object)),
            orchestration,
            _runService, _mockLogger.Object);
    }

    private SignalRWorkDistributor CreateDistributor()
    {
        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        return new SignalRWorkDistributor(_dbFactory, _mockAgentComm.Object, transitionService,
            _mockResolver.Object, _runService, new Mock<IProjectStore>().Object, new Mock<ILabelSwapper>().Object, NullLogger<SignalRWorkDistributor>.Instance);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // END-TO-END: PrepareDistributionRequestAsync → DistributeAsync → consistent IDs
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullDispatch_RunIdChain_AgentJobIdMatchesPipelineRunIdAndWorkItemId()
    {
        // Arrange
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        // Act: prepare (creates PipelineRun in OrchestratorRunService)
        var request = await orchestration.PrepareDistributionRequestAsync(
            "org/repo#42", "issue-1", "repo-1", null, null,
            "loop", TestProject, ct: CancellationToken.None);
        request.Should().NotBeNull();
        request!.RunId.Should().NotBeNullOrEmpty("orchestration must set RunId from PipelineRun.RunId");

        // Act: distribute (creates WorkItem in DB, sends to agent)
        var result = await distributor.DistributeAsync(request, CancellationToken.None);
        result.Success.Should().BeTrue();

        // Assert: all three IDs are the same
        var runId = request.RunId!;
        result.WorkItemId.Should().Be(runId, "WorkItem ID must match PipelineRun.RunId");

        // Assert: the agent received a message with JobId = runId
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-agent-1",
            It.Is<JobAssignmentMessage>(m => m.JobId == runId), It.IsAny<CancellationToken>()));

        // Assert: hub can find the run by jobId (simulates RequestTokenRefresh lookup)
        var foundRun = _runService.GetRun(runId);
        foundRun.Should().NotBeNull("hub must find PipelineRun by the same jobId the agent uses");
        foundRun!.IssueIdentifier.Should().Be("org/repo#42");
    }

    [Fact]
    public async Task FullDispatch_ProviderConfigsIncluded_InJobAssignmentMessage()
    {
        // Arrange
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        // Act
        var request = await orchestration.PrepareDistributionRequestAsync(
            "org/repo#42", "issue-1", "repo-1", null, null,
            "loop", TestProject, ct: CancellationToken.None);

        request!.ProviderConfigs.Should().NotBeNullOrEmpty("orchestration must resolve provider configs");
        request.ProviderConfigs!.Should().Contain(c => c.Id == "repo-1", "repo config must be included");
        request.ProviderConfigs!.Should().Contain(c => c.Id == "agent-1", "agent config must be included");

        var result = await distributor.DistributeAsync(request, CancellationToken.None);
        result.Success.Should().BeTrue();

        // Verify agent received ProviderConfigs in the message
        _mockAgentComm.Verify(c => c.AssignJobAsync(It.IsAny<string>(),
            It.Is<JobAssignmentMessage>(m =>
                m.ProviderConfigs.Any(p => p.Id == "repo-1") &&
                m.ProviderConfigs.Any(p => p.Id == "agent-1")),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task FullDispatch_AgentIdUpdated_FromNullToActualAgent()
    {
        // Arrange
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        // Act
        var request = await orchestration.PrepareDistributionRequestAsync(
            "org/repo#42", "issue-1", "repo-1", null, null,
            "loop", TestProject, ct: CancellationToken.None);
        request.Should().NotBeNull();

        // Before distribution, run has agentId=null (dispatch window)
        var run = _runService.GetRun(request!.RunId!);
        run.Should().NotBeNull();
        run!.AgentId.Should().BeNull();

        // Act: distribute
        var result = await distributor.DistributeAsync(request, CancellationToken.None);
        result.Success.Should().BeTrue();

        // After distribution, run.AgentId updated to actual agent
        run.AgentId.Should().Be("agent-dotnet-1",
            "distributor must update run.AgentId from null to the resolved agent");
    }

    [Fact]
    public async Task FullDispatch_WorkItemInDb_HasCorrectStateAndAgentId()
    {
        // Arrange
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        // Act
        var request = await orchestration.PrepareDistributionRequestAsync(
            "org/repo#42", "issue-1", "repo-1", null, null,
            "loop", TestProject, ct: CancellationToken.None);
        var result = await distributor.DistributeAsync(request!, CancellationToken.None);

        // Assert: WorkItem in DB has correct state
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        var workItem = await db.WorkItems.FindAsync(Guid.Parse(result.WorkItemId!));
        workItem.Should().NotBeNull();
        workItem!.Status.Should().Be(WorkItemStatus.Dispatched);
        workItem.AssignedAgentId.Should().Be("agent-dotnet-1");
        workItem.IssueIdentifier.Should().Be("org/repo#42");
    }

    [Fact]
    public async Task FullDispatch_TokenRefreshLookup_Succeeds()
    {
        // This test simulates what happens when the agent calls RequestTokenRefresh:
        // hub does _runService.GetRun(jobId) where jobId = the WorkItem ID sent to agent.
        // If RunId passthrough is broken, this returns null and throws HubException.
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        var request = await orchestration.PrepareDistributionRequestAsync(
            "org/repo#42", "issue-1", "repo-1", null, null,
            "loop", TestProject, ct: CancellationToken.None);
        var result = await distributor.DistributeAsync(request!, CancellationToken.None);

        // The agent's jobId (what it passes to hub methods) is the WorkItemId
        var agentJobId = result.WorkItemId!;

        // Simulate hub looking up the run — this is what RequestTokenRefresh does
        var run = _runService.GetRun(agentJobId);
        run.Should().NotBeNull("agent's jobId must resolve to the PipelineRun in OrchestratorRunService");
        run!.RepoProviderConfigId.Should().Be("repo-1");
    }

    [Fact]
    public async Task FullDispatch_HeartbeatMonitor_DoesNotOrphanFreshRun()
    {
        // Verifies that after dispatch, the run has a real AgentId,
        // so HeartbeatMonitor won't orphan it during its next sweep.
        var orchestration = CreateOrchestrationService();
        var distributor = CreateDistributor();

        var request = await orchestration.PrepareDistributionRequestAsync(
            "org/repo#42", "issue-1", "repo-1", null, null,
            "loop", TestProject, ct: CancellationToken.None);
        await distributor.DistributeAsync(request!, CancellationToken.None);

        // Verify: all active runs have real agent IDs (not null or "pending")
        var activeRuns = _runService.GetActiveRuns();
        foreach (var run in activeRuns)
        {
            run.AgentId.Should().NotBeNullOrEmpty(
                "after distribution, runs must have a resolved agent ID so HeartbeatMonitor doesn't orphan them");
        }
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
            "org/repo#42", "issue-1", "repo-1", null, null,
            "loop", TestProject, ct: CancellationToken.None);
        request.Should().NotBeNull();

        var run = _runService.GetRun(request!.RunId!);
        run.Should().NotBeNull();
        run!.AgentId.Should().BeNull("run is in dispatch window — no agent assigned yet");

        // Step 2: HeartbeatMonitor fires during the dispatch window
        var registry = new AgentRegistryService(_mockLogger.Object);
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        var dispatcher = new JobDispatcherService(registry, _mockLogger.Object);
        var monitor = new HeartbeatMonitorService(
            registry, _runService, mockHistoryService.Object, dispatcher,
            _mockLabelSwapper.Object, _mockConfigStore.Object, _mockLogger.Object);

        await monitor.SweepAsync(CancellationToken.None);

        // Step 3: Verify the run was NOT orphaned
        var survivedRun = _runService.GetRun(request.RunId!);
        survivedRun.Should().NotBeNull("Phase 3 must skip runs with null AgentId — they're in the dispatch window");
        survivedRun!.CurrentStep.Should().NotBe(PipelineStep.Failed);

        // Step 4: Distribution still works after the sweep
        var result = await distributor.DistributeAsync(request, CancellationToken.None);
        result.Success.Should().BeTrue();
        run.AgentId.Should().Be("agent-dotnet-1");
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
