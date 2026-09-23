using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="WorkItemStatusTransitionService.TransitionAsync"/>.
///
/// These tests validate that the orchestration logic extracted from
/// <c>WorkItemAgentEndpoints.PostStatus</c> behaves correctly in isolation:
/// infra-recovery guard, pre-read idempotency guard, lifecycle dispatch, telemetry,
/// and FailureReason validation.
///
/// Uses the same in-memory DB pattern as <see cref="PostStatusIdempotencyTests"/>
/// since <see cref="WorkItemTransitionService.TransitionDetailedAsync"/> is not on an interface.
/// </summary>
[Collection("PostStatusIdempotencyCollection")]
public sealed class WorkItemStatusTransitionServiceTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"TransitionSvc-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task<WorkItemEntity> SeedWorkItemAsync(
        DbContextOptions<PipelineDbContext> opts,
        WorkItemStatus status,
        DateTimeOffset? completedAt = null,
        FailureReason? failureReason = null)
    {
        var item = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = $"org/repo#{Guid.NewGuid():N}",
            IssueProviderConfigId = "ip-1",
            Status = status,
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = completedAt,
            FailureReason = failureReason,
        };
        await using var ctx = new TestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();
        return item;
    }

    private static WorkItemTransitionService CreateTransitionService(DbContextOptions<PipelineDbContext> opts)
        => new(new TestDbContextFactory(opts), NullLogger<WorkItemTransitionService>.Instance);

    private static IDbContextFactory<PipelineDbContext> CreateDbFactory(DbContextOptions<PipelineDbContext> opts)
        => new TestDbContextFactory(opts);

    private static WorkItemStatusTransitionService CreateService(
        DbContextOptions<PipelineDbContext> opts,
        IRunLifecycleManager lifecycleManager,
        IDbContextFactory<PipelineDbContext>? dbFactory = null)
        => new(CreateTransitionService(opts), lifecycleManager, dbFactory ?? CreateDbFactory(opts));

    // ── Infrastructure-recovery path ─────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_Running_InfraRecoverySucceeds_ReturnsTransitioned_NoLifecycleCall()
    {
        // Arrange: Failed/Timeout item — infra recovery should recover it to Running
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            failureReason: FailureReason.Timeout);

        // Strict mock: lifecycle must NOT be called on recovery path
        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Running };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned,
            "infra recovery returns true → Transitioned");
        lifecycleManager.VerifyNoOtherCalls();

        await using var db = new TestPipelineDbContext(opts);
        var updated = await db.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Running,
            "DB must reflect the recovered status");
    }

    [Fact]
    public async Task TransitionAsync_Running_InfraRecoveryFails_FallsThroughAndReturnsRejected()
    {
        // Arrange: Failed/AgentError — not recoverable; falls through to TransitionDetailedAsync
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            failureReason: FailureReason.AgentError);

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = CreateService(opts, lifecycleManager);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Running };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert: recovery failed; TransitionDetailedAsync also rejects Failed→Running
        outcome.Should().Be(StatusTransitionOutcome.Rejected,
            "Failed/AgentError is not recoverable; TransitionDetailedAsync rejects Failed→Running");
    }

    // ── Pre-read idempotency guard ─────────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_FailedOnAlreadyCancelled_ReturnsAlreadyAtTarget_NoLifecycleCall()
    {
        // Arrange: already Cancelled item receiving PostStatus(Failed)
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Cancelled,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert: pre-read guard short-circuits
        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget,
            "pre-read guard returns AlreadyAtTarget for incoming terminal on already-Cancelled item");
        lifecycleManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TransitionAsync_FailedOnAlreadySucceeded_ReturnsAlreadyAtTarget_NoLifecycleCall()
    {
        // Arrange: already Succeeded item receiving PostStatus(Failed)
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Succeeded,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget,
            "pre-read guard returns AlreadyAtTarget for incoming terminal on already-Succeeded item");
        lifecycleManager.VerifyNoOtherCalls();
    }

    // ── Lifecycle dispatch ─────────────────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_Failed_RealTransition_CallsFailRunAsync()
    {
        // Arrange: Running → Failed — a real terminal transition
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, ErrorMessage = "infra killed it" };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        lifecycleManager.Verify(
            m => m.FailRunAsync(
                It.Is<RunId>(r => r.Value == item.Id.ToString()),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.Is<FailureReason?>(fr => fr == FailureReason.InfrastructureFailure)),
            Times.Once,
            "FailRunAsync must be called with InfrastructureFailure on terminal Failed transition");
    }

    [Fact]
    public async Task TransitionAsync_Cancelled_RealTransition_CallsCancelRunAsync()
    {
        // Arrange: Running → Cancelled
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.CancelRunAsync(
                It.IsAny<RunId>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Cancelled };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        lifecycleManager.Verify(
            m => m.CancelRunAsync(
                It.Is<RunId>(r => r.Value == item.Id.ToString()),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()),
            Times.Once,
            "CancelRunAsync must be called on terminal Cancelled transition");
    }

    // ── NotFound and Rejected paths ────────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_NonExistentItem_ReturnsNotFound()
    {
        // Arrange: empty DB
        var opts = CreateDbOptions();
        await using (var ctx = new TestPipelineDbContext(opts))
            ctx.Database.EnsureCreated();

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = CreateService(opts, lifecycleManager);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded };

        // Act
        var outcome = await svc.TransitionAsync(Guid.NewGuid(), request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.NotFound);
    }

    [Fact]
    public async Task TransitionAsync_InvalidTransition_ReturnsRejected()
    {
        // Arrange: Pending → Succeeded is invalid
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = CreateService(opts, lifecycleManager);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Rejected);
    }

    // ── FailureReason IsDefined guard ──────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_UndefinedNumericFailureReason_PersistsAgentError()
    {
        // "99" parses via Enum.TryParse but is not a named member; IsDefined guard must reject it
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager.Object, dbFactory);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, FailureReason = "99" };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);

        await using var verifyCtx = new TestPipelineDbContext(opts);
        var persisted = await verifyCtx.WorkItems.FindAsync(item.Id);
        persisted!.FailureReason.Should().Be(FailureReason.AgentError,
            "the IsDefined guard must reject undefined numeric FailureReason and fall back to AgentError");
    }

    [Fact]
    public async Task TransitionAsync_ValidNamedFailureReason_PersistsCorrectly()
    {
        // "AgentError" is a named member — must pass through the IsDefined guard
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager.Object, dbFactory);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, FailureReason = "AgentError" };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);

        await using var verifyCtx = new TestPipelineDbContext(opts);
        var persisted = await verifyCtx.WorkItems.FindAsync(item.Id);
        persisted!.FailureReason.Should().Be(FailureReason.AgentError,
            "a valid named FailureReason must be persisted correctly");
    }

    // ── Telemetry emission (via awaitTelemetry seam) ───────────────────────────

    [Fact]
    public async Task TransitionAsync_ActualTerminalTransition_EmitsTelemetryCounter()
    {
        // Arrange: Running → Succeeded — telemetry must fire
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        long terminatedCount = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName
                && instrument.Name == "workdistribution.workitems_terminated")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
            Interlocked.Increment(ref terminatedCount));
        listener.Start();

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager, dbFactory);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded };

        // Act — awaitTelemetry: true for deterministic assertion
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        terminatedCount.Should().BeGreaterThanOrEqualTo(1,
            "workdistribution.workitems_terminated must increment on a real terminal transition");
    }

    [Fact]
    public async Task TransitionAsync_AlreadyAtTarget_DoesNotEmitTelemetry()
    {
        // Arrange: already Cancelled — AlreadyAtTarget must not emit telemetry
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Cancelled,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var dbFactory = CreateDbFactory(opts);

        var measurements = new System.Collections.Concurrent.ConcurrentBag<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName
                && instrument.Name == "workdistribution.workitems_terminated")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => measurements.Add(value));
        listener.Start();

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager.Object, dbFactory);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget);
        measurements.Should().BeEmpty(
            "workdistribution.workitems_terminated must not increment for an AlreadyAtTarget no-op");
        lifecycleManager.VerifyNoOtherCalls();
    }

    // ── Test Infrastructure ────────────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rv = entityType.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexes = entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList();
                foreach (var idx in indexes)
                    entityType.RemoveIndex(idx);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_opts);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
