using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for AgentHubFacade.TouchLastProgressAsync — throttled DB writes for progress-aware timeout.
/// Validates:
/// - First call writes LastProgressAt to DB
/// - Subsequent calls within throttle window (5 min) are skipped
/// - Calls after throttle window expires write again
/// - Invalid jobId (non-GUID) is no-op
/// - Missing work item is no-op
/// </summary>
public sealed class AgentHubFacadeProgressTrackingTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly AgentHubFacade _facade;

    public AgentHubFacadeProgressTrackingTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"ProgressTrackingTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        var mockSerilogLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockSerilogLogger.Object);
        var runService = new OrchestratorRunService(mockSerilogLogger.Object);
        var dispatcher = new JobDispatcherService(registry, mockSerilogLogger.Object);
        var drainService = new JobQueueDrainService(dispatcher, registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), Mock.Of<IConsolidationDispatcher>(), new ShutdownSignal(), mockSerilogLogger.Object);

        _facade = new AgentHubFacade(
            registry,
            runService,
            dispatcher,
            drainService,
            Mock.Of<IPipelineRunHistoryService>(),
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            NullLogger<AgentHubFacade>.Instance,
            transitionService,
            dbFactory: _dbFactory);
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task TouchLastProgressAsync_FirstCall_WritesToDb()
    {
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId);

        var now = DateTimeOffset.UtcNow;
        await _facade.TouchLastProgressAsync(workItemId.ToString(), now, CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.LastProgressAt.Should().NotBeNull();
        item.LastProgressAt!.Value.Should().BeCloseTo(now, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TouchLastProgressAsync_WithinThrottleWindow_DoesNotWrite()
    {
        var workItemId = Guid.NewGuid();
        var recentProgress = DateTimeOffset.UtcNow.AddMinutes(-2); // 2 min ago — within 5 min throttle
        await InsertWorkItem(workItemId, lastProgressAt: recentProgress);

        var newTimestamp = DateTimeOffset.UtcNow;
        await _facade.TouchLastProgressAsync(workItemId.ToString(), newTimestamp, CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(workItemId);
        // Should NOT have been updated — still the old value
        item!.LastProgressAt.Should().BeCloseTo(recentProgress, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TouchLastProgressAsync_BeyondThrottleWindow_Writes()
    {
        var workItemId = Guid.NewGuid();
        var staleProgress = DateTimeOffset.UtcNow.AddMinutes(-6); // 6 min ago — beyond 5 min throttle
        await InsertWorkItem(workItemId, lastProgressAt: staleProgress);

        var newTimestamp = DateTimeOffset.UtcNow;
        await _facade.TouchLastProgressAsync(workItemId.ToString(), newTimestamp, CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.LastProgressAt.Should().BeCloseTo(newTimestamp, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TouchLastProgressAsync_InvalidJobId_NoOp()
    {
        // Should not throw
        await _facade.TouchLastProgressAsync("not-a-guid", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task TouchLastProgressAsync_NonexistentWorkItem_NoOp()
    {
        var nonexistent = Guid.NewGuid().ToString();
        await _facade.TouchLastProgressAsync(nonexistent, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task TouchLastProgressAsync_NullLastProgressAt_AlwaysWrites()
    {
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, lastProgressAt: null); // No previous progress

        var now = DateTimeOffset.UtcNow;
        await _facade.TouchLastProgressAsync(workItemId.ToString(), now, CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.LastProgressAt.Should().NotBeNull();
        item.LastProgressAt!.Value.Should().BeCloseTo(now, TimeSpan.FromSeconds(2));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task InsertWorkItem(Guid id, DateTimeOffset? lastProgressAt = null)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = "owner/repo#1",
            IssueProviderConfigId = "provider-1",
            Status = WorkItemStatus.Running,
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            DispatchedAt = DateTimeOffset.UtcNow.AddHours(-1),
            TimeoutSeconds = 7200,
            LastProgressAt = lastProgressAt,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersion = entityType.FindProperty("RowVersion");
                if (rowVersion != null)
                {
                    rowVersion.IsConcurrencyToken = false;
                    rowVersion.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var index in entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
