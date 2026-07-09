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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Tests for <see cref="AgentHubFacade.TransitionWorkItemAsync"/> covering:
/// - Invalid GUID early return
/// - Null WorkItemTransitionService early return
/// - Direct transition success
/// - Two-step transition (Dispatched → Running → terminal)
/// - Already-terminal rejection
/// - CompletedAt set for terminal statuses
/// </summary>
public sealed class AgentHubFacadeTransitionTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly AgentHubFacade _facade;

    public AgentHubFacadeTransitionTests()
    {
        var dbName = $"FacadeTransition-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(
            _dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDispatcherService(registry, mockLogger.Object);
        var drainService = new JobQueueDrainService(
            dispatcher, registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), Mock.Of<IConsolidationDispatcher>(),
            new ShutdownSignal(), mockLogger.Object);

        _facade = new AgentHubFacade(
            registry, runService, dispatcher, drainService,
            Mock.Of<IPipelineRunHistoryService>(),
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            NullLogger<AgentHubFacade>.Instance,
            workItemTransition: _transitionService,
            dbFactory: _dbFactory);
    }

    public void Dispose()
    {
        // InMemory DB cleaned up when options go out of scope
    }

    private async Task<Guid> SeedWorkItem(WorkItemStatus initialStatus = WorkItemStatus.Pending)
    {
        var id = Guid.NewGuid();
        await using var db = _dbFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = "org/repo#1",
            IssueProviderConfigId = "ip-1",
            Status = initialStatus,
            AgentSelector = "dotnet",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ── Invalid inputs ───────────────────────────────────────────────────

    [Fact]
    public async Task TransitionWorkItemAsync_InvalidGuid_ReturnsWithoutAction()
    {
        await _facade.TransitionWorkItemAsync("not-a-guid", WorkItemStatus.Succeeded, CancellationToken.None);
        // No exception thrown — method completed silently
    }

    [Fact]
    public async Task TransitionWorkItemAsync_NullTransitionService_ReturnsWithoutAction()
    {
        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDispatcherService(registry, mockLogger.Object);
        var drainService = new JobQueueDrainService(
            dispatcher, registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), Mock.Of<IConsolidationDispatcher>(),
            new ShutdownSignal(), mockLogger.Object);

        var facadeWithoutTransition = new AgentHubFacade(
            registry, runService, dispatcher, drainService,
            Mock.Of<IPipelineRunHistoryService>(),
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            NullLogger<AgentHubFacade>.Instance);

        var id = Guid.NewGuid();
        await facadeWithoutTransition.TransitionWorkItemAsync(
            id.ToString(), WorkItemStatus.Succeeded, CancellationToken.None);
    }

    // ── Direct transition success ────────────────────────────────────────

    [Fact]
    public async Task TransitionWorkItemAsync_DirectTransition_Succeeds()
    {
        var id = await SeedWorkItem(WorkItemStatus.Dispatched);

        await _facade.TransitionWorkItemAsync(id.ToString(), WorkItemStatus.Running, CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Running);
    }

    // ── Two-step transition ──────────────────────────────────────────────

    [Fact]
    public async Task TransitionWorkItemAsync_TwoStep_DispatchedToSucceeded_ViaRunning()
    {
        var id = await SeedWorkItem(WorkItemStatus.Dispatched);

        await _facade.TransitionWorkItemAsync(id.ToString(), WorkItemStatus.Succeeded, CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Succeeded);
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TransitionWorkItemAsync_TwoStep_DispatchedToCancelled_ViaRunning()
    {
        var id = await SeedWorkItem(WorkItemStatus.Dispatched);

        await _facade.TransitionWorkItemAsync(id.ToString(), WorkItemStatus.Cancelled, CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Cancelled);
        item.CompletedAt.Should().NotBeNull();
    }

    // ── Already terminal ─────────────────────────────────────────────────

    [Fact]
    public async Task TransitionWorkItemAsync_AlreadyTerminal_DoesNotThrow()
    {
        var id = await SeedWorkItem(WorkItemStatus.Succeeded);

        await _facade.TransitionWorkItemAsync(id.ToString(), WorkItemStatus.Failed, CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Succeeded);
    }

    // ── CompletedAt is set for terminal statuses ─────────────────────────

    [Fact]
    public async Task TransitionWorkItemAsync_TerminalStatus_SetsCompletedAt()
    {
        var id = await SeedWorkItem(WorkItemStatus.Running);

        await _facade.TransitionWorkItemAsync(id.ToString(), WorkItemStatus.Failed, CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.CompletedAt.Should().NotBeNull();
        item.CompletedAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── Helper ───────────────────────────────────────────────────────────

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
    }
}
