using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using DispatchLifecycleService = CodingAgent.Api.Dispatch.DispatchLifecycleService;
using DispatchStateBuilder = CodingAgent.Api.Dispatch.DispatchStateBuilder;
using DispatchTemplateResolver = CodingAgent.Api.Dispatch.DispatchTemplateResolver;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
using Xunit;

namespace CodingAgent.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Dispatch-loop tests for <see cref="WorkItemDispatchService"/>.
/// Covers the rate-limit → eligibility → dispatch loop behavior for non-consolidation
/// WorkItems (Implementation, Review, Decomposition).
///
/// Items are inserted directly as <c>Pending</c> and the DB state is asserted after
/// calling <c>PollAndDispatchAsync</c>.
/// </summary>
[Trait("Feature", "WorkItemDispatchService")]
public class WorkItemDispatchServiceDispatchLoopTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly AlwaysLeaderService _leader = new();

    public WorkItemDispatchServiceDispatchLoopTests()
    {
        var dbName = $"WorkItemDispatch-{Guid.NewGuid()}";
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

    // ── Happy path ────────────────────────────────────────────────────────

    /// <summary>
    /// Standard Implementation WorkItem: eligible item dispatched as K8s Job and
    /// transitioned to Dispatched.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_EligibleImplementationItem_DispatchesAndTransitionsToDispatched()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Implementation);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(pvcPool: ["pvc-1"]);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), "default", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Review WorkItems are dispatched — the filter `TaskType != Consolidation` includes Review.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_EligibleReviewItem_DispatchesAndTransitionsToDispatched()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Review);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(pvcPool: ["pvc-1"]);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched, "Review items must be dispatched by WorkItemDispatchService");
    }

    /// <summary>
    /// Consolidation WorkItems are NOT dispatched by WorkItemDispatchService —
    /// they are dispatched synchronously via <c>KubernetesWorkDistributor.DistributeAsync</c>.
    /// </summary>
    // TODO [WARNING]: No surviving test verifies the synchronous dispatch routing itself — that
    // KubernetesWorkDistributor.DistributeAsync routes TaskType=Consolidation to DispatchSynchronouslyAsync
    // rather than the async Pending queue. The end-to-end FullLifecycle test that covered this routing
    // was part of DispatchServiceConsolidationTests.cs (deleted with ConsolidationWorkItemDispatchService).
    // If the `if (request.TaskType == WorkItemTaskType.Consolidation)` branch in KubernetesWorkDistributor
    // were accidentally removed, consolidation dispatches would silently land in the Pending queue with no
    // poller to claim them, and no test would catch it. Consider adding a unit test for
    // KubernetesWorkDistributor that asserts Consolidation requests call DispatchSynchronouslyAsync.
    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_IsNotDispatchedByThisService()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Consolidation, issueProviderConfigId: "consolidation");

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(pvcPool: ["pvc-1"]);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "WorkItemDispatchService must not dispatch Consolidation items");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Pending,
            "Consolidation items must remain Pending (dispatched synchronously via KubernetesWorkDistributor, not polled)");
    }

    // ── Rate-limit / concurrency ──────────────────────────────────────────

    /// <summary>
    /// Rate limit hit: when the token bucket is exhausted after the first item,
    /// subsequent items in the same poll cycle are not dispatched.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_RateLimitExhausted_StopsAfterFirstItem()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await InsertWorkItem(firstId, WorkItemTaskType.Implementation,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await InsertWorkItem(secondId, WorkItemTaskType.Implementation,
            createdAt: DateTimeOffset.UtcNow);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(rateLimitPerSecond: 1, pvcPool: ["pvc-1", "pvc-2"]);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var first = await db.WorkItems.FindAsync(firstId);
        var second = await db.WorkItems.FindAsync(secondId);

        first!.Status.Should().Be(WorkItemStatus.Dispatched,
            "first item must be dispatched before rate limit is exhausted");
        second!.Status.Should().Be(WorkItemStatus.Pending,
            "second item must stay Pending when the rate limit is hit mid-cycle");
    }

    /// <summary>
    /// Concurrency limit: items whose selector is at the concurrency limit are skipped;
    /// the loop continues to the next item with a different selector.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_AtConcurrencyLimit_SkipsItemAndContinues()
    {
        var limitedId = Guid.NewGuid();
        var freeId = Guid.NewGuid();

        // Selector at limit
        await InsertWorkItem(limitedId, WorkItemTaskType.Implementation,
            agentSelector: "kiro,dotnet", createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        // Different selector (not at limit)
        await InsertWorkItem(freeId, WorkItemTaskType.Implementation,
            agentSelector: "kiro,python", createdAt: DateTimeOffset.UtcNow);
        // Running item occupies the single dotnet slot
        await InsertWorkItem(Guid.NewGuid(), WorkItemTaskType.Implementation,
            agentSelector: "kiro,dotnet", status: WorkItemStatus.Running);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(
            imageMapping: new() { ["dotnet,kiro"] = "img:latest", ["kiro,python"] = "img:python" },
            maxConcurrentPods: new() { ["dotnet,kiro"] = 1 },
            pvcPool: ["pvc-1"]);

        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var limited = await db.WorkItems.FindAsync(limitedId);
        var free = await db.WorkItems.FindAsync(freeId);

        limited!.Status.Should().Be(WorkItemStatus.Pending,
            "concurrency-limited item must remain Pending");
        free!.Status.Should().Be(WorkItemStatus.Dispatched,
            "loop must continue past the skipped item and dispatch the next eligible one");
    }

    /// <summary>
    /// No-template outcome: item with an unresolvable selector is transitioned to Failed;
    /// loop continues to the next item.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_NoTemplate_FailsItemAndContinues()
    {
        var unknownId = Guid.NewGuid();
        var validId = Guid.NewGuid();

        await InsertWorkItem(unknownId, WorkItemTaskType.Implementation,
            agentSelector: "unknown-selector", createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await InsertWorkItem(validId, WorkItemTaskType.Implementation,
            agentSelector: "kiro,dotnet", createdAt: DateTimeOffset.UtcNow);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(pvcPool: ["pvc-1"]);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var unknown = await db.WorkItems.FindAsync(unknownId);
        var valid = await db.WorkItems.FindAsync(validId);

        unknown!.Status.Should().Be(WorkItemStatus.Failed,
            "item with no template must be transitioned to Failed");
        unknown.ErrorMessage.Should().Contain("No job template",
            "error message must identify the unresolvable selector");
        valid!.Status.Should().Be(WorkItemStatus.Dispatched,
            "loop must continue after a no-template failure and dispatch the next eligible item");
    }

    // ── PriorityWeight ordering ───────────────────────────────────────────

    /// <summary>
    /// Items are polled in PriorityWeight DESC, CreatedAt ASC order — the higher-weight item
    /// is dispatched first when the concurrency limit allows only one.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_HighPriorityItemDispatchedFirst_WhenConcurrencyLimitIsOne()
    {
        var lowId = Guid.NewGuid();
        var highId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        await InsertWorkItem(lowId, WorkItemTaskType.Implementation,
            priorityWeight: 0, createdAt: baseTime.AddMinutes(-10));
        await InsertWorkItem(highId, WorkItemTaskType.Implementation,
            priorityWeight: 100, createdAt: baseTime.AddMinutes(-5));

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // maxConcurrentPods=1 means only one K8s Job fits
        var handler = CreateHandler(
            maxConcurrentPods: new() { ["dotnet,kiro"] = 1 },
            pvcPool: ["pvc-1"]);

        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var low = await db.WorkItems.FindAsync(lowId);
        var high = await db.WorkItems.FindAsync(highId);

        high!.Status.Should().Be(WorkItemStatus.Dispatched,
            "higher-priority item must be dispatched first");
        low!.Status.Should().Be(WorkItemStatus.Pending,
            "lower-priority item must stay Pending when concurrency limit is reached");
    }

    // ── Project secrets / failure callbacks ───────────────────────────────

    /// <summary>
    /// An item carrying a ProjectId exercises the project-secret lookup in prepareVariant.
    /// With no matching project row, the lookup returns no secrets and the dispatch proceeds
    /// normally — the item must still transition to Dispatched.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_ItemWithProjectId_LoadsProjectSecretsAndDispatches()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Implementation, projectId: Guid.NewGuid());

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(pvcPool: ["pvc-1"]);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "an item with a ProjectId must still dispatch when the project has no secrets to inject");
    }

    /// <summary>
    /// When K8s Job creation throws, the dispatch lifecycle fails the item and invokes the
    /// service's onFailure callback. The item must land in Failed rather than Dispatched.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenK8sJobCreationThrows_InvokesOnFailureAndFailsItem()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Implementation);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("k8s api unavailable"));

        var handler = CreateHandler(pvcPool: ["pvc-1"]);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Failed,
            "a K8s Job creation failure must transition the item to Failed via the dispatch lifecycle");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private WorkItemDispatchService CreateHandler(
        Dictionary<string, string>? imageMapping = null,
        Dictionary<string, int>? maxConcurrentPods = null,
        string[]? pvcPool = null,
        int rateLimitPerSecond = 100)
    {
        imageMapping ??= new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" };
        pvcPool ??= ["pvc-test-1", "pvc-test-2"];

        var normalizedMax = maxConcurrentPods?.ToDictionary(
            kv => JobTemplateStore.NormalizeLabels(kv.Key), kv => kv.Value);

        var templates = imageMapping.Select(kv => new JobTemplate
        {
            Labels = kv.Key,
            Image = kv.Value,
            ProviderType = "kiro",
            MaxConcurrent = normalizedMax?.GetValueOrDefault(JobTemplateStore.NormalizeLabels(kv.Key), 0) ?? 0
        }).ToList();

        var templateStore = JobTemplateStore.LoadFromJson(JsonSerializer.Serialize(templates));

        var options = new DispatchServiceOptions
        {
            PollIntervalSeconds = 10,
            RateLimitPerSecond = rateLimitPerSecond,
            Namespace = "default",
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "agent-api-key",
            KiroPvcPool = pvcPool.ToList()
        };

        var lifecycle = new DispatchLifecycleService(_mockKubeClient.Object, _transitionService, options);
        var stateBuilder = new DispatchStateBuilder(
            _dbFactory, lifecycle, templateStore,
            new DispatchTemplateResolver(null, templateStore),
            options);

        return new WorkItemDispatchService(
            new WorkItemDispatchServiceDependencies(
                _dbFactory, _leader, lifecycle, templateStore,
                Mock.Of<Microsoft.Extensions.Configuration.IConfiguration>(),
                _transitionService,
                stateBuilder),
            options);
    }

    private async Task InsertWorkItem(
        Guid id,
        WorkItemTaskType taskType,
        string agentSelector = "kiro,dotnet",
        string issueProviderConfigId = "github",
        WorkItemStatus status = WorkItemStatus.Pending,
        DateTimeOffset? createdAt = null,
        int priorityWeight = 0,
        Guid? projectId = null)
    {
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = id.ToString(),
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = "",
            InitiatedBy = "test",
            TaskType = taskType,
            AgentSelector = agentSelector,
            TimeoutSeconds = 300,
            RunId = id.ToString()
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = id.ToString(),
            IssueProviderConfigId = issueProviderConfigId,
            Status = status,
            AgentSelector = agentSelector,
            TaskType = taskType,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            TimeoutSeconds = 300,
            PriorityWeight = priorityWeight,
            ProjectId = projectId,
            Payload = JsonSerializer.Serialize(payload, PipelineJsonOptions.Default)
        });
        await db.SaveChangesAsync();
    }

    // ── Test infrastructure ───────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Strip RowVersion concurrency tokens and filtered indexes — incompatible with
            // the EF Core InMemory provider used in unit tests.
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
