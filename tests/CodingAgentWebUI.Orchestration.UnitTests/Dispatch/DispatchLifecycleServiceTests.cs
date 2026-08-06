using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Tests for <see cref="DispatchLifecycleService"/> covering the extracted
/// <c>LoadAndPrepareWorkItemAsync</c> and <c>PreSaveJobNameAsync</c> paths.
/// Issue #1825: cognitive complexity reduction via extract-method refactoring.
/// </summary>
public sealed class DispatchLifecycleServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;

    public DispatchLifecycleServiceTests()
    {
        var dbName = $"DispatchLifecycle-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(
            _dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockKubeClient = new Mock<IKubernetesJobClient>();
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── LoadAndPrepareWorkItemAsync: item no longer pending ─────────────────

    /// <summary>
    /// When the WorkItem was already claimed or advanced beyond Pending by another process,
    /// ExecuteDispatchLifecycleAsync should return early without dispatching (exercises
    /// the LoadAndPrepareWorkItemAsync early-exit path).
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_WorkItemAlreadyRunning_ReturnsEarlyWithoutDispatching()
    {
        // Arrange: insert a work item that is already Running (not Pending)
        var workItemId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(BuildWorkItem(workItemId, WorkItemStatus.Running));
            await db.SaveChangesAsync();
        }

        var svc = CreateService();
        var prepareInvoked = false;

        // Act: run lifecycle — prepareVariant should NOT be called because item is not Pending
        await using var testDb = await _dbFactory.CreateDbContextAsync();
        var ctx = BuildContext(testDb, workItemId, isKiroAgent: false);

        await svc.ExecuteDispatchLifecycleAsync(
            ctx,
            prepareVariant: _ =>
            {
                prepareInvoked = true;
                return Task.FromResult((true, (Dictionary<string, string>?)null));
            },
            onDispatchSuccess: null,
            ct: CancellationToken.None);

        // Assert: no K8s job created, prepareVariant never called
        prepareInvoked.Should().BeFalse("item is not Pending — lifecycle should abort at LoadAndPrepare");
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// When the WorkItem does not exist in the database (race condition — deleted before dispatch),
    /// ExecuteDispatchLifecycleAsync should return early.
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_WorkItemNotFound_ReturnsEarlyWithoutDispatching()
    {
        // Arrange: no work item in DB — simulates deletion race
        var svc = CreateService();
        var prepareInvoked = false;

        await using var testDb = await _dbFactory.CreateDbContextAsync();
        var ctx = BuildContext(testDb, Guid.NewGuid(), isKiroAgent: false);

        await svc.ExecuteDispatchLifecycleAsync(
            ctx,
            prepareVariant: _ =>
            {
                prepareInvoked = true;
                return Task.FromResult((true, (Dictionary<string, string>?)null));
            },
            onDispatchSuccess: null,
            ct: CancellationToken.None);

        prepareInvoked.Should().BeFalse("non-existent item — lifecycle should abort at LoadAndPrepare");
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── LoadAndPrepareWorkItemAsync: prepareVariant returns shouldContinue=false ─

    /// <summary>
    /// When prepareVariant signals abort (shouldContinue=false), the lifecycle exits
    /// without writing K8sJobName or creating a K8s Job (exercises PreSaveJobNameAsync skip).
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_PrepareVariantAbortsDispatch_SkipsK8sJobCreation()
    {
        // Arrange: pending work item
        var workItemId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(BuildWorkItem(workItemId, WorkItemStatus.Pending));
            await db.SaveChangesAsync();
        }

        var svc = CreateService();

        await using var testDb = await _dbFactory.CreateDbContextAsync();
        var ctx = BuildContext(testDb, workItemId, isKiroAgent: false);

        // Act: prepareVariant returns shouldContinue=false
        await svc.ExecuteDispatchLifecycleAsync(
            ctx,
            prepareVariant: _ => Task.FromResult((false, (Dictionary<string, string>?)null)),
            onDispatchSuccess: null,
            ct: CancellationToken.None);

        // Assert: item remains Pending, no job created
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── PreSaveJobNameAsync: happy path ─────────────────────────────────────

    /// <summary>
    /// Full happy-path: pending item, prepare returns continue, K8s job created successfully.
    /// Exercises PreSaveJobNameAsync success branch and the subsequent Dispatched transition.
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_PendingItem_DispatchesSuccessfullyAndSavesJobName()
    {
        // Arrange: pending work item with a matching template
        var workItemId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(BuildWorkItem(workItemId, WorkItemStatus.Pending));
            await db.SaveChangesAsync();
        }

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService();
        var successCalled = false;

        await using var testDb = await _dbFactory.CreateDbContextAsync();
        var ctx = BuildContext(testDb, workItemId, isKiroAgent: false);

        // Act
        await svc.ExecuteDispatchLifecycleAsync(
            ctx,
            prepareVariant: _ => Task.FromResult((true, (Dictionary<string, string>?)null)),
            onDispatchSuccess: _ =>
            {
                successCalled = true;
                return Task.CompletedTask;
            },
            ct: CancellationToken.None);

        // Assert: item transitioned to Dispatched and K8sJobName set
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
        item.K8sJobName.Should().NotBeNullOrEmpty();
        successCalled.Should().BeTrue();

        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── PVC selection: no PVC available for Kiro agent ──────────────────────

    /// <summary>
    /// When isKiroAgent=true but no PVCs are available, lifecycle returns early
    /// without dispatching (exercises SelectPvcAsync → null path).
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_KiroAgentNoPvcAvailable_ReturnsEarlyWithoutDispatching()
    {
        // Arrange: pending work item
        var workItemId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(BuildWorkItem(workItemId, WorkItemStatus.Pending));
            await db.SaveChangesAsync();
        }

        var svc = CreateService();
        var prepareInvoked = false;

        await using var testDb = await _dbFactory.CreateDbContextAsync();
        // Build context with isKiroAgent=true and empty PVC pool
        var ctx = BuildContext(testDb, workItemId, isKiroAgent: true, availablePvcs: new List<string>());

        await svc.ExecuteDispatchLifecycleAsync(
            ctx,
            prepareVariant: _ =>
            {
                prepareInvoked = true;
                return Task.FromResult((true, (Dictionary<string, string>?)null));
            },
            onDispatchSuccess: null,
            ct: CancellationToken.None);

        // Assert: no job created, item still Pending, prepare not called
        prepareInvoked.Should().BeFalse("no PVC available — lifecycle should abort before LoadAndPrepare");
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private DispatchLifecycleService CreateService() =>
        new DispatchLifecycleService(
            _mockKubeClient.Object,
            _transitionService,
            new DispatchServiceOptions
            {
                PollIntervalSeconds = 10,
                RateLimitPerSecond = 100,
                Namespace = "default",
                OrchestratorUrl = "http://orchestrator:8080",
                AgentApiKeySecretName = "agent-api-key",
                KiroPvcPool = new List<string> { "pvc-test-1" }
            });

    private static DispatchLifecycleContext BuildContext(
        PipelineDbContext db,
        Guid workItemId,
        bool isKiroAgent,
        List<string>? availablePvcs = null) =>
        new DispatchLifecycleContext(
            Db: db,
            Item: new PendingWorkItemProjection
            {
                Id = workItemId,
                AgentSelector = "selector-1",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                TimeoutSeconds = 3600,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "owner/repo#1",
                IssueProviderConfigId = "provider-1"
            },
            Template: new JobTemplate
            {
                Labels = "kiro,dotnet",
                Image = "ghcr.io/agent:latest",
                ProviderType = "kiro"
            },
            IsKiroAgent: isKiroAgent,
            AvailablePvcs: availablePvcs ?? new List<string> { "pvc-test-1" },
            ConcurrencyBySelector: new Dictionary<string, int>(),
            LogPrefix: "test:");

    private static WorkItemEntity BuildWorkItem(Guid id, WorkItemStatus status) =>
        new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "owner/repo#1",
            IssueProviderConfigId = "provider-1",
            Status = status,
            Payload = """{"issueIdentifier":"owner/repo#1","issueProviderConfigId":"provider-1","repoProviderConfigId":"repo-1","initiatedBy":"test","taskType":"Implementation","agentSelector":"selector-1","runId":"test","timeoutSeconds":3600}""",
            AgentSelector = "selector-1",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            TimeoutSeconds = 3600
        };

    // ── Test infrastructure ──────────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Disable concurrency tokens — in-memory EF doesn't support RowVersion
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            // Remove filtered indexes not supported by in-memory provider
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
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult<PipelineDbContext>(new TestPipelineDbContext(_options));
    }
}
