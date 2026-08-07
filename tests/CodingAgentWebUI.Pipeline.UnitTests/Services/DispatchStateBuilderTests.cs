using System.Threading.RateLimiting;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="DispatchStateBuilder"/>.
/// Validates: state building, concurrency gating, rate limiting, template resolution, PVC gating.
/// </summary>
/// <remarks>
/// TODO: Missing test for successful fallback template resolution path — GetEligibleCandidatesAsync
/// has a code path where ResolveTemplateViaProfileAsync returns a valid fallback template and the
/// concurrency limit is re-checked against the resolved selector. Only the failure case (NoTemplate)
/// is currently tested.
///
/// TODO: Missing test for leadership loss stopping GetEligibleCandidatesAsync mid-iteration.
/// The method checks !leaderElection.IsLeader at each iteration and yields break, but no test
/// validates this specific leadership-loss bailout behavior.
/// </remarks>
[Trait("Feature", "DispatchStateBuilder")]
public class DispatchStateBuilderTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly LeaderElectionService _leaderElection;

    public DispatchStateBuilderTests()
    {
        var dbName = $"DispatchStateBuilder-{Guid.NewGuid()}";
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
    }

    [Fact]
    public async Task BuildStateAsync_FiltersByTaskType()
    {
        // Arrange: mix of consolidation and regular items
        await InsertWorkItem(Guid.NewGuid(), "regular", WorkItemStatus.Pending, WorkItemTaskType.Implementation);
        await InsertWorkItem(Guid.NewGuid(), "consolidation", WorkItemStatus.Pending, WorkItemTaskType.Consolidation);

        var builder = CreateBuilder();

        // Act: filter for non-consolidation
        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        // Assert
        state.Should().NotBeNull();
        state!.PendingItems.Should().HaveCount(1);
        state.PendingItems[0].AgentSelector.Should().Be("regular");
    }

    [Fact]
    public async Task BuildStateAsync_ReturnsNull_WhenNoPendingItems()
    {
        var builder = CreateBuilder();

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().BeNull();
    }

    [Fact]
    public async Task BuildStateAsync_BuildsConcurrencyMap()
    {
        // Arrange: pending + active items
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Dispatched);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Running);
        await InsertWorkItem(Guid.NewGuid(), "kiro,python", WorkItemStatus.Running);

        var builder = CreateBuilder();

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        state!.ConcurrencyBySelector["kiro,dotnet"].Should().Be(2);
        state.ConcurrencyBySelector["kiro,python"].Should().Be(1);
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_RateLimitHit_StopsIteration()
    {
        // Arrange: 3 pending items, rate limiter allows only 1
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(imageMapping: new() { ["dotnet,kiro"] = "img:latest" });

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        // Rate limiter with 1 token, no replenishment
        using var rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            ReplenishmentPeriod = TimeSpan.FromHours(1),
            TokensPerPeriod = 1,
            AutoReplenishment = false
        });

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, CancellationToken.None))
        {
            candidates.Add(candidate);
        }

        candidates.Should().HaveCount(1, "rate limiter should stop iteration after 1 item");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_ConcurrencyLimitReached_SkipsItem()
    {
        // Arrange: 1 pending + 2 active, max concurrent = 2
        var selector = "kiro,dotnet";
        await InsertWorkItem(Guid.NewGuid(), selector, WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), selector, WorkItemStatus.Dispatched);
        await InsertWorkItem(Guid.NewGuid(), selector, WorkItemStatus.Running);

        var builder = CreateBuilder(
            imageMapping: new() { ["dotnet,kiro"] = "img:latest" },
            maxConcurrent: new() { ["dotnet,kiro"] = 2 });

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, CancellationToken.None))
        {
            candidates.Add(candidate);
        }

        candidates.Should().BeEmpty("concurrency limit of 2 already reached with 2 active items");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_NoTemplate_CallsOnNoTemplateCallback()
    {
        await InsertWorkItem(Guid.NewGuid(), "unknown-selector", WorkItemStatus.Pending);

        var builder = CreateBuilder(imageMapping: new()); // No templates

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();

        PendingWorkItemProjection? failedItem = null;
        string? failedMessage = null;

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (item, message, _) =>
            {
                failedItem = item;
                failedMessage = message;
                return Task.CompletedTask;
            },
            CancellationToken.None))
        {
            candidates.Add(candidate);
        }

        candidates.Should().BeEmpty();
        failedItem.Should().NotBeNull();
        failedItem!.AgentSelector.Should().Be("unknown-selector");
        failedMessage.Should().Contain("No job template for selector");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_NoPvc_SkipsKiroItem()
    {
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        // Builder with empty PVC pool
        var builder = CreateBuilder(
            imageMapping: new() { ["dotnet,kiro"] = "img:latest" },
            pvcPool: []); // No PVCs available

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, CancellationToken.None))
        {
            candidates.Add(candidate);
        }

        candidates.Should().BeEmpty("no PVC available for kiro agent");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_Cancellation_StopsIteration()
    {
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(imageMapping: new() { ["dotnet,kiro"] = "img:latest" });

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();
        using var cts = new CancellationTokenSource();

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, cts.Token))
        {
            candidates.Add(candidate);
            cts.Cancel(); // Cancel after first candidate
        }

        candidates.Should().HaveCount(1, "should stop after cancellation");
    }

    // TODO: Missing test — GetEligibleCandidatesAsync path where ResolveTemplateAsync returns
    // skipItem=true is untested. The merged guard `if (skipItem || template is null)` with inner
    // `if (!skipItem) await onNoTemplate(...)` correctly suppresses the callback when skipItem=true,
    // but no test exercises this branch. If the !skipItem guard were accidentally removed, onNoTemplate
    // would be called spuriously for profile-fallback-skip items and no test would catch it.
    // Add a test: item with selector that triggers concurrency-limit skip in ResolveTemplateAsync
    // → verify onNoTemplate is NOT invoked and the item is not yielded.
    // See review finding: TestQualityReviewer WARNING DispatchStateBuilderTests.cs:273

    // ── IsAtConcurrencyLimit predicate tests ────────────────────────────

    [Fact]
    public void IsAtConcurrencyLimit_NoEntry_ReturnsFalse()
    {
        var concurrency = new Dictionary<string, int>(); // empty — no active runs
        DispatchStateBuilder.IsAtConcurrencyLimit("kiro,dotnet", concurrency, maxConcurrent: 2)
            .Should().BeFalse("no active runs, selector not in map");
    }

    [Fact]
    public void IsAtConcurrencyLimit_AtLimit_ReturnsTrue()
    {
        var concurrency = new Dictionary<string, int> { ["kiro,dotnet"] = 2 };
        DispatchStateBuilder.IsAtConcurrencyLimit("kiro,dotnet", concurrency, maxConcurrent: 2)
            .Should().BeTrue("current == maxConcurrent means at limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_BelowLimit_ReturnsFalse()
    {
        var concurrency = new Dictionary<string, int> { ["kiro,dotnet"] = 1 };
        DispatchStateBuilder.IsAtConcurrencyLimit("kiro,dotnet", concurrency, maxConcurrent: 2)
            .Should().BeFalse("1 active run with limit 2 is below limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_ZeroMaxConcurrent_AlwaysReturnsFalse()
    {
        // maxConcurrent == 0 means no limit is configured
        var concurrency = new Dictionary<string, int> { ["kiro,dotnet"] = 100 };
        DispatchStateBuilder.IsAtConcurrencyLimit("kiro,dotnet", concurrency, maxConcurrent: 0)
            .Should().BeFalse("maxConcurrent=0 means no limit");
    }

    // ── IsKiroAgentWithoutPvc predicate tests ────────────────────────────

    [Fact]
    public void IsKiroAgentWithoutPvc_NonKiroAgent_ReturnsFalse()
    {
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: false, availablePvcs: [])
            .Should().BeFalse("non-kiro agents do not require PVCs");
    }

    [Fact]
    public void IsKiroAgentWithoutPvc_KiroAgentEmptyPool_ReturnsTrue()
    {
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: true, availablePvcs: [])
            .Should().BeTrue("kiro agent with no available PVCs should be skipped");
    }

    [Fact]
    public void IsKiroAgentWithoutPvc_KiroAgentWithPvcs_ReturnsFalse()
    {
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: true, availablePvcs: ["pvc-1"])
            .Should().BeFalse("kiro agent with a PVC available can proceed");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

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
        var lifecycle = new DispatchLifecycleService(
            _mockKubeClient.Object,
            new Infrastructure.Persistence.Services.WorkItemTransitionService(
                _dbFactory, new Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Persistence.Services.WorkItemTransitionService>()),
            new DispatchServiceOptions { KiroPvcPool = pvcPool.ToList() });
        var options = new DispatchServiceOptions { KiroPvcPool = pvcPool.ToList() };

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
