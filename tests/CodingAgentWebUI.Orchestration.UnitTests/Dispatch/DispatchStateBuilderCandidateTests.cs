using System.Threading.RateLimiting;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Dispatch;
using DispatchLifecycleService = CodingAgentWebUI.Api.Dispatch.DispatchLifecycleService;
using DispatchStateBuilder = CodingAgentWebUI.Api.Dispatch.DispatchStateBuilder;
using DispatchTemplateResolver = CodingAgentWebUI.Api.Dispatch.DispatchTemplateResolver;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="DispatchStateBuilder.GetEligibleCandidatesAsync"/> and the
/// extracted <c>TryResolveCandidateAsync</c> helper (covered through the public method).
/// These tests run in the Orchestration.UnitTests project so that coverage is recorded for
/// CodingAgentWebUI.Orchestration by the project's coverlet.runsettings.
/// </summary>
[Trait("Feature", "DispatchStateBuilder")]
public class DispatchStateBuilderCandidateTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly LeaderElectionService _leaderElection;

    public DispatchStateBuilderCandidateTests()
    {
        var dbName = $"DispatchStateBuilderCandidate-{Guid.NewGuid()}";
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

    // ── TryResolveCandidateAsync — no template path ──────────────────────────

    // TODO: GetEligibleCandidatesAsync_NoTemplate_InvokesOnNoTemplateAndSkipsItem and the equivalent
    // test in DispatchStateBuilderTests (GetEligibleCandidatesAsync_NoTemplate_CallsOnNoTemplateCallback)
    // are functionally identical — same production code path, same in-memory DB setup, different assertion
    // style only. A defect in the onNoTemplate invocation must be fixed in both projects to keep the suite
    // green. Consider consolidating or parameterizing to reduce maintenance overhead.
    // See review finding: TestQualityReviewer WARNING DispatchStateBuilderCandidateTests.cs:69

    [Fact]
    public async Task GetEligibleCandidatesAsync_NoTemplate_InvokesOnNoTemplateAndSkipsItem()
    {
        // Arrange: item with selector that matches no template
        await InsertWorkItem(Guid.NewGuid(), "unknown-selector", WorkItemStatus.Pending);

        var builder = CreateBuilder(imageMapping: new()); // no templates configured

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();
        PendingWorkItemProjection? capturedItem = null;
        string? capturedMessage = null;

        var candidates = new List<DispatchCandidate>();
        await foreach (var c in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "TestCaller",
            (item, msg, _) => { capturedItem = item; capturedMessage = msg; return Task.CompletedTask; },
            CancellationToken.None))
        {
            candidates.Add(c);
        }

        // Assert: no candidate yielded, callback invoked with context
        candidates.Should().BeEmpty("no matching template means the item must be skipped");
        capturedItem.Should().NotBeNull("onNoTemplate callback must be called for unresolvable template");
        capturedMessage.Should().Contain("No job template for selector", "message should identify the problem");
    }

    // ── TryResolveCandidateAsync — kiro agent without PVC ───────────────────

    [Fact]
    public async Task GetEligibleCandidatesAsync_KiroAgentNoPvc_SkipsItemWithoutCallingOnNoTemplate()
    {
        // Arrange: item that resolves to a kiro template but no PVC is available
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(
            imageMapping: new() { ["dotnet,kiro"] = "img:latest" },
            pvcPool: []); // empty PVC pool

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();
        var onNoTemplateCalled = false;

        var candidates = new List<DispatchCandidate>();
        await foreach (var c in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "TestCaller",
            (_, _, _) => { onNoTemplateCalled = true; return Task.CompletedTask; },
            CancellationToken.None))
        {
            candidates.Add(c);
        }

        candidates.Should().BeEmpty("kiro agent without PVC must be skipped");
        onNoTemplateCalled.Should().BeFalse("PVC-skip is not a template-resolution failure — onNoTemplate must not be called");
    }

    // ── TryResolveCandidateAsync — successful candidate resolution ───────────

    [Fact]
    public async Task GetEligibleCandidatesAsync_ValidKiroItem_YieldsCandidate()
    {
        // Arrange: item with matching kiro template and available PVC
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(
            imageMapping: new() { ["dotnet,kiro"] = "img:latest" },
            pvcPool: ["pvc-1"]);

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

        candidates.Should().HaveCount(1);
        candidates[0].IsKiroAgent.Should().BeTrue("the template has kiro providerType");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_ValidOpenCodeItem_YieldsCandidate_WithIsKiroFalse()
    {
        // Arrange: item with matching opencode template (no PVC requirement)
        await InsertWorkItem(Guid.NewGuid(), "opencode,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(
            imageMapping: new() { ["dotnet,opencode"] = "img:latest" });

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

        candidates.Should().HaveCount(1);
        candidates[0].IsKiroAgent.Should().BeFalse("opencode templates are not kiro agents");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

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
        // TODO: This helper uses reflection to set private fields (_isLeader, _leaderCts) on
        // LeaderElectionService. A field rename or backing-store change in LeaderElectionService
        // silently breaks this helper at runtime rather than compile time. The identical pattern
        // exists in DispatchStateBuilderTests.CreateAlwaysLeaderElection. Consider introducing a
        // TestLeaderElectionService stub or a ForceLeader() factory method on the real type so
        // both test files can use a compile-safe seam.
        // See review finding: TestQualityReviewer WARNING DispatchStateBuilderCandidateTests.cs:270
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

    // ── Test infrastructure ──────────────────────────────────────────────────

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
