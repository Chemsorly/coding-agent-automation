using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests that PendingWorkItemDrainService dispatch latency metrics use OriginalEnqueuedAt
/// when present, falling back to CreatedAt for legacy work items (issue #1379).
/// </summary>
[Collection("Metrics")]
[Trait("Feature", "DispatchLatencyMetrics")]
public sealed class PendingWorkItemDrainServiceMetricsTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly Mock<IConsolidationDispatcher> _mockConsolidationDispatcher = new();
    private readonly Mock<IConsolidationRunStore> _mockConsolidationRunStore = new();
    private readonly OrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;

    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<double> _dispatchLatencies = [];
    private readonly ConcurrentBag<double> _pendingDurations = [];

    public PendingWorkItemDrainServiceMetricsTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DrainMetrics_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockPendingWork.Setup(p => p.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>().AsReadOnly());

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

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public async Task PipelineDispatch_UsesOriginalEnqueuedAt_WhenPresent()
    {
        // Arrange: OriginalEnqueuedAt is 60s before CreatedAt (simulating re-dispatch)
        var workItemId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var originalEnqueuedAt = now.AddSeconds(-60);
        var createdAt = now.AddSeconds(-10);

        await InsertWorkItem(workItemId, createdAt, originalEnqueuedAt, WorkItemTaskType.Implementation);

        SetupAgentResolver();
        _mockAgentComm
            .Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: latency should be >= 55s (UtcNow - OriginalEnqueuedAt), not ~10s (UtcNow - CreatedAt)
        _dispatchLatencies.Should().Contain(v => v >= 55.0, "latency should reflect OriginalEnqueuedAt (60s ago), not CreatedAt (10s ago)");
        _pendingDurations.Should().Contain(v => v >= 55.0, "pending duration should reflect OriginalEnqueuedAt");
    }

    [Fact]
    public async Task PipelineDispatch_FallsBackToCreatedAt_WhenOriginalEnqueuedAtIsNull()
    {
        // Arrange: OriginalEnqueuedAt is null (pre-migration legacy row)
        var workItemId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-15);

        await InsertWorkItem(workItemId, createdAt, originalEnqueuedAt: null, WorkItemTaskType.Implementation);

        SetupAgentResolver();
        _mockAgentComm
            .Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: latency should be ~15s (UtcNow - CreatedAt)
        _dispatchLatencies.Should().Contain(v => v >= 10.0 && v < 50.0, "latency should fall back to CreatedAt (15s ago)");
    }

    [Fact]
    public async Task ConsolidationDispatch_UsesOriginalEnqueuedAt_WhenPresent()
    {
        // Arrange: OriginalEnqueuedAt is 90s before CreatedAt (simulating re-dispatch of consolidation item)
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();
        var now = DateTimeOffset.UtcNow;
        var originalEnqueuedAt = now.AddSeconds(-90);
        var createdAt = now.AddSeconds(-5);

        await InsertWorkItem(workItemId, createdAt, originalEnqueuedAt, WorkItemTaskType.Consolidation,
            issueIdentifier: runId, issueProviderConfigId: "consolidation");

        SetupAgentResolver();

        _mockConsolidationRunStore
            .Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = runId,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Queued
            });

        _mockConsolidationDispatcher
            .Setup(d => d.TryDispatchToAgentAsync(
                It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = CreateServiceWithConsolidation();

        // Act
        await InvokeDrainAsync(service);

        // Assert: latency should be ~90s (UtcNow - OriginalEnqueuedAt), not ~5s
        _dispatchLatencies.Should().Contain(v => v >= 85.0, "consolidation latency should reflect OriginalEnqueuedAt (90s ago)");
        _pendingDurations.Should().Contain(v => v >= 85.0, "consolidation pending duration should reflect OriginalEnqueuedAt");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void SetupAgentResolver()
    {
        _mockResolver
            .Setup(r => r.ResolveAgent(It.IsAny<string>()))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
    }

    private PendingWorkItemDrainService CreateService()
    {
        return new PendingWorkItemDrainService(
            _dbFactory,
            _mockResolver.Object,
            _mockAgentComm.Object,
            _runService,
            _transitionService,
            _mockPendingWork.Object,
            _mockLabelService.Object,
            NullLogger<PendingWorkItemDrainService>.Instance,
            _mockProjectStore.Object);
    }

    private PendingWorkItemDrainService CreateServiceWithConsolidation()
    {
        return new PendingWorkItemDrainService(
            _dbFactory,
            _mockResolver.Object,
            _mockAgentComm.Object,
            _runService,
            _transitionService,
            _mockPendingWork.Object,
            _mockLabelService.Object,
            NullLogger<PendingWorkItemDrainService>.Instance,
            _mockProjectStore.Object,
            _mockConsolidationDispatcher.Object,
            _mockConsolidationRunStore.Object);
    }

    private async Task InsertWorkItem(
        Guid id,
        DateTimeOffset createdAt,
        DateTimeOffset? originalEnqueuedAt,
        WorkItemTaskType taskType,
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
            AgentSelector = "",
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
            AgentSelector = "",
            TaskType = taskType,
            CreatedAt = createdAt,
            OriginalEnqueuedAt = originalEnqueuedAt,
            TimeoutSeconds = 3600,
            Payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default)
        });
        await db.SaveChangesAsync();
    }

    // TODO: Task.Delay(3000) is non-deterministic — under CI load the drain loop may not complete within 3s,
    // causing assertions to find zero recorded metrics. Consider a signal-based approach (e.g., waiting on a
    // semaphore released when AssignJob is called) or polling for the expected metric count with a timeout.
    private static async Task InvokeDrainAsync(PendingWorkItemDrainService service)
    {
        using var cts = new CancellationTokenSource();
        service.Signal();
        var task = service.StartAsync(cts.Token);
        await Task.Delay(3000);
        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { }
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new PipelineDbContext(_options));
    }
}
