using System.Threading.RateLimiting;
using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using DispatchLifecycleService = CodingAgent.Api.Dispatch.DispatchLifecycleService;
using DispatchStateBuilder = CodingAgent.Api.Dispatch.DispatchStateBuilder;
using DispatchTemplateResolver = CodingAgent.Api.Dispatch.DispatchTemplateResolver;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CodingAgent.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Additional branch coverage tests for <see cref="DispatchStateBuilder"/>.
/// Covers static helper methods (<c>IsAtConcurrencyLimit</c>, <c>IsKiroAgentWithoutPvc</c>),
/// telemetry recording path in <c>BuildStateAsync</c>, and empty-result telemetry.
/// </summary>
[Trait("Feature", "DispatchStateBuilder")]
public class DispatchStateBuilderBranchTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly LeaderElectionService _leaderElection;

    public DispatchStateBuilderBranchTests()
    {
        var dbName = $"DispatchStateBuilderBranch-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _mockKubeClient = new Mock<IKubernetesJobClient>();
        _leaderElection = CreateAlwaysLeaderElection();
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    // ── IsAtConcurrencyLimit — static helper ─────────────────────────────

    [Fact]
    public void IsAtConcurrencyLimit_MaxConcurrentZero_AlwaysReturnsFalse()
    {
        // maxConcurrent == 0 means "no limit configured" — always allow
        var concurrency = new Dictionary<string, int> { ["selector-1"] = 999 };
        DispatchStateBuilder.IsAtConcurrencyLimit("selector-1", concurrency, maxConcurrent: 0)
            .Should().BeFalse("zero maxConcurrent disables the limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_NegativeMax_AlwaysReturnsFalse()
    {
        var concurrency = new Dictionary<string, int> { ["selector-1"] = 999 };
        DispatchStateBuilder.IsAtConcurrencyLimit("selector-1", concurrency, maxConcurrent: -1)
            .Should().BeFalse("negative maxConcurrent disables the limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_BelowLimit_ReturnsFalse()
    {
        var concurrency = new Dictionary<string, int> { ["sel"] = 1 };
        DispatchStateBuilder.IsAtConcurrencyLimit("sel", concurrency, maxConcurrent: 2)
            .Should().BeFalse("current(1) < max(2) — not at limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_AtLimit_ReturnsTrue()
    {
        var concurrency = new Dictionary<string, int> { ["sel"] = 2 };
        DispatchStateBuilder.IsAtConcurrencyLimit("sel", concurrency, maxConcurrent: 2)
            .Should().BeTrue("current(2) == max(2) — at limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_AboveLimit_ReturnsTrue()
    {
        var concurrency = new Dictionary<string, int> { ["sel"] = 5 };
        DispatchStateBuilder.IsAtConcurrencyLimit("sel", concurrency, maxConcurrent: 2)
            .Should().BeTrue("current(5) > max(2) — above limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_SelectorNotInMap_ReturnsFalse()
    {
        // Selector not in the dictionary → GetValueOrDefault returns 0
        var concurrency = new Dictionary<string, int>();
        DispatchStateBuilder.IsAtConcurrencyLimit("new-selector", concurrency, maxConcurrent: 1)
            .Should().BeFalse("no active items for selector → current=0 < max=1");
    }

    [Fact]
    public void IsAtConcurrencyLimit_NullSelector_FallsBackToEmpty_ReturnsFalse()
    {
        // Null agentSelector → "" key, nothing in map → 0 active
        var concurrency = new Dictionary<string, int>();
        DispatchStateBuilder.IsAtConcurrencyLimit(null, concurrency, maxConcurrent: 1)
            .Should().BeFalse("null selector uses empty key, not in map → 0 active");
    }

    // ── IsKiroAgentWithoutPvc — static helper ─────────────────────────────

    [Fact]
    public void IsKiroAgentWithoutPvc_KiroAgent_NoPvcs_ReturnsTrue()
    {
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: true, availablePvcs: new List<string>())
            .Should().BeTrue("kiro agent with no PVCs must be blocked");
    }

    [Fact]
    public void IsKiroAgentWithoutPvc_KiroAgent_HasPvc_ReturnsFalse()
    {
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: true, availablePvcs: new List<string> { "pvc-1" })
            .Should().BeFalse("kiro agent with available PVC may proceed");
    }

    [Fact]
    public void IsKiroAgentWithoutPvc_NonKiroAgent_NoPvcs_ReturnsFalse()
    {
        // Non-kiro agents don't need PVCs
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: false, availablePvcs: new List<string>())
            .Should().BeFalse("non-kiro agents never require a PVC");
    }

    [Fact]
    public void IsKiroAgentWithoutPvc_NonKiroAgent_HasPvc_ReturnsFalse()
    {
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: false, availablePvcs: new List<string> { "pvc-1" })
            .Should().BeFalse("non-kiro agent with PVC — still false");
    }

    // ── BuildStateAsync — telemetry path ──────────────────────────────────

    [Fact]
    public async Task BuildStateAsync_RecordTelemetry_True_WithItems_DoesNotThrow()
    {
        // The telemetry-recording path calls WorkDistributionTelemetry.RecordLastPollEpoch()
        // and UpdateCredentialPoolMetrics(). Verify the call succeeds without throwing.
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(pvcPool: ["pvc-1"]);

        // Should not throw even when telemetry path is exercised
        var act = () => builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: true,
            CancellationToken.None);

        await act.Should().NotThrowAsync("telemetry recording must be non-faulting");
    }

    [Fact]
    public async Task BuildStateAsync_RecordTelemetry_True_NoPendingItems_ReturnsNull()
    {
        // Telemetry path with no items: calls DispatcherPollCount.Add(1) then returns null
        var builder = CreateBuilder();

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: true,
            CancellationToken.None);

        state.Should().BeNull("no pending items → null regardless of telemetry flag");
    }

    [Fact]
    public async Task BuildStateAsync_RecordTelemetry_False_WithItems_ReturnsState()
    {
        // Verify the non-telemetry path still builds state correctly
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        state!.PendingItems.Should().HaveCount(1);
    }

    // ── GetEligibleCandidatesAsync — concurrency with PVC available ───────

    [Fact]
    public async Task GetEligibleCandidatesAsync_MultipleItems_AllEligible_YieldsAll()
    {
        await InsertWorkItem(Guid.NewGuid(), "opencode,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "opencode,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(imageMapping: new() { ["dotnet,opencode"] = "img:latest" });
        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();
        var candidates = new List<DispatchCandidate>();

        await foreach (var c in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "TestCaller",
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None))
        {
            candidates.Add(c);
        }

        candidates.Should().HaveCount(2, "two eligible items with no concurrency limit");
    }

    // ── Tier ordering (RunType tier = primary sort key) ───────────────────
    // TODO: These tests use the EF Core InMemory provider, which evaluates the OrderBy ternary
    // as a CLR expression (LINQ-to-Objects) — it never exercises the SQL CASE WHEN translation
    // used against PostgreSQL in production. If Npgsql failed to translate the conditional (it
    // does not — this is supported), these tests would still pass, giving false confidence.
    // Consider adding one tier-ordering assertion to the Testcontainers-backed Npgsql integration
    // suite (tests/CodingAgent.Infrastructure.IntegrationTests) to guard the actual SQL translation.

    // TODO: All tier-ordering tests below pass `w => true` as the taskTypeFilter, exercising the
    // "future CAS-locked poller" code path only. The acceptance criteria also require that tier
    // ordering works when the legacy filter (`w => w.TaskType != Consolidation`) is active.
    // Add a test that passes the legacy filter with a mixed-type seed (including a Consolidation
    // item that should be hidden) to verify the filter-before-sort contract and guard against a
    // future refactor accidentally swapping the filter and sort in BuildStateAsync.

    /// <summary>
    /// AC 1: Mixed task types are ordered by tier then PriorityWeight then CreatedAt.
    /// Review (tier 0) &gt; Decomposition (tier 1) &gt; Implementation (tier 2) &gt; Consolidation (tier 3).
    /// All items have equal PriorityWeight and different CreatedAt to make tier the sole differentiator.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_MixedTaskTypes_OrdersByTierThenPriorityThenCreatedAt()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var reviewId = Guid.NewGuid();
        var decompId = Guid.NewGuid();
        var implId = Guid.NewGuid();
        var consId = Guid.NewGuid();

        // Seed newest → oldest so that CreatedAt alone would produce the wrong order.
        await InsertWorkItemFull(reviewId, WorkItemTaskType.Review, priorityWeight: 0, createdAt: baseTime.AddMinutes(3));
        await InsertWorkItemFull(decompId, WorkItemTaskType.Decomposition, priorityWeight: 0, createdAt: baseTime.AddMinutes(2));
        await InsertWorkItemFull(implId, WorkItemTaskType.Implementation, priorityWeight: 0, createdAt: baseTime.AddMinutes(1));
        await InsertWorkItemFull(consId, WorkItemTaskType.Consolidation, priorityWeight: 0, createdAt: baseTime);

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => true,   // include all task types — Pending-status filter is applied inside BuildStateAsync
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        var ids = state!.PendingItems.Select(i => i.Id).ToList();
        ids.Should().ContainInConsecutiveOrder(new[] { reviewId, decompId, implId, consId },
            "tier ordering must place Review→Decomp→Impl→Consolidation regardless of CreatedAt");
    }

    /// <summary>
    /// AC 2: Within a tier, PriorityWeight DESC is the secondary sort key.
    /// A manual (weight=100) Implementation item dispatches before an automatic (weight=0) one.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_WithinTier_HigherPriorityWeightFirst()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var highId = Guid.NewGuid();
        var lowId = Guid.NewGuid();

        // lowId is older — without PriorityWeight ordering it would come first (FIFO).
        await InsertWorkItemFull(lowId, WorkItemTaskType.Implementation, priorityWeight: 0, createdAt: baseTime.AddMinutes(-10));
        await InsertWorkItemFull(highId, WorkItemTaskType.Implementation, priorityWeight: 100, createdAt: baseTime);

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => true,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        var items = state!.PendingItems;
        items[0].Id.Should().Be(highId,
            "higher PriorityWeight (100) must come before lower weight (0) within the same tier");
        items[1].Id.Should().Be(lowId);
    }

    /// <summary>
    /// AC 3: Within a tier, CreatedAt ASC (FIFO) is the tiebreaker when PriorityWeight is equal.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_WithinTier_OlderItemFirst_WhenSamePriority()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var olderId = Guid.NewGuid();
        var newerId = Guid.NewGuid();

        await InsertWorkItemFull(newerId, WorkItemTaskType.Implementation, priorityWeight: 0, createdAt: baseTime);
        await InsertWorkItemFull(olderId, WorkItemTaskType.Implementation, priorityWeight: 0, createdAt: baseTime.AddMinutes(-10));

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => true,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        var items = state!.PendingItems;
        items[0].Id.Should().Be(olderId,
            "older item (earlier CreatedAt) must come first when PriorityWeight and tier are equal");
        items[1].Id.Should().Be(newerId);
    }

    /// <summary>
    /// AC 4: Consolidation (tier 3) is last even if it was created first (oldest CreatedAt).
    /// A Review item created later still dispatches before consolidation.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_ConsolidationIsLast_EvenIfCreatedFirst()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var consId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();

        await InsertWorkItemFull(consId, WorkItemTaskType.Consolidation, priorityWeight: 0, createdAt: baseTime.AddMinutes(-100));
        await InsertWorkItemFull(reviewId, WorkItemTaskType.Review, priorityWeight: 0, createdAt: baseTime);

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => true,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        var items = state!.PendingItems;
        items[0].Id.Should().Be(reviewId,
            "Review (tier 0) must come before Consolidation (tier 3) regardless of CreatedAt");
        items[1].Id.Should().Be(consId,
            "Consolidation must be last even though it was created first");
    }

    /// <summary>
    /// AC 6: A manual (PriorityWeight=100) Implementation item must NOT cross the tier boundary
    /// and jump ahead of a Review item (PriorityWeight=0). Tier is the primary key.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_ManualItemWithinTier_DoesNotCrossOtherTier()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var implManualId = Guid.NewGuid();
        var reviewAutoId = Guid.NewGuid();

        await InsertWorkItemFull(implManualId, WorkItemTaskType.Implementation, priorityWeight: 100, createdAt: baseTime);
        await InsertWorkItemFull(reviewAutoId, WorkItemTaskType.Review, priorityWeight: 0, createdAt: baseTime.AddMinutes(10));

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => true,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        var items = state!.PendingItems;
        items[0].Id.Should().Be(reviewAutoId,
            "Review (tier 0) must come before a manual Implementation (tier 2) — tier beats PriorityWeight");
        items[1].Id.Should().Be(implManualId,
            "manual Implementation item must be second, not first");
    }

    // TODO: Missing test — legacy flat loop filter contract: add a test that calls BuildStateAsync
    // with the legacy filter (`w => w.TaskType != WorkItemTaskType.Consolidation`) when a Consolidation
    // Pending item exists in the DB, and asserts that the item does NOT appear in PendingItems.
    // This guards the filter-before-sort contract in BuildStateAsync and prevents the filter from
    // being accidentally removed in a future refactor. The existing
    // PollAndDispatch_ConsolidationItem_IsNotDispatchedByThisService covers the end-to-end poller
    // behaviour, but not the BuildStateAsync filter boundary directly.

    // ── Helpers ──────────────────────────────────────────────────────────

    private DispatchStateBuilder CreateBuilder(
        Dictionary<string, string>? imageMapping = null,
        Dictionary<string, int>? maxConcurrent = null,
        string[]? pvcPool = null)
    {
        imageMapping ??= new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" };
        maxConcurrent ??= new();
        pvcPool ??= ["pvc-1", "pvc-2"];

        var templateProvider = BuildTemplateProvider(imageMapping, maxConcurrent);
        var templateResolver = new DispatchTemplateResolver(null, templateProvider);
        var options = new DispatchServiceOptions { KiroPvcPool = pvcPool.ToList() };
        var transitionService = new Infrastructure.Persistence.Services.WorkItemTransitionService(
            _dbFactory, new NullLogger<Infrastructure.Persistence.Services.WorkItemTransitionService>());
        var lifecycle = new DispatchLifecycleService(_mockKubeClient.Object, transitionService, options);

        return new DispatchStateBuilder(_dbFactory, lifecycle, templateProvider, templateResolver, options);
    }

    private static JobTemplateStore BuildTemplateProvider(
        Dictionary<string, string> imageMapping,
        Dictionary<string, int>? maxConcurrentPods = null)
    {
        var normalizedMaxConcurrent = maxConcurrentPods?.ToDictionary(
            kv => JobTemplateStore.NormalizeLabels(kv.Key), kv => kv.Value);

        var templates = imageMapping.Select(kv => new JobTemplate
        {
            Labels = kv.Key,
            Image = kv.Value,
            ProviderType = kv.Key.Contains("kiro") ? "kiro" : "opencode",
            MaxConcurrent = normalizedMaxConcurrent?.GetValueOrDefault(
                JobTemplateStore.NormalizeLabels(kv.Key), 0) ?? 0
        }).ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(templates);
        return JobTemplateStore.LoadFromJson(json);
    }

    private static TokenBucketRateLimiter CreateUnlimitedRateLimiter() => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 1000,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        TokensPerPeriod = 1000,
        AutoReplenishment = true
    });

    private static LeaderElectionService CreateAlwaysLeaderElection()
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        isLeaderField!.SetValue(les, true);

        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        leaderCtsField!.SetValue(les, new CancellationTokenSource());
        return les;
    }

    private async Task InsertWorkItem(Guid id, string agentSelector, WorkItemStatus status,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = $"owner/repo#{id.ToString("N")[..4]}",
            IssueProviderConfigId = "provider-1",
            Status = status,
            AgentSelector = agentSelector,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            Payload = "{}",
            TaskType = taskType
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Extended helper supporting PriorityWeight and CreatedAt for tier-ordering tests.
    /// Inserts a Pending item on the default "kiro,dotnet" selector.
    /// </summary>
    private async Task InsertWorkItemFull(
        Guid id,
        WorkItemTaskType taskType,
        int priorityWeight = 0,
        DateTimeOffset? createdAt = null,
        string agentSelector = "kiro,dotnet")
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = $"owner/repo#{id.ToString("N")[..4]}",
            IssueProviderConfigId = "provider-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = agentSelector,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            Payload = "{}",
            TaskType = taskType,
            PriorityWeight = priorityWeight
        });
        await db.SaveChangesAsync();
    }

    // ── Test infrastructure ──────────────────────────────────────────────

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
