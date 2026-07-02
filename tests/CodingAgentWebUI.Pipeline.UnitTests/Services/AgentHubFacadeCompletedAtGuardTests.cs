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
/// Tests that AgentHubFacade.TransitionWorkItemAsync only sets CompletedAt on terminal statuses.
/// Validates fix for Issue #9: CompletedAt was set on ALL transitions including intermediate ones.
/// </summary>
public sealed class AgentHubFacadeCompletedAtGuardTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly AgentHubFacade _facade;

    public AgentHubFacadeCompletedAtGuardTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"CompletedAtGuardTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new TestDbContextFactory(_dbOptions);
        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        var mockSerilogLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockSerilogLogger.Object);
        var runService = new OrchestratorRunService(mockSerilogLogger.Object);
        var dispatcher = new JobDispatcherService(registry, mockSerilogLogger.Object);
        var drainService = new JobQueueDrainService(dispatcher, registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), new ConsolidationQueueService(mockSerilogLogger.Object),
            Mock.Of<IConsolidationService>(), Mock.Of<IConsolidationDispatcher>(), new ShutdownSignal(), mockSerilogLogger.Object);

        _facade = new AgentHubFacade(
            registry,
            runService,
            dispatcher,
            drainService,
            Mock.Of<IPipelineRunHistoryService>(),
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            NullLogger<AgentHubFacade>.Instance,
            transitionService);
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task TransitionToRunning_DoesNotSetCompletedAt()
    {
        // Arrange: create a work item in Dispatched status
        var workItemId = Guid.NewGuid();
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#1",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Dispatched,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Act: transition to Running (non-terminal)
        await _facade.TransitionWorkItemAsync(workItemId.ToString(), WorkItemStatus.Running, CancellationToken.None);

        // Assert: CompletedAt should NOT be set
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Running);
            item.CompletedAt.Should().BeNull();
        }
    }

    [Fact]
    public async Task TransitionToSucceeded_SetsCompletedAt()
    {
        // Arrange: create a work item in Running status
        var workItemId = Guid.NewGuid();
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#2",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Act: transition to Succeeded (terminal)
        await _facade.TransitionWorkItemAsync(workItemId.ToString(), WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert: CompletedAt should be set
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
            item.CompletedAt.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task TransitionToFailed_SetsCompletedAt()
    {
        // Arrange: work item in Running status
        var workItemId = Guid.NewGuid();
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#3",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Act
        await _facade.TransitionWorkItemAsync(workItemId.ToString(), WorkItemStatus.Failed, CancellationToken.None);

        // Assert
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
            item.CompletedAt.Should().NotBeNull();
        }
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

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

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
