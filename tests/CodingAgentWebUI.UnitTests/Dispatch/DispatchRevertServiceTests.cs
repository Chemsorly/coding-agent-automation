using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Characterization tests for <see cref="DispatchRevertService"/>.
/// Tests written before extraction to lock in behavior (#1871 prerequisite).
/// </summary>
public sealed class DispatchRevertServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly OrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;

    public DispatchRevertServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DispatchRevertServiceTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    // ── TryRevertToPendingAsync ──────────────────────────────────────────

    [Fact]
    public async Task TryRevertToPendingAsync_IncrementTrue_IncrementsRetryCount()
    {
        var workItemId = await InsertDispatchedItem(retryCount: 0);
        var handler = CreateHandler();

        await handler.TryRevertToPendingAsync(workItemId, incrementRetryCount: true);

        var item = await GetItem(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);
        item.RetryCount.Should().Be(1, "pipeline failures must increment RetryCount");
        item.DispatchedAt.Should().BeNull();
        item.AssignedAgentId.Should().BeNull();
    }

    [Fact]
    public async Task TryRevertToPendingAsync_IncrementFalse_LeavesRetryCountUnchanged()
    {
        // Consolidation failures must NOT increment RetryCount
        var workItemId = await InsertDispatchedItem(retryCount: 2);
        var handler = CreateHandler();

        await handler.TryRevertToPendingAsync(workItemId, incrementRetryCount: false);

        var item = await GetItem(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);
        item.RetryCount.Should().Be(2, "consolidation failures must NOT increment RetryCount");
    }

    [Fact]
    public async Task TryRevertToPendingAsync_TransitionThrows_ExceptionSwallowed()
    {
        var workItemId = await InsertDispatchedItem(retryCount: 0);
        var throwingFactory = new AlwaysThrowingDbContextFactory();
        var failingTransition = new WorkItemTransitionService(throwingFactory, NullLogger<WorkItemTransitionService>.Instance);
        var handler = new DispatchRevertService(_dbFactory, _mockResolver.Object, _runService, failingTransition, NullLogger<DispatchRevertService>.Instance);

        // Exception must be swallowed — stuck-item detector handles recovery
        var act = async () => await handler.TryRevertToPendingAsync(workItemId, incrementRetryCount: true);
        await act.Should().NotThrowAsync("TryRevertToPendingAsync must swallow transition exceptions");
    }

    [Fact]
    public async Task TryRevertToPendingAsync_CancelledToken_ExceptionSwallowed_ItemStaysDispatched()
    {
        var workItemId = await InsertDispatchedItem(retryCount: 0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancellationAwareFactory = new CancellationAwareDbContextFactory(_dbOptions);
        var transitionService = new WorkItemTransitionService(cancellationAwareFactory, NullLogger<WorkItemTransitionService>.Instance);
        var handler = new DispatchRevertService(_dbFactory, _mockResolver.Object, _runService, transitionService, NullLogger<DispatchRevertService>.Instance);

        // OCE from cancelled token must be swallowed
        var act = async () => await handler.TryRevertToPendingAsync(workItemId, incrementRetryCount: false, cts.Token);
        await act.Should().NotThrowAsync("TryRevertToPendingAsync must swallow OperationCanceledException");

        // Item remains Dispatched — stuck-item detector handles recovery
        var item = await GetItem(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    // ── HandlePipelineDispatchFailureAsync ──────────────────────────────

    [Fact]
    public async Task HandlePipelineDispatchFailureAsync_DispatchedSuccessfully_ReleasesAgent_RemovesRun_RevertsToPending()
    {
        var workItemId = await InsertDispatchedItem(retryCount: 0);
        var runId = workItemId.ToString();

        // Pre-register a run in memory
        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "title",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        });
        _runService.AddRun(existingRun);
        _mockResolver.Setup(r => r.ReleaseAgent(It.IsAny<AgentId>()));

        var item = await GetItem(workItemId);
        var request = BuildRequest(runId);
        var handler = CreateHandler();

        // Act: dispatch succeeded (Dispatched transition ran), then AssignJobAsync threw
        await handler.HandlePipelineDispatchFailureAsync(
            item!, request, (AgentId)"agent-1", dispatchedSuccessfully: true,
            new InvalidOperationException("SignalR delivery failed"));

        // Assert: agent released
        _mockResolver.Verify(r => r.ReleaseAgent((AgentId)"agent-1"), Times.Once);
        // Run removed from in-memory
        _runService.GetRun(runId).Should().BeNull("run must be removed after dispatch failure");
        // WorkItem reverted to Pending with RetryCount incremented
        var stored = await GetItem(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Pending);
        stored.RetryCount.Should().Be(1, "pipeline dispatch failure must increment RetryCount");
    }

    [Fact]
    public async Task HandlePipelineDispatchFailureAsync_DispatchTransitionFailed_ReleasesAgent_IncrementsRetryCountDirectly()
    {
        // When the Dispatched DB transition itself fails, item is still Pending.
        // The revert path's idempotent behavior skips the RetryCount++ callback.
        // The handler must fall back to a direct DB increment.
        var workItemId = await InsertPendingItem(retryCount: 0);
        var runId = workItemId.ToString();
        _mockResolver.Setup(r => r.ReleaseAgent(It.IsAny<AgentId>()));

        var item = await GetItem(workItemId);
        var request = BuildRequest(runId);
        var handler = CreateHandler();

        // Act: dispatchedSuccessfully = false (Dispatched transition failed)
        await handler.HandlePipelineDispatchFailureAsync(
            item!, request, (AgentId)"agent-1", dispatchedSuccessfully: false,
            new DbUpdateException("Constraint violation", new Exception()));

        // Assert: agent released, NO run removed (never registered), RetryCount incremented
        _mockResolver.Verify(r => r.ReleaseAgent((AgentId)"agent-1"), Times.Once);
        var stored = await GetItem(workItemId);
        // RetryCount must be incremented despite the idempotent revert (which skips the callback)
        // TODO: Strengthen assertion to Be(1) instead of BeGreaterThan(0). Initial retryCount is 0
        // and the expected increment is exactly 1. BeGreaterThan(0) would pass if RetryCount were
        // incremented by 2 or more (e.g. double-increment bug). Be(1) pins the invariant precisely.
        stored!.RetryCount.Should().BeGreaterThan(0, "RetryCount must be incremented even when Dispatched transition failed");
    }

    [Fact]
    public async Task HandlePipelineDispatchFailureAsync_DispatchedSuccessfully_WithNullRunId_DoesNotRemoveRun()
    {
        var workItemId = await InsertDispatchedItem(retryCount: 0);
        _mockResolver.Setup(r => r.ReleaseAgent(It.IsAny<AgentId>()));

        var item = await GetItem(workItemId);
        // Request with null RunId — no run to remove
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = null,
            TimeoutSeconds = 3600
        };
        var handler = CreateHandler();

        // Act: should not throw even with null RunId
        var act = async () => await handler.HandlePipelineDispatchFailureAsync(
            item!, request, (AgentId)"agent-1", dispatchedSuccessfully: true,
            new InvalidOperationException("failed"));
        await act.Should().NotThrowAsync();
    }

    // ── EnsureInMemoryRunRegistered ──────────────────────────────────────

    [Fact]
    public async Task EnsureInMemoryRunRegistered_RunExistsInMemory_UpdatesAgentId()
    {
        var workItemId = await InsertDispatchedItem(retryCount: 0);
        var runId = workItemId.ToString();
        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "title",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        });
        _runService.AddRun(existingRun);

        var item = await GetItem(workItemId);
        var request = BuildRequest(runId);
        var handler = CreateHandler();
        var dispatchTime = DateTimeOffset.UtcNow;

        // Act
        handler.EnsureInMemoryRunRegistered(request, "agent-42", dispatchTime, item!);

        // Assert: existing run gets AgentId updated
        var run = _runService.GetRun(runId);
        run.Should().NotBeNull();
        run!.AgentId.Should().Be("agent-42", "existing run AgentId must be updated at dispatch time");
        // BUG-14 fix: PostDispatchTimingCorrection must update StartedAt to actual dispatch time
        // TODO: Add a pre-condition assertion that existingRun.StartedAtOffset is NOT close to dispatchTime
        // before the call (e.g., create existingRun with StartedAt hours in the past). Without a before/after
        // contrast, BeCloseTo(dispatchTime) here cannot distinguish "StartedAt was updated from an old value"
        // from "StartedAt was already close to dispatchTime at construction". A regression removing
        // PostDispatchTimingCorrection would still pass if the run was created with a recent StartedAt. (#2065)
        run.StartedAtOffset.Should().BeCloseTo(dispatchTime, TimeSpan.FromSeconds(1),
            "PostDispatchTimingCorrection must reset StartedAt to dispatch time, not preparation/enqueue time");
    }

    [Fact]
    public async Task EnsureInMemoryRunRegistered_RunNotInMemory_CreatesNewRun()
    {
        var workItemId = await InsertDispatchedItem(retryCount: 0);
        var runId = workItemId.ToString();
        // No run pre-registered

        var item = await GetItem(workItemId);
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = runId,
            RunType = PipelineRunType.Implementation,
            TimeoutSeconds = 3600,
            IssueDetail = new IssueDetail { Identifier = "org/repo#42", Title = "Fix bug", Description = "", Labels = [] }
        };
        var handler = CreateHandler();
        var dispatchTime = DateTimeOffset.UtcNow;

        // Act
        handler.EnsureInMemoryRunRegistered(request, "agent-42", dispatchTime, item!);

        // Assert: run created from request data
        var run = _runService.GetRun(runId);
        run.Should().NotBeNull("a new run must be created for orchestrator restart recovery");
        run!.AgentId.Should().Be("agent-42");
        // Verify the run was created from the correct request data
        run.RunId.Should().Be(runId);
        run.RunType.Should().Be(PipelineRunType.Implementation);
        // BUG-14 fix: PostDispatchTimingCorrection must update StartedAt even on re-created runs
        // TODO: This assertion cannot reliably detect a missing PostDispatchTimingCorrection call:
        // PipelineRun.CreateImplementation likely defaults StartedAt to DateTimeOffset.UtcNow, which is
        // already within 1 second of dispatchTime (captured just before the synchronous call). If
        // PostDispatchTimingCorrection were removed, the assertion would still pass because the creation
        // time and dispatchTime are nearly identical. Fix by capturing a stale creation time and asserting
        // the pre-call StartedAtOffset was different, or by using a clock abstraction with a frozen value. (#2065)
        run.StartedAtOffset.Should().BeCloseTo(dispatchTime, TimeSpan.FromSeconds(1),
            "PostDispatchTimingCorrection must reset StartedAt to dispatch time after re-creation");
    }

    [Fact]
    public async Task EnsureInMemoryRunRegistered_NullRunId_DoesNothing()
    {
        var workItemId = await InsertDispatchedItem(retryCount: 0);
        var item = await GetItem(workItemId);
        var request = BuildRequest(null); // null RunId
        var handler = CreateHandler();

        // Act: should be a no-op
        var act = () => handler.EnsureInMemoryRunRegistered(request, "agent-1", DateTimeOffset.UtcNow, item!);
        act.Should().NotThrow("null RunId must be handled gracefully");

        // No run was registered
        _runService.GetActiveRuns().Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private DispatchRevertService CreateHandler() =>
        new(_dbFactory, _mockResolver.Object, _runService, _transitionService, NullLogger<DispatchRevertService>.Instance);

    private static JobDistributionRequest BuildRequest(string? runId) =>
        new()
        {
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = runId,
            TimeoutSeconds = 3600
        };

    private async Task<Guid> InsertDispatchedItem(int retryCount)
    {
        var id = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = "",
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = DateTimeOffset.UtcNow,
            AssignedAgentId = "agent-1",
            TimeoutSeconds = 3600,
            RetryCount = retryCount
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> InsertPendingItem(int retryCount)
    {
        var id = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Pending,
            Payload = "{}",
            AgentSelector = "",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            RetryCount = retryCount
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<WorkItemEntity?> GetItem(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkItems.FindAsync(id);
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new PipelineDbContext(_options));
    }

    private sealed class AlwaysThrowingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => throw new InvalidOperationException("Simulated DB failure");
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated DB failure");
    }

    private sealed class CancellationAwareDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public CancellationAwareDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PipelineDbContext(_options));
        }
    }
}
