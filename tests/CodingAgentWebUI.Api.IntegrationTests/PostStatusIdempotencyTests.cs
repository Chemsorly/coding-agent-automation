using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Direct unit tests for <see cref="WorkItemEndpoints.PostStatus"/> covering the idempotent
/// already-at-terminal-state path (issue #2226).
///
/// These tests call the internal static method directly rather than going through the HTTP stack
/// so that:
/// 1. The <see cref="WorkDistributionTelemetry.WorkItemsTerminated"/> counter assertion is
///    synchronous — no fire-and-forget timing concern.
/// 2. The lifecycle manager calls can be tracked via a recording stub without Moq.
/// 3. The test can assert 404 vs 400 without configuring a full WebApplicationFactory.
/// </summary>
public sealed class PostStatusIdempotencyTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"PostStatusIdempotency-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task<WorkItemEntity> SeedWorkItemAsync(
        DbContextOptions<PipelineDbContext> opts,
        WorkItemStatus status,
        DateTimeOffset? completedAt = null)
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

    // ── Primary acceptance criterion — idempotent no-op does NOT emit telemetry ──

    /// <summary>
    /// Regression test for issue #2226.
    /// PostStatus called with a status that matches the item's current terminal state
    /// must NOT call LogTerminalStatus and must NOT increment workdistribution.workitems_terminated.
    ///
    /// Two structural assertions guard this:
    /// 1. Pre-assert that <see cref="WorkItemTransitionService.TransitionDetailedAsync"/> returns
    ///    <see cref="TransitionResult.AlreadyAtTarget"/> for the seeded item — this proves the
    ///    guarded block inside PostStatus is never entered, making the counter and log assertions
    ///    timing-independent rather than relying on a Task.Delay safety window.
    /// 2. The Serilog global logger is temporarily replaced with a <see cref="CapturingSink"/>
    ///    to assert that no "WorkItem terminal:" log event is emitted (acceptance criterion #2).
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task PostStatus_AlreadyAtTerminalState_DoesNotIncrementTerminatedCounter(WorkItemStatus terminal)
    {
        // Arrange
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, terminal, completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var transitionService = CreateTransitionService(opts);
        var dbFactory = CreateDbFactory(opts);

        // CRITICAL FIX (issue #2226, TestQualityReviewer): Structural pre-assertion — confirm that
        // TransitionDetailedAsync returns AlreadyAtTarget for the seeded item. This proves the guard
        // inside PostStatus (`if (transitionResult == TransitionResult.Transitioned)`) is never
        // entered, making all subsequent negative assertions timing-independent rather than relying
        // on a Task.Delay safety window that may expire before a regressed fire-and-forget task runs.
        var preconditionResult = await transitionService.TransitionDetailedAsync(item.Id, terminal);
        preconditionResult.Should().Be(TransitionResult.AlreadyAtTarget,
            $"seeded item is already at {terminal} — TransitionDetailedAsync must return AlreadyAtTarget, " +
            "confirming that PostStatus will take the no-op path and never enter the telemetry block");

        // Use ConcurrentBag (thread-safe) per brain pitfall #35 — static meter is shared
        // across all tests in the process. Start the listener immediately before the call
        // to minimise the window for cross-test interference.
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

        // CRITICAL FIX (issue #2226, TestQualityReviewer): Replace the Serilog global logger with
        // a capturing sink to assert that no "WorkItem terminal:" log event is emitted
        // (acceptance criterion #2: log must not be emitted for an already-terminal WorkItem).
        // LogTerminalStatus calls Serilog.Log.Information("WorkItem terminal: ...") directly, so
        // the global logger must be intercepted rather than a DI-injected ILogger<T>.
        // The previous logger is restored in the finally block to avoid polluting other tests.
        var capturingSink = new CapturingSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(capturingSink)
            .CreateLogger();

        var request = new WorkItemStatusRequest { Status = terminal };
        var runService = new Mock<IOrchestratorRunService>().Object;
        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;

        try
        {
            // Act — call PostStatus directly (not via HTTP)
            var result = await WorkItemEndpoints.PostStatus(
                item.Id, request, transitionService, runService, lifecycleManager, dbFactory);

            // Assert — structural guard proven above; no timing dependency on Task.Delay.
            // Both the counter and the log line must be absent because the
            // `if (transitionResult == TransitionResult.Transitioned)` block is never entered.
            result.Should().BeOfType<Ok>("an idempotent PostStatus must still return 200");
            measurements.Should().BeEmpty(
                $"workdistribution.workitems_terminated must not increment when PostStatus is a no-op for already-{terminal} item");
            capturingSink.Events
                .Should().NotContain(
                    e => e.MessageTemplate.Text.Contains("WorkItem terminal:"),
                    $"the \"WorkItem terminal:\" log line must not be emitted from PostStatus for an already-{terminal} item (acceptance criterion #2)");
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    // ── Primary acceptance criterion — idempotent no-op does NOT call lifecycle manager ──

    /// <summary>
    /// Regression test for issue #2226.
    /// PostStatus called for an already-terminal item must NOT invoke RunLifecycleManager events
    /// (FailRunAsync / CancelRunAsync) — those trigger label-swap, history writes, and dedup guards.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task PostStatus_AlreadyAtTerminalState_DoesNotCallLifecycleManager(WorkItemStatus terminal)
    {
        // Arrange
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, terminal, completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var transitionService = CreateTransitionService(opts);

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        // Strict mock: any unexpected call fails the test.
        // On the idempotent path, neither FailRunAsync nor CancelRunAsync should be called.

        var runService = new Mock<IOrchestratorRunService>().Object;
        var request = new WorkItemStatusRequest { Status = terminal };

        // Act
        var result = await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager.Object, null);

        // Assert
        result.Should().BeOfType<Ok>("idempotent PostStatus must return 200");
        // TODO: dbFactory is null here. On the AlreadyAtTarget path this is fine because
        // EmitTerminalStatusTelemetryAsync is never called. However, if the guard regresses and
        // the fire-and-forget task is launched with a null factory, the test won't catch a
        // NullReferenceException inside that task (it runs after VerifyNoOtherCalls). Consider
        // passing a real dbFactory here so a regressed implementation would surface the failure.
        lifecycleManager.VerifyNoOtherCalls();
    }

    // ── 404 path via richer return type ──────────────────────────────────────

    /// <summary>
    /// PostStatus for a non-existent WorkItem must return 404 (not 400).
    /// With the richer TransitionResult return type, this no longer requires a secondary DB read —
    /// TransitionDetailedAsync returns NotFound directly.
    /// Previously, when dbFactory was null, the code fell through to BadRequest — a latent bug now fixed.
    /// </summary>
    [Fact]
    public async Task PostStatus_NonExistentWorkItem_Returns404()
    {
        // Arrange: empty DB
        var opts = CreateDbOptions();
        await using (var ctx = new TestPipelineDbContext(opts))
            ctx.Database.EnsureCreated();

        var transitionService = CreateTransitionService(opts);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded };
        var runService = new Mock<IOrchestratorRunService>().Object;
        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;

        // Act — dbFactory is null (no secondary DB read path)
        var result = await WorkItemEndpoints.PostStatus(
            Guid.NewGuid(), request, transitionService, runService, lifecycleManager, null);

        // Assert
        result.Should().BeOfType<NotFound>(
            "a non-existent WorkItem must return 404, not 400, even when dbFactory is null");
    }

    // ── 400 path via richer return type ──────────────────────────────────────

    [Fact]
    public async Task PostStatus_InvalidTransition_Returns400()
    {
        // Arrange: Pending → Succeeded is invalid per IsValidTransition
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var transitionService = CreateTransitionService(opts);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded };
        var runService = new Mock<IOrchestratorRunService>().Object;
        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;

        // Act
        var result = await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager, null);

        // Assert
        result.Should().BeOfType<BadRequest<string>>("an invalid transition must return 400");
    }

    // ── Actual transition path DOES emit telemetry (regression guard) ─────────

    /// <summary>
    /// On a real Running → Succeeded transition, the counter MUST increment.
    /// This guards against over-suppressing telemetry.
    /// </summary>
    [Fact]
    public async Task PostStatus_ActualTerminalTransition_EmitsTelemetry()
    {
        // Arrange
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var transitionService = CreateTransitionService(opts);
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

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded };
        var runService = new Mock<IOrchestratorRunService>().Object;
        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;

        // Act
        var result = await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager, dbFactory);

        // EmitTerminalStatusTelemetryAsync is fire-and-forget — wait for it
        // TODO: 200 ms is a timing-dependent wait for the fire-and-forget telemetry task. This can
        // produce flaky false-negatives on a slow CI machine (task hasn't run yet when assertion fires)
        // or false-positives if the task is never queued. Consider refactoring EmitTerminalStatusTelemetryAsync
        // to return the Task (store it in a local) so it can be awaited directly in tests, or use a
        // TaskCompletionSource/ManualResetEventSlim signalled from within the telemetry path.
        await Task.Delay(200);

        // Assert
        result.Should().BeOfType<Ok>();
        terminatedCount.Should().BeGreaterThanOrEqualTo(1,
            "workdistribution.workitems_terminated must increment on a real terminal transition");
    }

    // ── LifecycleManager IS called on a real terminal transition ─────────────

    [Fact]
    public async Task PostStatus_ActualFailedTransition_CallsFailRunAsync()
    {
        // Arrange: Running → Failed is a real transition; FailRunAsync must be invoked
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var transitionService = CreateTransitionService(opts);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var runService = new Mock<IOrchestratorRunService>().Object;
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, ErrorMessage = "test error" };

        // Act
        // TODO: dbFactory is null here. On the Transitioned path (Running→Failed), PostStatus launches
        // EmitTerminalStatusTelemetryAsync as a fire-and-forget task with the null factory. If
        // EmitTerminalStatusTelemetryAsync dereferences dbFactory unconditionally, this produces a silent
        // unobserved NullReferenceException inside the background task — invisible to the test harness.
        // The test only verifies lifecycle manager invocations, not the telemetry side-effect.
        // Pass a real dbFactory here so that a null-dereference regression in the telemetry path
        // surfaces as an observable unobserved task exception.
        await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager.Object, null);

        // Assert
        lifecycleManager.Verify(
            m => m.FailRunAsync(
                It.Is<RunId>(r => r.Value == item.Id.ToString()),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>()),
            Times.Once,
            "FailRunAsync must be called exactly once on a real Running→Failed transition");
    }

    [Fact]
    public async Task PostStatus_ActualCancelledTransition_CallsCancelRunAsync()
    {
        // Arrange: Running → Cancelled is a real transition; CancelRunAsync must be invoked
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var transitionService = CreateTransitionService(opts);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.CancelRunAsync(
                It.IsAny<RunId>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync((PipelineRun?)null);

        var runService = new Mock<IOrchestratorRunService>().Object;
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Cancelled };

        // Act
        // TODO: dbFactory is null here — same concern as PostStatus_ActualFailedTransition_CallsFailRunAsync.
        // The fire-and-forget EmitTerminalStatusTelemetryAsync task will receive a null factory on the
        // Transitioned path. Pass a real dbFactory to surface any null-dereference regression in the
        // background task as an observable failure.
        await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager.Object, null);

        // Assert
        lifecycleManager.Verify(
            m => m.CancelRunAsync(
                It.Is<RunId>(r => r.Value == item.Id.ToString()),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()),
            Times.Once,
            "CancelRunAsync must be called exactly once on a real Running→Cancelled transition");
    }

    // ── Test Infrastructure ───────────────────────────────────────────────────

    /// <summary>
    /// A Serilog sink that accumulates emitted <see cref="LogEvent"/> instances for assertion.
    /// Used to verify that no "WorkItem terminal:" log event is emitted on the idempotent path
    /// (acceptance criterion #2 of issue #2226).
    /// Thread-safe: <see cref="Emit"/> is called from the logging pipeline and may be invoked
    /// concurrently, so events are stored in a <see cref="System.Collections.Concurrent.ConcurrentBag{T}"/>.
    /// </summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }

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
                var indexes = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
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
