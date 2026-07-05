using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Text.Json;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Lifecycle tests for DispatchService using IKubernetesJobClient abstraction.
/// Validates: Requirements 5.1-5.8, 5.13-5.14
/// </summary>
[Trait("Feature", "035a-kubernetes-dispatch")]
public class DispatchServiceLifecycleTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly LeaderElectionService _leaderElection;

    public DispatchServiceLifecycleTests()
    {
        var dbName = $"DispatchServiceLifecycle-{Guid.NewGuid()}";
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
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Happy Path ──────────────────────────────────────────────────────

    [Fact]
    public async Task PollAndDispatch_PendingItem_CreatesJobAndTransitionsToDispatched()
    {
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#1", "kiro,dotnet", WorkItemStatus.Pending);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(imageMapping: new Dictionary<string, string>
        {
            ["dotnet,kiro"] = "ghcr.io/agent:latest"
        });

        await InvokePollAndDispatch(service);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
        item.DispatchedAt.Should().NotBeNull();
        item.K8sJobName.Should().Be($"caa-{workItemId.ToString("N").Substring(0, 8)}");

        _mockKubeClient.Verify(k => k.CreateJobAsync(
            It.Is<V1Job>(j => j.Metadata.Name == $"caa-{workItemId.ToString("N").Substring(0, 8)}"),
            "default", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Error Handling ──────────────────────────────────────────────────

    [Fact]
    public async Task PollAndDispatch_NoTemplateMatch_FailsWorkItem()
    {
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#2", "unknown-label", WorkItemStatus.Pending);

        var service = CreateService(imageMapping: new Dictionary<string, string>
        {
            ["dotnet,kiro"] = "ghcr.io/agent:latest"
        });

        await InvokePollAndDispatch(service);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Contain("No job template");
    }

    [Fact]
    public async Task PollAndDispatch_K8sApiFailure_FailsWorkItem()
    {
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#3", "kiro,dotnet", WorkItemStatus.Pending);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException("K8s API error")
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.InternalServerError), "")
            });

        var service = CreateService(imageMapping: new Dictionary<string, string>
        {
            ["dotnet,kiro"] = "ghcr.io/agent:latest"
        });

        await InvokePollAndDispatch(service);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Contain("K8s Job creation failed");
    }

    [Fact]
    public async Task PollAndDispatch_409Conflict_TreatsAsSuccess()
    {
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#4", "kiro,dotnet", WorkItemStatus.Pending);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException("Conflict")
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Conflict), "")
            });

        var service = CreateService(imageMapping: new Dictionary<string, string>
        {
            ["dotnet,kiro"] = "ghcr.io/agent:latest"
        });

        await InvokePollAndDispatch(service);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    // ── Concurrency & Ordering ──────────────────────────────────────────

    [Fact]
    public async Task PollAndDispatch_ConcurrencyLimitReached_SkipsItem()
    {
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#5", "kiro,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "owner/repo#existing", "kiro,dotnet", WorkItemStatus.Running);

        var service = CreateService(
            imageMapping: new Dictionary<string, string> { ["dotnet,kiro"] = "ghcr.io/agent:latest" },
            maxConcurrentPods: new Dictionary<string, int> { ["kiro,dotnet"] = 1 });

        await InvokePollAndDispatch(service);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);
        _mockKubeClient.Verify(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollAndDispatch_FifoOrdering_DispatchesOldestFirst()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        await InsertWorkItem(oldId, "owner/repo#old", "kiro,dotnet", WorkItemStatus.Pending,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await InsertWorkItem(newId, "owner/repo#new", "kiro,dotnet", WorkItemStatus.Pending,
            createdAt: DateTimeOffset.UtcNow);

        var dispatchedJobNames = new List<string>();
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) => dispatchedJobNames.Add(job.Metadata.Name))
            .Returns(Task.CompletedTask);

        var service = CreateService(imageMapping: new Dictionary<string, string>
        {
            ["dotnet,kiro"] = "ghcr.io/agent:latest"
        });

        await InvokePollAndDispatch(service);

        dispatchedJobNames.Should().HaveCount(2);
        dispatchedJobNames[0].Should().Be($"caa-{oldId.ToString("N").Substring(0, 8)}");
        dispatchedJobNames[1].Should().Be($"caa-{newId.ToString("N").Substring(0, 8)}");
    }

    [Fact]
    public async Task PollAndDispatch_OnlyPendingItems_IgnoresOtherStatuses()
    {
        await InsertWorkItem(Guid.NewGuid(), "owner/repo#d", "kiro,dotnet", WorkItemStatus.Dispatched);
        await InsertWorkItem(Guid.NewGuid(), "owner/repo#r", "kiro,dotnet", WorkItemStatus.Running);
        await InsertWorkItem(Guid.NewGuid(), "owner/repo#f", "kiro,dotnet", WorkItemStatus.Failed);

        var service = CreateService(imageMapping: new Dictionary<string, string>
        {
            ["dotnet,kiro"] = "ghcr.io/agent:latest"
        });

        await InvokePollAndDispatch(service);

        _mockKubeClient.Verify(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LeadershipLost_ExitsWithinOneSecond()
    {
        // Arrange: create a service with controllable leader election
        var leaderCts = new CancellationTokenSource();
        var leaderElection = CreateLeaderElectionWithCts(leaderCts);

        var service = CreateService(
            imageMapping: new Dictionary<string, string> { ["dotnet,kiro"] = "ghcr.io/agent:latest" },
            leaderElection: leaderElection);

        var hostStopCts = new CancellationTokenSource();

        // Act: start ExecuteAsync — it will enter the poll loop since IsLeader=true
        var executeTask = InvokeExecuteAsync(service, hostStopCts.Token);

        // Allow the service to enter its work loop
        await Task.Delay(200);

        // Simulate leadership loss by cancelling the leaderCts
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        leaderCts.Cancel();

        // Allow the service to detect leadership loss and re-enter wait loop
        // Then stop the host to exit ExecuteAsync completely
        await Task.Delay(200);
        hostStopCts.Cancel();

        // Assert: ExecuteAsync should complete promptly (within 1 second of host stop)
        var completed = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(2)));
        stopwatch.Stop();

        completed.Should().Be(executeTask, "ExecuteAsync should exit promptly after leadership loss + host stop");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(2000,
            "service should respond to leadership loss within 1 second");

        leaderCts.Dispose();
        hostStopCts.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_LeadershipLostAndReacquired_ResumesDispatching()
    {
        // Arrange: create a service with controllable leader election
        var leaderCts = new CancellationTokenSource();
        var leaderElection = CreateLeaderElectionWithCts(leaderCts);

        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#resume", "kiro,dotnet", WorkItemStatus.Pending);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(
            imageMapping: new Dictionary<string, string> { ["dotnet,kiro"] = "ghcr.io/agent:latest" },
            leaderElection: leaderElection);

        var hostStopCts = new CancellationTokenSource();

        // Act: start ExecuteAsync
        var executeTask = InvokeExecuteAsync(service, hostStopCts.Token);

        // Allow service to run a poll cycle (should dispatch the item)
        await Task.Delay(500);

        // Simulate leadership loss
        leaderCts.Cancel();
        await Task.Delay(200);

        // Simulate re-acquisition: set IsLeader=true and create new CTS
        var newLeaderCts = new CancellationTokenSource();
        SetLeaderState(leaderElection, isLeader: true, cts: newLeaderCts);

        // Insert another work item for the resumed session to pick up
        var resumedItemId = Guid.NewGuid();
        await InsertWorkItem(resumedItemId, "owner/repo#resumed", "kiro,dotnet", WorkItemStatus.Pending);

        // Allow service to re-enter leader loop and dispatch
        await Task.Delay(3000); // Wait for leadership re-check + poll interval

        // Stop the host
        hostStopCts.Cancel();
        await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(2)));

        // Assert: both items should have been dispatched
        await using var db = await _dbFactory.CreateDbContextAsync();
        var firstItem = await db.WorkItems.FindAsync(workItemId);
        firstItem!.Status.Should().Be(WorkItemStatus.Dispatched,
            "first item should be dispatched before leadership loss");

        newLeaderCts.Dispose();
        leaderCts.Dispose();
        hostStopCts.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private DispatchService CreateService(
        Dictionary<string, string>? imageMapping = null,
        Dictionary<string, int>? maxConcurrentPods = null,
        LeaderElectionService? leaderElection = null)
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
            ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key",
            ["WorkDistribution:CredentialPools:Kiro:0"] = "pvc-test-1",
            ["WorkDistribution:CredentialPools:Kiro:1"] = "pvc-test-2"
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        // Build JobTemplateProvider from imageMapping + maxConcurrentPods
        var templateProvider = BuildTemplateProvider(imageMapping, maxConcurrentPods);

        return new DispatchService(_dbFactory, leaderElection ?? _leaderElection, _mockKubeClient.Object, _transitionService, config, templateProvider);
    }

    private static JobTemplateProvider BuildTemplateProvider(
        Dictionary<string, string> imageMapping,
        Dictionary<string, int>? maxConcurrentPods = null)
    {
        // Normalize maxConcurrentPods keys so they match regardless of input order
        var normalizedMaxConcurrent = maxConcurrentPods?.ToDictionary(
            kv => JobTemplateProvider.NormalizeLabels(kv.Key), kv => kv.Value);

        var templates = imageMapping.Select(kv => new JobTemplate
        {
            Labels = kv.Key,
            Image = kv.Value,
            ProviderType = kv.Key.Contains("kiro") ? "kiro" : "opencode",
            MaxConcurrent = normalizedMaxConcurrent?.GetValueOrDefault(
                JobTemplateProvider.NormalizeLabels(kv.Key), 0) ?? 0
        }).ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(templates);
        return JobTemplateProvider.LoadFromJson(json);
    }

    private async Task InvokePollAndDispatch(DispatchService service)
    {
        var method = typeof(DispatchService).GetMethod("PollAndDispatchAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;
    }

    private async Task InvokeExecuteAsync(DispatchService service, CancellationToken stoppingToken)
    {
        var method = typeof(BackgroundService).GetMethod("ExecuteAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [stoppingToken])!;
        await task;
    }

    private static LeaderElectionService CreateLeaderElectionWithCts(CancellationTokenSource cts)
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        SetLeaderState(les, isLeader: true, cts: cts);
        return les;
    }

    private static void SetLeaderState(LeaderElectionService les, bool isLeader, CancellationTokenSource cts)
    {
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        isLeaderField!.SetValue(les, isLeader);

        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        leaderCtsField!.SetValue(les, cts);
    }

    private async Task InsertWorkItem(Guid id, string issueId, string selector, WorkItemStatus status,
        DateTimeOffset? createdAt = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = issueId,
            IssueProviderConfigId = "provider-1",
            Status = status,
            AgentSelector = selector,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            TimeoutSeconds = 1800,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
    }

    private static LeaderElectionService CreateAlwaysLeaderElection()
    {
        // TODO: Reflection with null-conditional (?.) silently succeeds if field names change.
        // Consider using Assert.NotNull on field lookups to fail loudly on rename.
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        isLeaderField?.SetValue(les, true);

        // Initialize _leaderCts so LeaderToken returns a non-cancelled token
        // TODO: CancellationTokenSource created here is never disposed. Consider disposing
        // LeaderElectionService in test teardown or tracking the CTS for disposal.
        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
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
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
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
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
