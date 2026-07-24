using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests that dispatch latency metrics use OriginalEnqueuedAt when present,
/// falling back to CreatedAt for legacy work items (issue #1379).
/// </summary>
[Collection("Metrics")]
[Trait("Feature", "DispatchLatencyMetrics")]
public class DispatchServiceMetricsTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient = new();
    private readonly Mock<IAgentProfileStore> _mockAgentProfileStore = new();
    private readonly LeaderElectionService _leaderElection;

    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<double> _dispatchLatencies = [];
    private readonly ConcurrentBag<double> _pendingDurations = [];

    public DispatchServiceMetricsTests()
    {
        var dbName = $"DispatchMetrics-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _leaderElection = CreateAlwaysLeaderElection();

        SetupDefaultMocks();

        // Listen for WorkDistribution metrics
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "workdistribution.dispatch_latency_seconds")
                _dispatchLatencies.Add(measurement);
            else if (instrument.Name == "workdistribution.workitems_pending_duration_seconds")
                _pendingDurations.Add(measurement);
        });

        _listener.Start();
    }

    // TODO: Dispose _leaderElection — LeaderElectionService holds a CancellationTokenSource that won't be cleaned up.
    public void Dispose()
    {
        _listener.Dispose();
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task PipelineDispatch_UsesOriginalEnqueuedAt_WhenPresent()
    {
        // Arrange: OriginalEnqueuedAt is 60s before CreatedAt (simulating re-dispatch)
        var workItemId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var originalEnqueuedAt = now.AddSeconds(-60);
        var createdAt = now.AddSeconds(-10);

        await InsertWorkItem(workItemId, createdAt, originalEnqueuedAt, WorkItemTaskType.Implementation, "kiro,dotnet");

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await InvokePollAndDispatch(service);

        // Assert: latency should be ~60s (DispatchedAt - OriginalEnqueuedAt), not ~10s (DispatchedAt - CreatedAt)
        _dispatchLatencies.Should().Contain(v => v >= 55.0, "latency should reflect OriginalEnqueuedAt (60s ago), not CreatedAt (10s ago)");
        _pendingDurations.Should().Contain(v => v >= 55.0, "pending duration should reflect OriginalEnqueuedAt");
    }

    [Fact]
    public async Task PipelineDispatch_FallsBackToCreatedAt_WhenOriginalEnqueuedAtIsNull()
    {
        // Arrange: OriginalEnqueuedAt is null (pre-migration legacy row)
        var workItemId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-15);

        await InsertWorkItem(workItemId, createdAt, originalEnqueuedAt: null, WorkItemTaskType.Implementation, "kiro,dotnet");

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await InvokePollAndDispatch(service);

        // Assert: latency should be ~15s (DispatchedAt - CreatedAt)
        // TODO: Upper bound (v < 50.0) may cause flaky failures under CI load if elapsed time exceeds threshold. Consider asserting only the lower bound or using a higher upper bound.
        _dispatchLatencies.Should().Contain(v => v >= 10.0 && v < 50.0, "latency should fall back to CreatedAt (15s ago)");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void SetupDefaultMocks()
    {
        // Agent profile store: return a profile matching "kiro,dotnet"
        _mockAgentProfileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new()
                {
                    Id = "profile-1",
                    DisplayName = "Kiro Dotnet",
                    Enabled = true,
                    MatchLabels = ["dotnet", "kiro"],
                    AgentProviderConfigId = "agent-provider-001",
                    Priority = 1
                }
            });
    }

    private DispatchService CreateService()
    {
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
        var templateProvider = BuildTemplateProvider();

        var options = new DispatchServiceOptions
        {
            PollIntervalSeconds = 10,
            RateLimitPerSecond = 100,
            Namespace = "default",
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "agent-api-key",
            KiroPvcPool = new List<string> { "pvc-test-1", "pvc-test-2" }
        };

        var lifecycle = new DispatchLifecycleService(
            _mockKubeClient.Object,
            _transitionService,
            options);

        return new DispatchService(
            _dbFactory, _leaderElection, lifecycle, _transitionService, config, templateProvider,
            null,
            _mockAgentProfileStore.Object,
            runService: null);
    }

    private static JobTemplateProvider BuildTemplateProvider()
    {
        var templates = new List<JobTemplate>
        {
            new() { Labels = "dotnet,kiro", Image = "ghcr.io/agent:latest", ProviderType = "kiro" }
        };
        var json = JsonSerializer.Serialize(templates);
        return JobTemplateProvider.LoadFromJson(json);
    }

    private async Task InsertWorkItem(
        Guid id,
        DateTimeOffset createdAt,
        DateTimeOffset? originalEnqueuedAt,
        WorkItemTaskType taskType,
        string selector,
        string issueIdentifier = "org/repo#42",
        string issueProviderConfigId = "issue-provider-1")
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = taskType,
            AgentSelector = selector,
            TimeoutSeconds = 3600,
            RunId = id.ToString(),
            ConsolidationRunType = taskType == WorkItemTaskType.Consolidation ? ConsolidationRunType.BrainConsolidation : null,
            ConsolidationTemplateId = taskType == WorkItemTaskType.Consolidation ? "template-001" : null,
            ConsolidationWorkspacePath = taskType == WorkItemTaskType.Consolidation ? "/tmp/test" : null
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            Status = WorkItemStatus.Pending,
            AgentSelector = selector,
            TaskType = taskType,
            CreatedAt = createdAt,
            OriginalEnqueuedAt = originalEnqueuedAt,
            TimeoutSeconds = 3600,
            Payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default)
        });
        await db.SaveChangesAsync();
    }

    // TODO: Consider making PollAndDispatchAsync internal with [InternalsVisibleTo] instead of reflection.
    // If the method is renamed, GetMethod returns null and the ! operator throws NRE with an unhelpful error.
    private static async Task InvokePollAndDispatch(DispatchService service)
    {
        var method = typeof(DispatchService).GetMethod("PollAndDispatchAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;
    }

    private static LeaderElectionService CreateAlwaysLeaderElection()
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            BindingFlags.NonPublic | BindingFlags.Instance);
        isLeaderField?.SetValue(les, true);

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
