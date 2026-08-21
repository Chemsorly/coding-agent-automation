using AwesomeAssertions;
using CodingAgentWebUI.Hub;
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
/// Tests for AgentHubFacade exception-catch paths and edge cases:
/// - ThrowingDbContextFactory causes swallowed exceptions (no propagation)
/// - Empty IssueIdentifier returns null from GetWorkItemIssueMetadataAsync
/// - GetWorkItemRetryCountAsync returns 0 when WorkItemTransitionService throws
/// - Signal() is a no-op
/// </summary>
public sealed class AgentHubFacadeExceptionPathTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;

    public AgentHubFacadeExceptionPathTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"ExceptionPathTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new TestPipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static AgentHubFacade BuildFacade(
        IDbContextFactory<PipelineDbContext>? dbFactory = null,
        WorkItemTransitionService? transitionService = null)
    {
        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDeduplicationGuardService(registry, mockLogger.Object);

        return new AgentHubFacade(new AgentHubFacadeDependencies(
            registry,
            runService,
            dispatcher,
            Mock.Of<IPipelineRunHistoryService>(),
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            NullLogger<AgentHubFacadeDependencies>.Instance,
            WorkItemTransition: transitionService,
            DbFactory: dbFactory));
    }

    private async Task InsertWorkItemWithEmptyIssueIdentifier(Guid id)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = "",
            IssueProviderConfigId = "provider-1",
            Status = WorkItemStatus.Running,
            AgentSelector = "dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
    }

    // ── TouchLastProgressAsync ────────────────────────────────────────────

    [Fact]
    public async Task TouchLastProgressAsync_DbThrows_DoesNotPropagateException()
    {
        var facade = BuildFacade(dbFactory: new ThrowingDbContextFactory());

        var act = async () =>
            await facade.TouchLastProgressAsync(
                new JobId(Guid.NewGuid().ToString()),
                DateTimeOffset.UtcNow,
                CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── GetWorkItemIssueMetadataAsync ─────────────────────────────────────

    [Fact]
    public async Task GetWorkItemIssueMetadataAsync_DbThrows_ReturnsNull()
    {
        var facade = BuildFacade(dbFactory: new ThrowingDbContextFactory());

        var result = await facade.GetWorkItemIssueMetadataAsync(
            new JobId(Guid.NewGuid().ToString()),
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkItemIssueMetadataAsync_EmptyIssueIdentifier_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        await InsertWorkItemWithEmptyIssueIdentifier(workItemId);

        var facade = BuildFacade(dbFactory: _dbFactory);

        var result = await facade.GetWorkItemIssueMetadataAsync(
            new JobId(workItemId.ToString()),
            CancellationToken.None);

        result.Should().BeNull();
    }

    // ── GetWorkItemRetryCountAsync ────────────────────────────────────────

    [Fact]
    public async Task GetWorkItemRetryCountAsync_ServiceThrows_ReturnsZero()
    {
        // WorkItemTransitionService backed by ThrowingDbContextFactory will throw on any DB call
        var throwingTransitionService = new WorkItemTransitionService(
            new ThrowingDbContextFactory(),
            NullLogger<WorkItemTransitionService>.Instance);

        var facade = BuildFacade(transitionService: throwingTransitionService);

        var result = await facade.GetWorkItemRetryCountAsync(
            new JobId(Guid.NewGuid().ToString()),
            CancellationToken.None);

        result.Should().Be(0);
    }

    // ── GetWorkItemProviderConfigIdsAsync ─────────────────────────────────

    [Fact]
    public async Task GetWorkItemProviderConfigIdsAsync_DbThrows_ReturnsNull()
    {
        var facade = BuildFacade(dbFactory: new ThrowingDbContextFactory());

        var result = await facade.GetWorkItemProviderConfigIdsAsync(
            new JobId(Guid.NewGuid().ToString()),
            CancellationToken.None);

        result.Should().BeNull();
    }

    // ── Signal ────────────────────────────────────────────────────────────

    [Fact]
    public void Signal_IsNoOp_DoesNotThrow()
    {
        var facade = BuildFacade();

        var act = () => facade.Signal();

        act.Should().NotThrow();
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    /// <summary>
    /// DbContextFactory that always throws to exercise exception catch paths.
    /// </summary>
    private sealed class ThrowingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext()
            => throw new InvalidOperationException("Simulated DB failure");

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated DB failure");
    }

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
