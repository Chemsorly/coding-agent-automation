using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Integration tests for K8s mode full pipeline lifecycle:
/// DispatchService creates K8s Jobs from Pending WorkItems,
/// ReconciliationService detects completed/failed/timed-out Jobs and transitions accordingly.
/// Both services share the same InMemory DB to validate end-to-end behavior.
/// </summary>
[Trait("Feature", "K8sLifecycle")]
public class K8sLifecycleIntegrationTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly Mock<IKubernetes> _mockKube;
    private readonly Mock<IBatchV1Operations> _mockBatchV1;
    private readonly Mock<ILabelService> _mockLabelService;
    private readonly LeaderElectionService _leaderElection;

    public K8sLifecycleIntegrationTests()
    {
        var dbName = $"K8sLifecycleIntegration-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockKubeClient = new Mock<IKubernetesJobClient>();
        _mockKube = new Mock<IKubernetes> { DefaultValue = DefaultValue.Mock };
        _mockBatchV1 = new Mock<IBatchV1Operations> { DefaultValue = DefaultValue.Mock };
        _mockKube.Setup(k => k.BatchV1).Returns(_mockBatchV1.Object);
        _mockLabelService = new Mock<ILabelService>();
        _leaderElection = CreateAlwaysLeaderElection();
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Group 1: Full K8s Lifecycle (dispatch → reconcile) ──────────────

    [Fact]
    public async Task K8s_FullLifecycle_PendingItem_DispatchCreatesJob_ReconcileMarksSucceeded()
    {
        // Arrange
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#1", "kiro,dotnet", WorkItemStatus.Pending);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dispatchService = CreateDispatchService();

        // Act: Dispatch creates K8s Job and transitions WorkItem to Dispatched
        await InvokePollAndDispatch(dispatchService);

        // Verify intermediate state: Dispatched with K8sJobName set
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Dispatched);
            item.DispatchedAt.Should().NotBeNull();
            item.K8sJobName.Should().StartWith("caa-");
        }

        // Simulate agent check-in → Running
        await _transitionService.TransitionAsync(workItemId, WorkItemStatus.Running,
            w => { }, CancellationToken.None);

        // Simulate agent completion → Succeeded (this is how the agent API reports success)
        await _transitionService.TransitionAsync(workItemId, WorkItemStatus.Succeeded,
            w => { w.CompletedAt = DateTimeOffset.UtcNow; }, CancellationToken.None);

        // Assert: full lifecycle completed
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
            item.CompletedAt.Should().NotBeNull();
        }

        // Verify: K8s Job was created during dispatch
        _mockKubeClient.Verify(k => k.CreateJobAsync(
            It.Is<V1Job>(j => j.Metadata.Labels["caa/work-item-id"] == workItemId.ToString()),
            "default", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task K8s_FullLifecycle_PendingItem_DispatchCreatesJob_ReconcileMarksFailed()
    {
        // Arrange
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#2", "kiro,dotnet", WorkItemStatus.Pending);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dispatchService = CreateDispatchService();
        var reconciliationService = CreateReconciliationService();

        // Act: Dispatch
        await InvokePollAndDispatch(dispatchService);

        // Get the job name that was assigned
        string k8sJobName;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Dispatched);
            k8sJobName = item.K8sJobName!;
        }

        // Simulate K8s Watch event: Job has Failed condition
        var failedJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = k8sJobName,
                Labels = new Dictionary<string, string>
                {
                    ["caa/work-item-id"] = workItemId.ToString()
                },
                ResourceVersion = "123"
            },
            Status = new V1JobStatus
            {
                Failed = 1,
                Conditions =
                [
                    new V1JobCondition
                    {
                        Type = "Failed",
                        Status = "True",
                        Reason = "BackoffLimitExceeded"
                    }
                ]
            }
        };

        // Invoke the watch handler via reflection
        await InvokeHandleJobEventAsync(reconciliationService, WatchEventType.Modified, failedJob);

        // Assert: WorkItem transitioned to Failed
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
            item.CompletedAt.Should().NotBeNull();
            item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
            item.ErrorMessage.Should().Contain("BackoffLimitExceeded");
        }
    }

    [Fact]
    public async Task K8s_FullLifecycle_JobTimesOut_ReconcileEnforcesTimeout()
    {
        // Arrange: WorkItem dispatched 2 hours ago with 1 hour timeout
        var workItemId = Guid.NewGuid();
        var k8sJobName = $"caa-{workItemId.ToString("N")[..8]}";
        await InsertWorkItem(workItemId, "owner/repo#3", "kiro,dotnet", WorkItemStatus.Dispatched,
            createdAt: DateTimeOffset.UtcNow.AddHours(-2),
            timeoutSeconds: 3600,
            k8sJobName: k8sJobName);

        var reconciliationService = CreateReconciliationService();

        // Act
        await reconciliationService.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert: WorkItem transitioned to Failed with Timeout reason
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.Timeout);
        item.ErrorMessage.Should().Contain("Timeout exceeded");
        item.CompletedAt.Should().NotBeNull();

        // Verify: K8s Job delete was attempted (BatchV1 received a delete call)
        _mockBatchV1.Invocations
            .Should().Contain(i => i.Method.Name.Contains("DeleteNamespacedJob"));
    }

    // ── Group 2: Dispatch Edge Cases ────────────────────────────────────

    [Fact]
    public async Task K8s_Dispatch_NoTemplateMatch_WorkItemFailedImmediately()
    {
        // Arrange: WorkItem with labels that have no template match
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#4", "unmapped-agent", WorkItemStatus.Pending);

        var service = CreateDispatchService(imageMapping: new Dictionary<string, string>
        {
            ["dotnet,kiro"] = "ghcr.io/agent:latest"
        });

        // Act
        await InvokePollAndDispatch(service);

        // Assert: WorkItem transitioned to Failed with clear error
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Contain("No job template");
        item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
    }

    [Fact]
    public async Task K8s_Dispatch_K8sApiConflict_WorkItemStaysDispatched()
    {
        // Arrange: 409 Conflict means job already exists — idempotent success
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#5", "kiro,dotnet", WorkItemStatus.Pending);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException("Conflict")
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Conflict), "")
            });

        var service = CreateDispatchService();

        // Act
        await InvokePollAndDispatch(service);

        // Assert: WorkItem is Dispatched (409 treated as success, job already running)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    [Fact]
    public async Task K8s_Dispatch_MultipleItems_FifoOrdering()
    {
        // Arrange: 3 WorkItems with different CreatedAt timestamps
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        await InsertWorkItem(id1, "owner/repo#oldest", "kiro,dotnet", WorkItemStatus.Pending,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-30));
        await InsertWorkItem(id2, "owner/repo#middle", "kiro,dotnet", WorkItemStatus.Pending,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-15));
        await InsertWorkItem(id3, "owner/repo#newest", "kiro,dotnet", WorkItemStatus.Pending,
            createdAt: DateTimeOffset.UtcNow);

        var dispatchOrder = new List<string>();
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) => dispatchOrder.Add(job.Metadata.Name))
            .Returns(Task.CompletedTask);

        // Use 3 PVCs to support dispatching all 3 kiro agents
        var service = CreateDispatchService(pvcCount: 3);

        // Act
        await InvokePollAndDispatch(service);

        // Assert: dispatched in CreatedAt order (FIFO)
        dispatchOrder.Should().HaveCount(3);
        dispatchOrder[0].Should().Be($"caa-{id1.ToString("N")[..8]}");
        dispatchOrder[1].Should().Be($"caa-{id2.ToString("N")[..8]}");
        dispatchOrder[2].Should().Be($"caa-{id3.ToString("N")[..8]}");
    }

    // ── Group 3: Reconciliation Edge Cases ──────────────────────────────

    [Fact]
    public async Task K8s_Reconcile_OrphanedJob_NoMatchingWorkItem_JobCleaned()
    {
        // Arrange: WorkItem is Dispatched with a K8sJobName, but the K8s Job no longer exists
        var workItemId = Guid.NewGuid();
        var k8sJobName = $"caa-{workItemId.ToString("N")[..8]}";
        await InsertWorkItem(workItemId, "owner/repo#orphan", "kiro,dotnet", WorkItemStatus.Dispatched,
            k8sJobName: k8sJobName);

        // Mock K8s API: ListNamespacedJobWithHttpMessagesAsync returns an empty job list
        _mockBatchV1
            .Setup(b => b.ListNamespacedJobWithHttpMessagesAsync(
                It.IsAny<string>(),       // namespaceParameter
                It.IsAny<bool?>(),        // allowWatchBookmarks
                It.IsAny<string>(),       // continueParameter
                It.IsAny<string>(),       // fieldSelector
                It.IsAny<string>(),       // labelSelector
                It.IsAny<int?>(),         // limit
                It.IsAny<string>(),       // resourceVersion
                It.IsAny<string>(),       // resourceVersionMatch
                It.IsAny<bool?>(),        // sendInitialEvents
                It.IsAny<int?>(),         // timeoutSeconds
                It.IsAny<bool?>(),        // watch
                It.IsAny<bool?>(),        // pretty
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), // customHeaders
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<V1JobList>
            {
                Body = new V1JobList { Items = new List<V1Job>() }
            });

        var reconciliationService = CreateReconciliationService();

        // Act: DetectOrphansAsync finds the WorkItem has no matching K8s Job
        await InvokeDetectOrphansAsync(reconciliationService);

        // Assert: orphaned WorkItem is marked Failed
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
        item.ErrorMessage.Should().Contain("no longer exists");
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task K8s_Reconcile_LabelSwapOnFailure_CallsLabelService()
    {
        // Arrange: Insert a recently-terminal (Failed) WorkItem that still needs label swap
        var workItemId = Guid.NewGuid();
        var issueId = "owner/repo#label-swap";
        var providerConfigId = "github-provider-1";

        await InsertWorkItem(workItemId, issueId, "kiro,dotnet", WorkItemStatus.Failed,
            issueProviderConfigId: providerConfigId,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        _mockLabelService
            .Setup(l => l.SwapLabelAsync(providerConfigId, issueId, "agent:next", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reconciliationService = CreateReconciliationService(withLabelService: true);

        // Act: startup label reconciliation detects recently terminal items and swaps labels
        await InvokeReconcileStartupLabelsAsync(reconciliationService);

        // Assert: ILabelService.SwapLabelAsync was called for the terminal item
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            providerConfigId, issueId, "agent:next", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Service Factories ───────────────────────────────────────────────

    private DispatchService CreateDispatchService(Dictionary<string, string>? imageMapping = null, int pvcCount = 2)
    {
        imageMapping ??= new Dictionary<string, string>
        {
            ["dotnet,kiro"] = "ghcr.io/agent:latest"
        };

        var configData = new Dictionary<string, string?>
        {
            ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "10",
            ["WorkDistribution:Dispatch:RateLimitPerSecond"] = "100",
            ["WorkDistribution:Namespace"] = "default",
            ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
            ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key"
        };

        for (var i = 0; i < pvcCount; i++)
            configData[$"WorkDistribution:CredentialPools:Kiro:{i}"] = $"pvc-test-{i + 1}";

        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        // Build JobTemplateProvider from imageMapping dictionary
        var templateProvider = BuildTemplateProvider(imageMapping);

        return new DispatchService(_dbFactory, _leaderElection, new DispatchLifecycleService(_mockKubeClient.Object, _transitionService, new DispatchServiceOptions
        {
            PollIntervalSeconds = 10,
            RateLimitPerSecond = 100,
            Namespace = "default",
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "agent-api-key",
            KiroPvcPool = Enumerable.Range(0, pvcCount).Select(i => $"pvc-test-{i + 1}").ToList()
        }), _transitionService, config, templateProvider);
    }

    private static JobTemplateProvider BuildTemplateProvider(Dictionary<string, string> imageMapping)
    {
        var templates = imageMapping.Select(kv => new JobTemplate
        {
            Labels = kv.Key,
            Image = kv.Value,
            ProviderType = kv.Key.Contains("kiro") ? "kiro" : "opencode"
        }).ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(templates);
        return JobTemplateProvider.LoadFromJson(json);
    }

    private ReconciliationService CreateReconciliationService(bool withLabelService = false)
    {
        var configData = new Dictionary<string, string?>
        {
            ["WorkDistribution:Reconciliation:PollIntervalSeconds"] = "30",
            ["WorkDistribution:Reconciliation:RetentionDays"] = "7",
            ["WorkDistribution:Namespace"] = "default"
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        return new ReconciliationService(
            _dbFactory, _leaderElection, _mockKube.Object,
            _transitionService, config,
            withLabelService ? _mockLabelService.Object : null);
    }

    // ── Invocation Helpers (reflection for private methods) ──────────────

    private async Task InvokePollAndDispatch(DispatchService service)
    {
        var method = typeof(DispatchService).GetMethod("PollAndDispatchAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;
    }

    private async Task InvokeHandleJobEventAsync(ReconciliationService service, WatchEventType type, V1Job job)
    {
        var method = typeof(ReconciliationService).GetMethod("HandleJobEventAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [type, job, CancellationToken.None])!;
        await task;
    }

    private async Task InvokeDetectOrphansAsync(ReconciliationService service)
    {
        var method = typeof(ReconciliationService).GetMethod("DetectOrphansAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;
    }

    private async Task InvokeReconcileStartupLabelsAsync(ReconciliationService service)
    {
        var method = typeof(ReconciliationService).GetMethod("ReconcileStartupLabelsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;
    }

    // ── Data Helpers ────────────────────────────────────────────────────

    private async Task InsertWorkItem(Guid id, string issueId, string selector, WorkItemStatus status,
        DateTimeOffset? createdAt = null, int timeoutSeconds = 1800,
        string? k8sJobName = null, DateTimeOffset? completedAt = null,
        string? issueProviderConfigId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = issueId,
            IssueProviderConfigId = issueProviderConfigId ?? "provider-1",
            Status = status,
            AgentSelector = selector,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            TimeoutSeconds = timeoutSeconds,
            K8sJobName = k8sJobName,
            CompletedAt = completedAt,
            Payload = "{}"
        };

        if (status == WorkItemStatus.Dispatched)
            entity.DispatchedAt = createdAt?.AddSeconds(5) ?? DateTimeOffset.UtcNow;

        db.WorkItems.Add(entity);
        await db.SaveChangesAsync();
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private static LeaderElectionService CreateAlwaysLeaderElection()
    {
        // TODO: Reflection with null-conditional (?.) silently succeeds if field names change.
        // Consider using Assert.NotNull on field lookups to fail loudly on rename.
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            BindingFlags.NonPublic | BindingFlags.Instance);
        isLeaderField?.SetValue(les, true);

        // Initialize _leaderCts so LeaderToken returns a non-cancelled token
        // TODO: CancellationTokenSource created here is never disposed. Consider disposing
        // LeaderElectionService in test teardown or tracking the CTS for disposal.
        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            BindingFlags.NonPublic | BindingFlags.Instance);
        leaderCtsField?.SetValue(les, new CancellationTokenSource());

        return les;
    }

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
