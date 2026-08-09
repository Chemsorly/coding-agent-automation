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
/// Tests for AgentHubFacade methods that were changed by the JobId migration and remain
/// uncovered: GetWorkItemRetryCountAsync, RequeueWorkItemAsync, GetWorkItemProviderConfigIdsAsync,
/// TouchLastProgressAsync, GetWorkItemIssueMetadataAsync, and Signal with PendingDrainService.
/// Uses in-memory SQLite for DB-dependent tests.
/// </summary>
public sealed class AgentHubFacadeJobIdMethodsTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly AgentHubFacade _facade;

    public AgentHubFacadeJobIdMethodsTests()
    {
        var dbName = $"FacadeJobIdMethods-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(
            _dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDeduplicationGuardService(registry, mockLogger.Object);
        var drainService = new JobQueueDrainService(
            new JobQueueDrainDependencies(dispatcher, registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), Mock.Of<IConsolidationDispatchService>(),
            new ShutdownSignal(), mockLogger.Object));

        _facade = new AgentHubFacade(
            registry, runService, dispatcher, drainService,
            Mock.Of<IPipelineRunHistoryService>(),
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            NullLogger<AgentHubFacadeDependencies>.Instance,
            workItemTransition: _transitionService,
            dbFactory: _dbFactory);
    }

    public void Dispose() { }

    private async Task<Guid> SeedWorkItem(
        WorkItemStatus initialStatus = WorkItemStatus.Pending,
        string? payload = null,
        string? issueIdentifier = null,
        string? issueProviderConfigId = null)
    {
        var id = Guid.NewGuid();
        await using var db = _dbFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = issueIdentifier ?? "org/repo#1",
            IssueProviderConfigId = issueProviderConfigId ?? "ip-1",
            Status = initialStatus,
            AgentSelector = "dotnet",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            Payload = payload ?? "{}"
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ── GetWorkItemRetryCountAsync ────────────────────────────────────────

    [Fact]
    public async Task GetWorkItemRetryCountAsync_NullTransitionService_ReturnsZero()
    {
        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDeduplicationGuardService(registry, mockLogger.Object);
        var drainService = new JobQueueDrainService(
            new JobQueueDrainDependencies(dispatcher, registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), Mock.Of<IConsolidationDispatchService>(),
            new ShutdownSignal(), mockLogger.Object));

        var facadeWithout = new AgentHubFacade(
            registry, runService, dispatcher, drainService,
            Mock.Of<IPipelineRunHistoryService>(), Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(), NullLogger<AgentHubFacadeDependencies>.Instance);

        var result = await facadeWithout.GetWorkItemRetryCountAsync(
            new JobId(Guid.NewGuid().ToString()), CancellationToken.None);

        result.Should().Be(0);
    }

    [Fact]
    public async Task GetWorkItemRetryCountAsync_InvalidGuid_ReturnsZero()
    {
        var result = await _facade.GetWorkItemRetryCountAsync("not-a-guid", CancellationToken.None);
        result.Should().Be(0);
    }

    [Fact]
    public async Task GetWorkItemRetryCountAsync_ValidItem_ReturnsRetryCount()
    {
        var id = await SeedWorkItem();
        // Set a retry count
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.RetryCount = 2;
            await db.SaveChangesAsync();
        }

        // TODO: [WARNING] Pass new JobId(id.ToString()) instead of id.ToString() (raw string via implicit
        // conversion) to make the JobId type constraint load-bearing. If GetWorkItemRetryCountAsync
        // reverted to string, this test would still compile and pass. The same applies to similar
        // DB-path tests in this file (RequeueWorkItemAsync_ValidItem_TransitionsToPending,
        // TouchLastProgressAsync_FirstTouch_UpdatesLastProgressAt, etc.).
        // See: review-findings.md [WARNING] AgentHubFacadeJobIdMethodsTests.cs:2039
        var result = await _facade.GetWorkItemRetryCountAsync(id.ToString(), CancellationToken.None);
        result.Should().Be(2);
    }

    [Fact]
    public async Task GetWorkItemRetryCountAsync_ItemNotFound_ReturnsZero()
    {
        var nonExistentId = Guid.NewGuid().ToString();
        var result = await _facade.GetWorkItemRetryCountAsync(nonExistentId, CancellationToken.None);
        result.Should().Be(0);
    }

    // ── RequeueWorkItemAsync ──────────────────────────────────────────────

    [Fact]
    public async Task RequeueWorkItemAsync_NullTransitionService_DoesNotThrow()
    {
        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDeduplicationGuardService(registry, mockLogger.Object);
        var drainService = new JobQueueDrainService(
            new JobQueueDrainDependencies(dispatcher, registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), Mock.Of<IConsolidationDispatchService>(),
            new ShutdownSignal(), mockLogger.Object));

        var facadeWithout = new AgentHubFacade(
            registry, runService, dispatcher, drainService,
            Mock.Of<IPipelineRunHistoryService>(), Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(), NullLogger<AgentHubFacadeDependencies>.Instance);

        var act = () => facadeWithout.RequeueWorkItemAsync(
            new JobId(Guid.NewGuid().ToString()), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RequeueWorkItemAsync_InvalidGuid_DoesNotThrow()
    {
        var act = () => _facade.RequeueWorkItemAsync("not-a-guid", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RequeueWorkItemAsync_ValidItem_TransitionsToPending()
    {
        var id = await SeedWorkItem(WorkItemStatus.Dispatched);

        await _facade.RequeueWorkItemAsync(id.ToString(), CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Pending);
    }

    // ── GetWorkItemProviderConfigIdsAsync ─────────────────────────────────

    [Fact]
    public async Task GetWorkItemProviderConfigIdsAsync_NullDbFactory_ReturnsNull()
    {
        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDeduplicationGuardService(registry, mockLogger.Object);
        var drainService = new JobQueueDrainService(
            new JobQueueDrainDependencies(dispatcher, registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), Mock.Of<IConsolidationDispatchService>(),
            new ShutdownSignal(), mockLogger.Object));

        var facadeWithout = new AgentHubFacade(
            registry, runService, dispatcher, drainService,
            Mock.Of<IPipelineRunHistoryService>(), Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(), NullLogger<AgentHubFacadeDependencies>.Instance);

        var result = await facadeWithout.GetWorkItemProviderConfigIdsAsync(
            new JobId(Guid.NewGuid().ToString()), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkItemProviderConfigIdsAsync_InvalidGuid_ReturnsNull()
    {
        var result = await _facade.GetWorkItemProviderConfigIdsAsync("not-a-guid", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkItemProviderConfigIdsAsync_ItemNotFound_ReturnsNull()
    {
        var result = await _facade.GetWorkItemProviderConfigIdsAsync(
            Guid.NewGuid().ToString(), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkItemProviderConfigIdsAsync_WithPayload_ReturnsConfigIds()
    {
        var payload = """{"repoProviderConfigId":"repo-cfg-1","brainProviderConfigId":"brain-cfg-1"}""";
        var id = await SeedWorkItem(payload: payload);

        var result = await _facade.GetWorkItemProviderConfigIdsAsync(id.ToString(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.RepoProviderConfigId.Should().Be("repo-cfg-1");
        result.Value.BrainProviderConfigId.Should().Be("brain-cfg-1");
    }

    [Fact]
    public async Task GetWorkItemProviderConfigIdsAsync_EmptyPayload_ReturnsNullValues()
    {
        var id = await SeedWorkItem(payload: "{}");

        var result = await _facade.GetWorkItemProviderConfigIdsAsync(id.ToString(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.RepoProviderConfigId.Should().BeNull();
        result.Value.BrainProviderConfigId.Should().BeNull();
    }

    // ── TouchLastProgressAsync ────────────────────────────────────────────

    [Fact]
    public async Task TouchLastProgressAsync_NullDbFactory_DoesNotThrow()
    {
        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDeduplicationGuardService(registry, mockLogger.Object);
        var drainService = new JobQueueDrainService(
            new JobQueueDrainDependencies(dispatcher, registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), Mock.Of<IConsolidationDispatchService>(),
            new ShutdownSignal(), mockLogger.Object));

        var facadeWithout = new AgentHubFacade(
            registry, runService, dispatcher, drainService,
            Mock.Of<IPipelineRunHistoryService>(), Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(), NullLogger<AgentHubFacadeDependencies>.Instance);

        var act = () => facadeWithout.TouchLastProgressAsync(
            new JobId(Guid.NewGuid().ToString()), DateTimeOffset.UtcNow, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TouchLastProgressAsync_InvalidGuid_DoesNotThrow()
    {
        var act = () => _facade.TouchLastProgressAsync("not-a-guid", DateTimeOffset.UtcNow, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TouchLastProgressAsync_ItemNotFound_DoesNotThrow()
    {
        var act = () => _facade.TouchLastProgressAsync(
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TouchLastProgressAsync_FirstTouch_UpdatesLastProgressAt()
    {
        var id = await SeedWorkItem();
        var timestamp = DateTimeOffset.UtcNow;

        await _facade.TouchLastProgressAsync(id.ToString(), timestamp, CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.WorkItems.FindAsync(id);
        item!.LastProgressAt.Should().BeCloseTo(timestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TouchLastProgressAsync_RecentTouch_SkipsWrite()
    {
        var id = await SeedWorkItem();
        var recentTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1); // 1 minute ago — within throttle window
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.LastProgressAt = recentTimestamp;
            await db.SaveChangesAsync();
        }

        var newTimestamp = DateTimeOffset.UtcNow;
        await _facade.TouchLastProgressAsync(id.ToString(), newTimestamp, CancellationToken.None);

        // Should NOT update — within the 5-minute throttle window
        await using var verifyDb = _dbFactory.CreateDbContext();
        var result = await verifyDb.WorkItems.FindAsync(id);
        result!.LastProgressAt.Should().BeCloseTo(recentTimestamp, TimeSpan.FromSeconds(1));
    }

    // ── GetWorkItemIssueMetadataAsync ─────────────────────────────────────

    [Fact]
    public async Task GetWorkItemIssueMetadataAsync_NullDbFactory_ReturnsNull()
    {
        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDeduplicationGuardService(registry, mockLogger.Object);
        var drainService = new JobQueueDrainService(
            new JobQueueDrainDependencies(dispatcher, registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), Mock.Of<IConsolidationDispatchService>(),
            new ShutdownSignal(), mockLogger.Object));

        var facadeWithout = new AgentHubFacade(
            registry, runService, dispatcher, drainService,
            Mock.Of<IPipelineRunHistoryService>(), Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(), NullLogger<AgentHubFacadeDependencies>.Instance);

        var result = await facadeWithout.GetWorkItemIssueMetadataAsync(
            new JobId(Guid.NewGuid().ToString()), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkItemIssueMetadataAsync_InvalidGuid_ReturnsNull()
    {
        var result = await _facade.GetWorkItemIssueMetadataAsync("not-a-guid", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkItemIssueMetadataAsync_ItemNotFound_ReturnsNull()
    {
        var result = await _facade.GetWorkItemIssueMetadataAsync(
            Guid.NewGuid().ToString(), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkItemIssueMetadataAsync_ValidItem_ReturnsMetadata()
    {
        var id = await SeedWorkItem(issueIdentifier: "org/repo#77", issueProviderConfigId: "ip-77");

        var result = await _facade.GetWorkItemIssueMetadataAsync(id.ToString(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.IssueIdentifier.Should().Be("org/repo#77");
        result.Value.IssueProviderConfigId.Should().Be("ip-77");
    }

    // ── Signal ────────────────────────────────────────────────────────────

    [Fact]
    public void Signal_WithNoPendingDrainService_DoesNotThrow()
    {
        // Exercises the _pendingDrainService?.Signal() null-conditional path.
        // _facade has no pending drain service (null), so this tests the null branch.
        var act = () => _facade.Signal();
        act.Should().NotThrow();
    }

    // ── Helper ────────────────────────────────────────────────────────────

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
    }
}
