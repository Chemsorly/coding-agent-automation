using System.Text.Json;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.IntegrationTests.DbMode;

/// <summary>
/// Integration tests verifying that active pipeline run rehydration works correctly
/// with real services: InMemory EF + real OrchestratorRunService + HeartbeatMonitorService sweep.
/// Simulates orchestrator restart scenarios.
/// </summary>
public sealed class ActiveRunRehydrationIntegrationTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly OrchestratorRunService _runService;
    private readonly AgentRegistryService _registry;
    private readonly Mock<ILogger> _mockLogger;

    public ActiveRunRehydrationIntegrationTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"RehydrationIntegration-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _mockLogger = new Mock<ILogger>();
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _registry = new AgentRegistryService(_mockLogger.Object);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task RestartSimulation_RunsVisibleImmediately()
    {
        // Arrange — simulate pre-existing active runs in DB
        var run1Id = Guid.NewGuid().ToString();
        var run2Id = Guid.NewGuid().ToString();

        await InsertActiveWorkItem(run1Id, "owner/repo#100", WorkItemStatus.Dispatched, "agent-1");
        await InsertActiveWorkItem(run2Id, "owner/repo#101", WorkItemStatus.Running, "agent-2");

        // Act — rehydrate into a fresh RunService (simulating restart)
        await RehydrateAsync();

        // Assert — runs are visible immediately
        _runService.GetActiveRuns().Should().HaveCount(2);
        _runService.HasActiveRuns.Should().BeTrue();
        _runService.ActiveRunCount.Should().Be(2);

        _runService.GetRun(run1Id).Should().NotBeNull();
        _runService.GetRun(run2Id).Should().NotBeNull();
    }

    [Fact]
    public async Task HeartbeatMonitor_DoesNotFail_RehydratedRuns_WithNullAgentId()
    {
        // Arrange — rehydrate a run (AgentId will be null)
        var runId = Guid.NewGuid().ToString();
        await InsertActiveWorkItem(runId, "owner/repo#200", WorkItemStatus.Running, "agent-3");
        await RehydrateAsync();

        // Verify precondition: rehydrated run has null AgentId
        var rehydratedRun = _runService.GetRun(runId)!;
        rehydratedRun.AgentId.Should().BeNull();

        // Create HeartbeatMonitor with real services
        var dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        var mockLabelSwapper = new Mock<ILabelSwapper>();
        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var monitor = new HeartbeatMonitorService(
            _registry,
            _runService,
            mockHistoryService.Object,
            dispatcher,
            mockLabelSwapper.Object,
            mockConfigStore.Object,
            _mockLogger.Object);

        // Act — run a sweep (Phase 3 checks for orphaned runs)
        await monitor.SweepAsync(CancellationToken.None);

        // Assert — run should NOT be failed (Phase 3 skips null AgentId)
        _runService.GetRun(runId).Should().NotBeNull();
        _runService.GetActiveRuns().Should().HaveCount(1);
        mockHistoryService.Verify(h => h.AddRunToHistory(It.IsAny<PipelineRun>()), Times.Never);
    }

    [Fact]
    public async Task PostgresActiveRunQueryService_ReturnsEnrichedResults_AfterRehydration()
    {
        // Arrange — insert work items and rehydrate
        var runId = Guid.NewGuid().ToString();
        await InsertActiveWorkItem(runId, "owner/repo#300", WorkItemStatus.Running, "agent-4");
        await RehydrateAsync();

        // Create PostgresActiveRunQueryService with real run service
        var queryService = new PostgresActiveRunQueryService(_dbFactory, _runService);

        // Act
        var results = await queryService.GetActiveRunsAsync();

        // Assert — should have the run with enriched CurrentStep from in-memory state
        results.Should().HaveCount(1);
        var result = results[0];
        result.RunId.Should().Be(runId);
        result.IssueIdentifier.Should().Be("owner/repo#300");
        result.CurrentStep.Should().Be(PipelineStep.GeneratingCode); // From rehydrated in-memory state
    }

    [Fact]
    public async Task AgentReconnect_UpdatesExistingRehydratedRun()
    {
        // Arrange — rehydrate a run
        var runId = Guid.NewGuid().ToString();
        await InsertActiveWorkItem(runId, "owner/repo#400", WorkItemStatus.Running, "agent-5");
        await RehydrateAsync();

        // Verify initial state
        var run = _runService.GetRun(runId)!;
        run.AgentId.Should().BeNull();

        // TODO: This test directly mutates the run object rather than exercising the actual
        // AgentHub.RegisterAgent path. It validates ConcurrentDictionary reference semantics
        // (in-place mutation), but if the implementation changes to store copies or use
        // immutable records, this test would still pass despite real behavior breaking.
        // Consider wiring up an AgentHub instance for a more realistic reconnection test.

        // Act — simulate what AgentHub does when agent reconnects:
        // It calls GetRun, finds existing run, and updates it
        var existingRun = _runService.GetRun(runId);
        existingRun.Should().NotBeNull();
        existingRun!.AgentId = "agent-5";
        existingRun.CurrentStep = PipelineStep.ReviewingCode;

        // Assert — run is updated in-place (same reference in ConcurrentDictionary)
        var updatedRun = _runService.GetRun(runId)!;
        updatedRun.AgentId.Should().Be("agent-5");
        updatedRun.CurrentStep.Should().Be(PipelineStep.ReviewingCode);
        _runService.GetActiveRuns().Should().HaveCount(1);
    }

    // ── Helpers ──

    private async Task RehydrateAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var activeItems = await db.WorkItems
            .AsNoTracking()
            .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                     && w.TaskType != WorkItemTaskType.Consolidation)
            .ToListAsync();

        foreach (var item in activeItems)
        {
            if (string.IsNullOrEmpty(item.Payload)) continue;

            try
            {
                var request = JsonSerializer.Deserialize<JobDistributionRequest>(
                    item.Payload, PipelineJsonOptions.Default);
                if (request is null || string.IsNullOrEmpty(request.RunId)) continue;

                var initialStep = item.Status == WorkItemStatus.Running
                    ? PipelineStep.GeneratingCode
                    : PipelineStep.Created;

                var run = PipelineRunFactory.FromDistributionRequest(
                    request, agentId: null, initialStep,
                    startedAt: item.DispatchedAt ?? item.CreatedAt);
                _runService.AddRun(run);
            }
            catch (JsonException)
            {
                // Skip corrupt payloads
            }
        }
    }

    private async Task InsertActiveWorkItem(string runId, string issueId, WorkItemStatus status, string agentId)
    {
        // TODO: Set DispatchedAt to a past timestamp (e.g., DateTimeOffset.UtcNow.AddHours(-2)) and assert
        // StartedAtOffset on rehydrated runs. Currently DispatchedAt is not set, so only the
        // `?? item.CreatedAt` fallback is exercised, and no test asserts the timestamp propagation (BUG-08).
        var request = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 1800,
            RunId = runId,
            RunType = PipelineRunType.Implementation,
            IssueDetail = new IssueDetail
            {
                Identifier = issueId,
                Title = $"Issue {issueId}",
                Description = "",
                Labels = []
            }
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.Parse(runId),
            IssueIdentifier = issueId,
            IssueProviderConfigId = "ip-1",
            Status = status,
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 1800,
            Payload = payload,
            AssignedAgentId = agentId
        });
        await db.SaveChangesAsync();
    }

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
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
