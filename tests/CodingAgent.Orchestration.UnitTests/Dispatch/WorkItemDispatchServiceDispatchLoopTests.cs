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
using CodingAgent.Pipeline.Interfaces;
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
/// Pattern mirrors <see cref="ConsolidationWorkItemDispatchServiceDispatchLoopTests"/>:
/// items are inserted directly as <c>Pending</c> and the DB state is asserted after
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
    /// they are managed by ConsolidationWorkItemDispatchService.
    /// </summary>
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
            "Consolidation items must remain Pending (owned by ConsolidationWorkItemDispatchService)");
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
        string? issueIdentifier = null,
        string? repoProviderConfigId = null,
        WorkItemStatus status = WorkItemStatus.Pending,
        DateTimeOffset? createdAt = null,
        int priorityWeight = 0,
        Guid? projectId = null)
    {
        var resolvedIssueIdentifier = issueIdentifier ?? id.ToString();
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = resolvedIssueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = repoProviderConfigId ?? "",
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
            IssueIdentifier = resolvedIssueIdentifier,
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

    // ── Pre-dispatch eligibility gate (CheckEligibilityAsync) ────────────

    /// <summary>
    /// A Pending Implementation WorkItem whose issue is closed is cancelled before any K8s Job
    /// is created. The gate calls IsIssueClosedAsync via IIssueProvider.
    /// </summary>
    [Fact]
    public async Task CheckEligibility_ImplementationItemWithClosedIssue_ReturnsCancellationReason()
    {
        var item = MakeProjection("issue-42", "ip-1", WorkItemTaskType.Implementation);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // closed

        var mockProviderFactory = new Mock<IProviderFactory>();
        mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var mockConfigStore = new Mock<IProviderConfigStore>();
        mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig { Id = "ip-1", DisplayName = "GH", Kind = ProviderKind.Issue, ProviderType = "GitHub" });

        var handler = CreateHandlerWithGate(mockProviderFactory.Object, mockConfigStore.Object);
        var cache = new Dictionary<(string, string, WorkItemTaskType), bool?>();

        var reason = await handler.CheckEligibilityAsync(item, cache, repoProviderConfigIdFromPayload: null, CancellationToken.None);

        reason.Should().NotBeNull("a closed issue must produce a cancellation reason");
        reason.Should().Contain("Issue", "reason must identify the issue as the cause");
        cache.Should().ContainKey(("ip-1", "issue-42", WorkItemTaskType.Implementation))
            .WhoseValue.Should().BeFalse("result must be cached as ineligible");
    }

    /// <summary>
    /// A Pending Implementation WorkItem whose issue is still open is NOT cancelled.
    /// </summary>
    [Fact]
    public async Task CheckEligibility_ImplementationItemWithOpenIssue_ReturnsNull()
    {
        var item = MakeProjection("issue-42", "ip-1", WorkItemTaskType.Implementation);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // open

        var mockProviderFactory = new Mock<IProviderFactory>();
        mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var mockConfigStore = new Mock<IProviderConfigStore>();
        mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig { Id = "ip-1", DisplayName = "GH", Kind = ProviderKind.Issue, ProviderType = "GitHub" });

        var handler = CreateHandlerWithGate(mockProviderFactory.Object, mockConfigStore.Object);
        var cache = new Dictionary<(string, string, WorkItemTaskType), bool?>();

        var reason = await handler.CheckEligibilityAsync(item, cache, repoProviderConfigIdFromPayload: null, CancellationToken.None);

        reason.Should().BeNull("an open issue must not produce a cancellation reason");
        cache.Should().ContainKey(("ip-1", "issue-42", WorkItemTaskType.Implementation))
            .WhoseValue.Should().BeTrue("open result must be cached as eligible");
    }

    /// <summary>
    /// A Pending Review WorkItem whose PR is closed is cancelled. Uses IPullRequestProvider
    /// (via CreateRepositoryProvider), not IIssueProvider.
    /// </summary>
    [Fact]
    public async Task CheckEligibility_ReviewItemWithClosedPr_ReturnsCancellationReason()
    {
        var item = MakeProjection("101", "ip-1", WorkItemTaskType.Review);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider
            .Setup(p => p.IsPullRequestClosedAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // closed

        var mockProviderFactory = new Mock<IProviderFactory>();
        mockProviderFactory
            .Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);

        var mockConfigStore = new Mock<IProviderConfigStore>();
        mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("rp-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig { Id = "rp-1", DisplayName = "GH Repo", Kind = ProviderKind.Repository, ProviderType = "GitHub" });

        var handler = CreateHandlerWithGate(mockProviderFactory.Object, mockConfigStore.Object);
        var cache = new Dictionary<(string, string, WorkItemTaskType), bool?>();

        var reason = await handler.CheckEligibilityAsync(item, cache, repoProviderConfigIdFromPayload: "rp-1", CancellationToken.None);

        reason.Should().NotBeNull("a closed PR must produce a cancellation reason");
        reason.Should().Contain("PR", "reason must identify the PR as the cause");
        // Verify the gate used CreateRepositoryProvider, NOT CreateIssueProvider
        mockProviderFactory.Verify(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()), Times.Never,
            "Review items must use CreateRepositoryProvider, not CreateIssueProvider");
        mockProviderFactory.Verify(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()), Times.Once);
        // Review eligibility result must be cached under the Review task type key
        cache.Should().ContainKey(("ip-1", "101", WorkItemTaskType.Review))
            .WhoseValue.Should().BeFalse("closed PR result must be cached as ineligible");
    }

    /// <summary>
    /// When two items share the same (provider, identifier) in the same cycle, the upstream
    /// provider is called only once — the second call uses the cache.
    /// </summary>
    [Fact]
    public async Task CheckEligibility_MultipleItemsSameProviderAndIdentifier_OnlyOneUpstreamCall()
    {
        var item1 = MakeProjection("issue-42", "ip-1", WorkItemTaskType.Implementation);
        var item2 = MakeProjection("issue-42", "ip-1", WorkItemTaskType.Implementation);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // open

        var mockProviderFactory = new Mock<IProviderFactory>();
        mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var mockConfigStore = new Mock<IProviderConfigStore>();
        mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig { Id = "ip-1", DisplayName = "GH", Kind = ProviderKind.Issue, ProviderType = "GitHub" });

        var handler = CreateHandlerWithGate(mockProviderFactory.Object, mockConfigStore.Object);
        var cache = new Dictionary<(string, string, WorkItemTaskType), bool?>();

        await handler.CheckEligibilityAsync(item1, cache, repoProviderConfigIdFromPayload: null, CancellationToken.None);
        await handler.CheckEligibilityAsync(item2, cache, repoProviderConfigIdFromPayload: null, CancellationToken.None);

        mockIssueProvider.Verify(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()),
            Times.Once, "upstream provider must be called only once per (provider, identifier, taskType) per cycle");
    }

    /// <summary>
    /// Review and Implementation items with the same numeric identifier under the same provider
    /// do NOT collide in the cache — each uses its own eligibility method independently.
    /// </summary>
    [Fact]
    public async Task CheckEligibility_ReviewAndImplementationSameIdentifier_DoNotCollideInCache()
    {
        // PR #42 is closed; issue #42 is open. Both share provider "ip-1" and identifier "42".
        // Without TaskType in the cache key they would collide; with it they must not.
        var reviewItem = MakeProjection("42", "ip-1", WorkItemTaskType.Review);
        var implItem = MakeProjection("42", "ip-1", WorkItemTaskType.Implementation);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider
            .Setup(p => p.IsPullRequestClosedAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // PR #42 is closed

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // issue #42 is open

        var mockProviderFactory = new Mock<IProviderFactory>();
        mockProviderFactory
            .Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
        mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var mockConfigStore = new Mock<IProviderConfigStore>();
        mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("rp-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig { Id = "rp-1", DisplayName = "GH Repo", Kind = ProviderKind.Repository, ProviderType = "GitHub" });
        mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig { Id = "ip-1", DisplayName = "GH", Kind = ProviderKind.Issue, ProviderType = "GitHub" });

        var handler = CreateHandlerWithGate(mockProviderFactory.Object, mockConfigStore.Object);
        var cache = new Dictionary<(string, string, WorkItemTaskType), bool?>();

        // Check Review item first (PR #42 is closed → cancellation reason)
        var reviewReason = await handler.CheckEligibilityAsync(
            reviewItem, cache, repoProviderConfigIdFromPayload: "rp-1", CancellationToken.None);
        // Then check Implementation item (issue #42 is open → null)
        var implReason = await handler.CheckEligibilityAsync(
            implItem, cache, repoProviderConfigIdFromPayload: null, CancellationToken.None);

        reviewReason.Should().NotBeNull("PR #42 is closed — Review item must be cancelled");
        reviewReason.Should().Contain("PR");
        implReason.Should().BeNull("issue #42 is open — Implementation item must NOT be cancelled");

        // Both upstream providers must have been called independently (no cross-type cache collision)
        mockRepoProvider.Verify(p => p.IsPullRequestClosedAsync(42, It.IsAny<CancellationToken>()), Times.Once,
            "Review item must have called IsPullRequestClosedAsync");
        mockIssueProvider.Verify(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()), Times.Once,
            "Implementation item must have called IsIssueClosedAsync independently");

        // Cache must contain two separate entries keyed by task type
        cache.Should().ContainKey(("ip-1", "42", WorkItemTaskType.Review))
            .WhoseValue.Should().BeFalse("Review result cached as ineligible");
        cache.Should().ContainKey(("ip-1", "42", WorkItemTaskType.Implementation))
            .WhoseValue.Should().BeTrue("Implementation result cached as eligible");
    }

    /// <summary>
    /// When the eligibility check throws (network failure, rate limit, etc.), the gate fails
    /// open: returns null (no cancellation) and does NOT cache as eligible or ineligible.
    /// </summary>
    [Fact]
    public async Task CheckEligibility_WhenProviderThrows_FailsOpenAndDoesNotCache()
    {
        var item = MakeProjection("issue-42", "ip-1", WorkItemTaskType.Implementation);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var mockProviderFactory = new Mock<IProviderFactory>();
        mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var mockConfigStore = new Mock<IProviderConfigStore>();
        mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig { Id = "ip-1", DisplayName = "GH", Kind = ProviderKind.Issue, ProviderType = "GitHub" });

        var handler = CreateHandlerWithGate(mockProviderFactory.Object, mockConfigStore.Object);
        var cache = new Dictionary<(string, string, WorkItemTaskType), bool?>();

        var reason = await handler.CheckEligibilityAsync(item, cache, repoProviderConfigIdFromPayload: null, CancellationToken.None);

        reason.Should().BeNull("an eligibility check failure must fail open (no cancellation)");
        // Cache entry must be null (inconclusive), not true or false
        cache.Should().ContainKey(("ip-1", "issue-42", WorkItemTaskType.Implementation))
            .WhoseValue.Should().BeNull("inconclusive result must be cached as null, not eligible/ineligible");
    }

    /// <summary>
    /// When provider infrastructure is not wired (ProviderFactory = null), the gate
    /// fails open immediately without calling any provider.
    /// </summary>
    [Fact]
    public async Task CheckEligibility_WhenProviderFactoryIsNull_FailsOpen()
    {
        var item = MakeProjection("issue-42", "ip-1", WorkItemTaskType.Implementation);
        // CreateHandler (no gate) — ProviderFactory and ProviderConfigStore are null
        var handler = CreateHandler();
        var cache = new Dictionary<(string, string, WorkItemTaskType), bool?>();

        var reason = await handler.CheckEligibilityAsync(item, cache, repoProviderConfigIdFromPayload: null, CancellationToken.None);

        reason.Should().BeNull("gate must fail open when ProviderFactory is null");
        cache.Should().BeEmpty("no cache entry should be written when gate is skipped");
    }

    /// <summary>
    /// A Pending Implementation WorkItem whose issue is closed is cancelled before K8s Job
    /// creation in the full PollAndDispatchAsync path (end-to-end gate test).
    /// Also verifies that RetryCount is NOT incremented on an eligibility cancellation.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_ImplementationItemWithClosedIssue_CancelledBeforeK8sJob()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Implementation, issueProviderConfigId: "ip-1");

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // closed

        var mockProviderFactory = new Mock<IProviderFactory>();
        mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var mockConfigStore = new Mock<IProviderConfigStore>();
        mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig { Id = "ip-1", DisplayName = "GH", Kind = ProviderKind.Issue, ProviderType = "GitHub" });

        var handler = CreateHandlerWithGate(mockProviderFactory.Object, mockConfigStore.Object, pvcPool: ["pvc-1"]);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        // K8s Job must NOT be created
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "K8s Job must not be created for an item whose issue is closed");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Cancelled,
            "item with closed issue must be cancelled before K8s Job creation");
        item.ErrorMessage.Should().Contain("Issue",
            "cancellation reason must mention the issue");
        item.RetryCount.Should().Be(0,
            "RetryCount must not be incremented on an eligibility cancellation — WorkItemMutationFactory.Cancelled only sets CompletedAt and ErrorMessage");
    }

    /// <summary>
    /// A Pending Review WorkItem whose PR is closed is cancelled before K8s Job creation
    /// in the full PollAndDispatchAsync path (end-to-end gate test).
    /// Verifies that IPullRequestProvider is used (not IIssueProvider), the K8s Job is never
    /// created, and RetryCount is not incremented.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_ReviewItemWithClosedPr_CancelledBeforeK8sJob()
    {
        var id = Guid.NewGuid();
        // Insert a Review WorkItem with a numeric PR identifier and a repo provider config ID
        // in the payload — both are required for CheckReviewItemEligibilityAsync to run.
        await InsertWorkItem(
            id,
            WorkItemTaskType.Review,
            issueIdentifier: "101",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1");

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider
            .Setup(p => p.IsPullRequestClosedAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // PR #101 is closed

        var mockProviderFactory = new Mock<IProviderFactory>();
        mockProviderFactory
            .Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);

        var mockConfigStore = new Mock<IProviderConfigStore>();
        mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("rp-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig { Id = "rp-1", DisplayName = "GH Repo", Kind = ProviderKind.Repository, ProviderType = "GitHub" });

        var handler = CreateHandlerWithGate(mockProviderFactory.Object, mockConfigStore.Object, pvcPool: ["pvc-1"]);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        // K8s Job must NOT be created
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "K8s Job must not be created for a Review item whose PR is closed");

        // Gate must use IPullRequestProvider, NOT IIssueProvider
        mockProviderFactory.Verify(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()), Times.Never,
            "Review gate must use CreateRepositoryProvider, not CreateIssueProvider");
        mockProviderFactory.Verify(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()), Times.Once,
            "Review gate must call CreateRepositoryProvider to check PR state");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var workItem = await db.WorkItems.FindAsync(id);
        workItem!.Status.Should().Be(WorkItemStatus.Cancelled,
            "Review item with closed PR must be cancelled before K8s Job creation");
        workItem.ErrorMessage.Should().Contain("PR",
            "cancellation reason must mention the PR");
        workItem.RetryCount.Should().Be(0,
            "RetryCount must not be incremented on a Review eligibility cancellation");
    }

    // ── CheckEligibilityAsync helpers ─────────────────────────────────────

    private static PendingWorkItemProjection MakeProjection(
        string issueIdentifier,
        string issueProviderConfigId,
        WorkItemTaskType taskType) => new()
        {
            Id = Guid.NewGuid(),
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 300,
            TaskType = taskType,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId
        };

    private WorkItemDispatchService CreateHandlerWithGate(
        IProviderFactory providerFactory,
        IProviderConfigStore providerConfigStore,
        string[]? pvcPool = null)
    {
        pvcPool ??= ["pvc-test-1"];
        var imageMapping = new Dictionary<string, string> { ["dotnet,kiro"] = "ghcr.io/agent:latest" };

        var templates = imageMapping.Select(kv => new JobTemplate
        {
            Labels = kv.Key,
            Image = kv.Value,
            ProviderType = "kiro",
            MaxConcurrent = 0
        }).ToList();

        var templateStore = JobTemplateStore.LoadFromJson(JsonSerializer.Serialize(templates));

        var options = new DispatchServiceOptions
        {
            PollIntervalSeconds = 10,
            RateLimitPerSecond = 100,
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
                stateBuilder,
                ProviderFactory: providerFactory,
                ProviderConfigStore: providerConfigStore),
            options);
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
