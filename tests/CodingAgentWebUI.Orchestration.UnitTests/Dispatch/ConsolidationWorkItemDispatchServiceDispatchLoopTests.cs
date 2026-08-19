using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using Xunit;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Dispatch-loop characterization tests for <see cref="ConsolidationWorkItemDispatchService"/>.
/// Covers the rate-limit → eligibility → dispatch loop behavior after migration to
/// <see cref="DispatchStateBuilder.GetEligibleCandidatesAsync"/> (Issue #1989 prerequisite).
/// </summary>
[Trait("Feature", "1989-extract-dispatch-loop")]
public class ConsolidationWorkItemDispatchServiceDispatchLoopTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly LeaderElectionService _leaderElection;
    private readonly Mock<IConsolidationRunStore> _mockRunStore;
    private readonly Mock<IConsolidationService> _mockConsolidationService;
    private readonly Mock<IConsolidationJobPreparationService> _mockJobPreparer;

    public ConsolidationWorkItemDispatchServiceDispatchLoopTests()
    {
        var dbName = $"ConsolidationDispatchLoop-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockKubeClient = new Mock<IKubernetesJobClient>();
        _leaderElection = CreateAlwaysLeaderElection();

        _mockRunStore = new Mock<IConsolidationRunStore>();
        _mockRunStore
            .Setup(s => s.GetByIdAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);

        _mockConsolidationService = new Mock<IConsolidationService>();
        _mockConsolidationService
            .Setup(s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConsolidationService
            .Setup(s => s.TransitionToRunningAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockJobPreparer = new Mock<IConsolidationJobPreparationService>();
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Rate-limit / eligibility / template outcomes ─────────────────────

    /// <summary>
    /// Rate limit hit: when the token bucket is exhausted after the first item,
    /// subsequent consolidation items in the same poll cycle are not dispatched.
    /// </summary>
    [Fact]
    public async Task PollAndDispatchConsolidation_RateLimitExhausted_StopsAfterFirstItem()
    {
        // TODO: Test ordering relies on CreatedAt timestamps (AddMinutes(-5) vs UtcNow) to guarantee
        // that "firstId" is queried and dispatched before "secondId". There is no assertion that the
        // implementation actually sorts pending items by CreatedAt. If the query ordering changes or
        // is non-deterministic on the in-memory EF provider, the "first" dispatched item may not be
        // firstId — both assertions could still pass while actually testing the wrong item. Consider
        // adding an explicit ordering assertion or verifying by IssueIdentifier rather than by position.
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await InsertConsolidationItem(firstId, "run-1", createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await InsertConsolidationItem(secondId, "run-2", createdAt: DateTimeOffset.UtcNow);

        SetupJobPreparer();

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(
            rateLimitPerSecond: 1,
            pvcPool: ["pvc-1"]);

        await handler.PollAndDispatchConsolidationAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var first = await db.WorkItems.FindAsync(firstId);
        var second = await db.WorkItems.FindAsync(secondId);

        first!.Status.Should().Be(WorkItemStatus.Dispatched, "first item should be dispatched before rate limit exhaustion");
        second!.Status.Should().Be(WorkItemStatus.Pending, "second item must stay pending when rate limit is hit");
    }

    /// <summary>
    /// AtConcurrencyLimit outcome: items at concurrency limit are skipped; loop continues
    /// to the next item with a different selector.
    /// </summary>
    [Fact]
    public async Task PollAndDispatchConsolidation_AtConcurrencyLimit_SkipsItemAndContinues()
    {
        // First consolidation item: selector at concurrency limit
        var limitedId = Guid.NewGuid();
        await InsertConsolidationItem(limitedId, "run-limited", agentSelector: "kiro,dotnet",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        // Second consolidation item: different selector (not at limit)
        var freeId = Guid.NewGuid();
        await InsertConsolidationItem(freeId, "run-free", agentSelector: "kiro,python",
            createdAt: DateTimeOffset.UtcNow);
        // Existing running consolidation job consumes the kiro,dotnet concurrency slot
        await InsertConsolidationItem(Guid.NewGuid(), "run-running", agentSelector: "kiro,dotnet",
            status: WorkItemStatus.Running);

        SetupJobPreparer();
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest", ["kiro,python"] = "ghcr.io/agent:python" },
            maxConcurrentPods: new() { ["dotnet,kiro"] = 1 },
            pvcPool: ["pvc-1"]);

        await handler.PollAndDispatchConsolidationAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var limited = await db.WorkItems.FindAsync(limitedId);
        var free = await db.WorkItems.FindAsync(freeId);

        limited!.Status.Should().Be(WorkItemStatus.Pending, "concurrency-limited item must remain pending");
        free!.Status.Should().Be(WorkItemStatus.Dispatched, "loop must continue past skipped item and dispatch the next eligible one");
    }

    /// <summary>
    /// NoTemplate outcome: consolidation item with unresolvable selector is failed AND
    /// the failure cascades to the ConsolidationRun; loop continues to next item.
    /// </summary>
    [Fact]
    public async Task PollAndDispatchConsolidation_NoTemplate_FailsItemWithCascadeAndContinues()
    {
        // Item with unknown selector first
        var unknownId = Guid.NewGuid();
        await InsertConsolidationItem(unknownId, "run-unknown", agentSelector: "unknown-label",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        // Valid item second
        var validId = Guid.NewGuid();
        await InsertConsolidationItem(validId, "run-valid", agentSelector: "kiro,dotnet",
            createdAt: DateTimeOffset.UtcNow);

        SetupJobPreparer();
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(pvcPool: ["pvc-1"]);

        await handler.PollAndDispatchConsolidationAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var unknown = await db.WorkItems.FindAsync(unknownId);
        var valid = await db.WorkItems.FindAsync(validId);

        unknown!.Status.Should().Be(WorkItemStatus.Failed,
            "NoTemplate item must be transitioned to Failed via FailConsolidationWorkItemAsync");
        unknown.ErrorMessage.Should().Contain("No job template",
            "error message must identify the missing template");
        valid!.Status.Should().Be(WorkItemStatus.Dispatched,
            "loop must continue after a NoTemplate failure and dispatch the next eligible item");

        // Cascade: IConsolidationService.UpdateRunAsync must have been called to fail the consolidation run
        _mockConsolidationService.Verify(s => s.UpdateRunAsync(
            (RunId)"run-unknown",
            ConsolidationRunStatus.Failed,
            It.Is<string?>(msg => msg != null && msg.Contains("No job template")),
            It.IsAny<CancellationToken>()), Times.Once,
            "failure must cascade to the ConsolidationRun via IConsolidationService");
    }

    /// <summary>
    /// Eligible outcome: standard happy path — consolidation item dispatched as K8s Job
    /// and transitioned to Dispatched.
    /// </summary>
    [Fact]
    public async Task PollAndDispatchConsolidation_EligibleItem_DispatchesJobAndTransitionsToDispatched()
    {
        var workItemId = Guid.NewGuid();
        await InsertConsolidationItem(workItemId, "run-eligible");

        SetupJobPreparer();
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(pvcPool: ["pvc-1"]);

        await handler.PollAndDispatchConsolidationAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
        _mockKubeClient.Verify(k => k.CreateJobAsync(
            It.IsAny<k8s.Models.V1Job>(), "default", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void SetupJobPreparer()
    {
        _mockJobPreparer
            .Setup(p => p.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationJobPreparationResult
            {
                ProviderConfigs = new List<ProviderConfig>(),
                RepoProviderConfigId = "repo-provider-1"
            });
    }

    private ConsolidationWorkItemDispatchService CreateHandler(
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

        var templateProvider = JobTemplateStore.LoadFromJson(JsonSerializer.Serialize(templates));

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
            _dbFactory, lifecycle, templateProvider,
            new DispatchTemplateResolver(null, templateProvider),
            options);

        return new ConsolidationWorkItemDispatchService(
            new ConsolidationWorkItemDispatchServiceDependencies(
                _dbFactory, _leaderElection, lifecycle, templateProvider,
                Mock.Of<Microsoft.Extensions.Configuration.IConfiguration>(),
                _transitionService,
                ConsolidationRunStore: _mockRunStore.Object,
                ConsolidationService: _mockConsolidationService.Object,
                ConsolidationJobPreparer: _mockJobPreparer.Object,
                StateBuilder: stateBuilder),
            options);
    }

    private async Task InsertConsolidationItem(
        Guid id,
        string runId,
        string agentSelector = "kiro,dotnet",
        WorkItemStatus status = WorkItemStatus.Pending,
        DateTimeOffset? createdAt = null)
    {
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = agentSelector,
            TimeoutSeconds = 300,
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            RunId = runId
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            Status = status,
            AgentSelector = agentSelector,
            TaskType = WorkItemTaskType.Consolidation,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            TimeoutSeconds = 300,
            Payload = JsonSerializer.Serialize(payload, PipelineJsonOptions.Default)
        });
        await db.SaveChangesAsync();
    }

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

    // ── Test infrastructure ───────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
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
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
