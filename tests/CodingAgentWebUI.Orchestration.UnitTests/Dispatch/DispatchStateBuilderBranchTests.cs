using System.Threading.RateLimiting;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Dispatch;
using DispatchLifecycleService = CodingAgentWebUI.Api.Dispatch.DispatchLifecycleService;
using DispatchStateBuilder = CodingAgentWebUI.Api.Dispatch.DispatchStateBuilder;
using DispatchTemplateResolver = CodingAgentWebUI.Api.Dispatch.DispatchTemplateResolver;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

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
