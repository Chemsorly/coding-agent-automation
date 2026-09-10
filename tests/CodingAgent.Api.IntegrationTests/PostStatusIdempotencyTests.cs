using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Collection definition that disables parallel execution for <see cref="PostStatusIdempotencyTests"/>.
/// Several tests in that class subscribe to the global <c>workdistribution.workitems_terminated</c>
/// meter via <see cref="MeterListener"/> and then wait 200 ms for a fire-and-forget background task
/// to emit. When tests run concurrently, a background task from one test fires into another test's
/// active listener, causing spurious tag captures (e.g. "AgentError" appearing in the
/// NumericUndefined test's bag). Serialising the class eliminates that cross-test pollution.
/// </summary>
[CollectionDefinition("PostStatusIdempotencyCollection", DisableParallelization = true)]
public sealed class PostStatusIdempotencyCollection { }

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
[Collection("PostStatusIdempotencyCollection")]
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

        // Act — pass awaitTelemetry: true so PostStatus awaits EmitTerminalStatusTelemetryAsync
        // before returning. This eliminates the Task.Delay(200) race: the metric is recorded
        // synchronously (from the test's perspective) before the assertion runs.
        var result = await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager, dbFactory,
            ct: default, awaitTelemetry: true);

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

    // ── Infrastructure recovery from Failed/Timeout race (issue #2459) ──────────

    /// <summary>
    /// Regression test for issue #2459.
    /// When a WorkItem is in Failed state with FailureReason=Timeout (set by ReconciliationLoop's
    /// EnforceTimeoutsAsync during a race with the agent's in-flight PostStatus(Running)),
    /// PostStatus(Running) must return HTTP 200 and transition the item to Running via
    /// TryRecoverFromInfrastructureFailureAsync — not return 400 and silently drop the update.
    ///
    /// Primary assertion: DB state read-back confirms item.Status == Running. HTTP 200 alone is
    /// insufficient because TryRecoverFromInfrastructureFailureAsync returns true both on a real
    /// transition AND when the item is already at the target (idempotent path). Only the DB
    /// read-back proves the transition actually occurred from Failed/Timeout.
    ///
    /// The "Invalid transition" warning is NOT emitted for this scenario: PostStatus now calls
    /// TryRecoverFromInfrastructureFailureAsync before TransitionDetailedAsync for Running requests,
    /// so TransitionCoreAsync (which emits the warning) is never reached when recovery succeeds.
    /// </summary>
    [Fact]
    public async Task WhenItemIsFailedWithTimeoutReason_PostStatusRunning_RecoversThroughInfrastructureRecovery()
    {
        // Arrange: seed a WorkItem in Failed state with FailureReason=Timeout to replicate the
        // race condition where ReconciliationLoop timed out the item while the agent was still running.
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            failureReason: FailureReason.Timeout);
        var transitionService = CreateTransitionService(opts);
        var runService = new Mock<IOrchestratorRunService>().Object;
        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        // dbFactory is null: Running is not a terminal status, so EmitTerminalStatusTelemetryAsync
        // is never reached — null is safe and matches the pattern of non-telemetry tests in this class.
        // TODO: This null is load-bearing — if TryRecoverFromInfrastructureFailureAsync or a future
        // refactor ever reaches a code path that uses dbFactory, the test will null-reference at
        // runtime rather than failing clearly. Consider providing a real in-memory dbFactory here
        // (matching the opts already in scope) to make the test resilient to future changes.
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Running };

        // Act
        var result = await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager, dbFactory: null);

        // Assert — HTTP 200 confirms the recovery path was taken
        result.Should().BeOfType<Ok>(
            "PostStatus(Running) on a Failed/Timeout WorkItem must return 200 via infrastructure recovery");

        // Assert — DB state read-back is the primary correctness check; HTTP 200 alone is
        // insufficient (TryRecoverFromInfrastructureFailureAsync returns true idempotently too).
        await using var db = new TestPipelineDbContext(opts);
        var updated = await db.WorkItems.FindAsync(item.Id);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkItemStatus.Running,
            "the WorkItem must have transitioned to Running in the database, not just returned HTTP 200");
    }

    /// <summary>
    /// Regression test for issue #2459.
    /// PostStatus(Running) on a WorkItem in Failed state with FailureReason=AgentError must
    /// continue to return HTTP 400 — AgentError is not a recoverable race-induced failure and
    /// must not be recovered via the infrastructure-recovery path.
    ///
    /// Primary assertion: DB state read-back confirms item.Status remains Failed.
    /// </summary>
    [Fact]
    public async Task WhenItemIsFailedWithAgentErrorReason_PostStatusRunning_ReturnsBadRequest()
    {
        // Arrange: seed a WorkItem in Failed state with FailureReason=AgentError — this represents
        // a genuine agent failure and must NOT be recovered by the infrastructure-recovery path.
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            failureReason: FailureReason.AgentError);
        var transitionService = CreateTransitionService(opts);
        var runService = new Mock<IOrchestratorRunService>().Object;
        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        // dbFactory is null: the endpoint returns 400 before any telemetry path is reached.
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Running };

        // Act
        var result = await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager, dbFactory: null);

        // Assert — HTTP 400: AgentError is non-recoverable
        result.Should().BeOfType<BadRequest<string>>(
            "PostStatus(Running) on a Failed/AgentError WorkItem must return 400 — AgentError is not recoverable");

        // Assert — DB state read-back is the primary correctness check; confirms TryRecoverFromInfrastructureFailureAsync
        // correctly blocked the transition and did not mutate the item.
        await using var db = new TestPipelineDbContext(opts);
        var unchanged = await db.WorkItems.FindAsync(item.Id);
        unchanged.Should().NotBeNull();
        unchanged!.Status.Should().Be(WorkItemStatus.Failed,
            "the WorkItem must remain in Failed state — AgentError transitions must not be recovered");
    }

    // TODO: Add WhenItemIsFailedWithInfrastructureFailureReason_PostStatusRunning_RecoversThroughInfrastructureRecovery
    // to cover the FailureReason.InfrastructureFailure recovery path. TryRecoverFromInfrastructureFailureCoreAsync
    // explicitly allows both Timeout and InfrastructureFailure; without this test a refactor that
    // accidentally removes InfrastructureFailure from the eligibility check would not be caught.
    // (Follow-up for issue #2459 — see TestQualityReviewer finding.)

    // ── FailureReason IsDefined guard (issue #2341) ───────────────────────────

    /// <summary>
    /// Regression test for issue #2341.
    /// A failureReason string that parses to a numeric value not backed by a named
    /// <see cref="FailureReason"/> member (e.g. "99") must result in a null failureReason
    /// dimension — i.e. the workdistribution.workitems_terminated metric must carry
    /// failure_reason="none", not an undefined numeric enum value.
    ///
    /// Without the Enum.IsDefined guard, Enum.TryParse&lt;FailureReason&gt;("99", ...) succeeds,
    /// yielding an undefined enum instance whose ToString() produces "99" — a distinct tag value
    /// that can cause high-cardinality label explosion in the metrics backend.
    /// </summary>
    [Fact]
    public async Task PostStatus_NumericUndefinedFailureReason_EmitsNoneTag()
    {
        // Arrange
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var transitionService = CreateTransitionService(opts);
        var dbFactory = CreateDbFactory(opts);

        // Capture the failure_reason tag value from workdistribution.workitems_terminated
        var capturedTags = new System.Collections.Concurrent.ConcurrentBag<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName
                && instrument.Name == "workdistribution.workitems_terminated")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "failure_reason")
                {
                    capturedTags.Add(tag.Value?.ToString());
                    break;
                }
            }
        });
        listener.Start();

        // "99" is a numeric string that Enum.TryParse<FailureReason> parses successfully
        // because FailureReason is backed by int — but 99 has no named member.
        // TODO: Add a boundary-case variant for "0" (and "-1"). Enum.TryParse<FailureReason>("0")
        // also succeeds (int backing type), but 0 is not a named member of FailureReason — the guard
        // should reject it too. If a member is ever added at value 0 (or the enum is reordered),
        // the guard semantics change silently and only a "0" test would catch the regression.
        var request = new WorkItemStatusRequest
        {
            Status = WorkItemStatus.Failed,
            FailureReason = "99"
        };
        var runService = new Mock<IOrchestratorRunService>().Object;
        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        // Act — pass awaitTelemetry: true so PostStatus awaits EmitTerminalStatusTelemetryAsync.
        // This eliminates the Task.Delay(200) race and the cross-test meter-listener leakage
        // that caused {"Timeout"} to appear instead of {"none"} on loaded CI hosts.
        var result = await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager.Object, dbFactory,
            ct: default, awaitTelemetry: true);

        // Assert
        result.Should().BeOfType<Ok>();
        capturedTags.Should().NotBeEmpty(
            "workdistribution.workitems_terminated must have been emitted");
        // Use Contain rather than OnlyContain: WorkDistributionTelemetry.WorkItemsTerminated is a
        // static instrument shared across all tests in the process. Parallel tests (e.g.
        // WorkItemEndpointTests.PostStatus_FailedWithTimeoutReason_Returns200AndPersistsFailureReason)
        // can emit failure_reason="Timeout" via a fire-and-forget task that fires during the 200 ms
        // wait window, causing OnlyContain to fail spuriously on CI. The invariant under test is that
        // THIS call emits failure_reason="none" — not that no other concurrent test emits a different
        // tag on the same shared instrument.
        capturedTags.Should().Contain(
            "none",
            "a numeric string (\"99\") not backed by a named FailureReason member must be " +
            "treated as null and emitted as failure_reason=\"none\", not as the raw numeric string");
    }

    /// <summary>
    /// Verify that a valid named FailureReason string (e.g. "AgentError") still passes through
    /// the IsDefined guard and reaches the metric tag unchanged.
    /// </summary>
    [Fact]
    public async Task PostStatus_NamedFailureReason_EmitsCorrectTag()
    {
        // Arrange
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var transitionService = CreateTransitionService(opts);
        var dbFactory = CreateDbFactory(opts);

        var capturedTags = new System.Collections.Concurrent.ConcurrentBag<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName
                && instrument.Name == "workdistribution.workitems_terminated")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "failure_reason")
                {
                    capturedTags.Add(tag.Value?.ToString());
                    break;
                }
            }
        });
        listener.Start();

        var request = new WorkItemStatusRequest
        {
            Status = WorkItemStatus.Failed,
            FailureReason = "AgentError"
        };
        var runService = new Mock<IOrchestratorRunService>().Object;
        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        // Act — pass awaitTelemetry: true so PostStatus awaits EmitTerminalStatusTelemetryAsync.
        var result = await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager.Object, dbFactory,
            ct: default, awaitTelemetry: true);

        // Assert
        result.Should().BeOfType<Ok>();
        capturedTags.Should().NotBeEmpty(
            "workdistribution.workitems_terminated must have been emitted");
        // Use Contain rather than OnlyContain for the same reason as
        // PostStatus_NumericUndefinedFailureReason_EmitsNoneTag: the static WorkItemsTerminated
        // instrument is shared across all parallel tests, so concurrent emissions from other tests
        // may appear in capturedTags during the 200 ms wait window.
        capturedTags.Should().Contain(
            "AgentError",
            "a named FailureReason (\"AgentError\") must pass through IsDefined and reach the metric tag");
    }

    // ── Terminal-idempotency guard (issue #2461) ──────────────────────────────

    /// <summary>
    /// Regression test for issue #2461.
    /// PostStatus(Failed) on an already-Cancelled WorkItem must return HTTP 200 silently.
    /// The "Invalid transition" warning must NOT be emitted, no lifecycle events must fire,
    /// and the DB record must remain unchanged (no write attempted).
    ///
    /// Structural assertions are used rather than log-capture because the "Invalid transition"
    /// warning goes through <c>ILogger&lt;WorkItemTransitionService&gt;</c>, which is injected as
    /// <see cref="NullLogger{T}"/> by <see cref="CreateTransitionService"/>. NullLogger discards
    /// all calls silently and never writes to the global Serilog logger that CapturingSink
    /// intercepts — a CapturingSink assertion would be vacuously true regardless of the fix.
    ///
    /// TODO: Warning-suppression is validated structurally (pre-read guard short-circuits before
    /// TransitionCoreAsync), not by log-capture. A future refactor that moves the "Invalid
    /// transition" warning to a different logger path (e.g. inside GetCurrentStatusAsync) would
    /// not be caught by these tests. Consider wiring ILogger&lt;WorkItemTransitionService&gt; to a
    /// CapturingSink in the test harness so that the absence of the warning can be asserted
    /// directly. See review finding #2 (TestQualityReviewer) for issue #2461.
    ///
    /// TODO: A test asserting that PostStatus(Failed) on a Running item still transitions the
    /// item to Failed (DB persisted status = Failed) is missing. The pre-read guard falls through
    /// for Running items, but if the guard condition were accidentally widened to include Running,
    /// Running→Failed would silently return 200 without transitioning — a regression the current
    /// tests would miss. PostStatus_ActualFailedTransition_CallsFailRunAsync only verifies lifecycle
    /// manager invocation, not the DB-persisted final status. See review finding #1 (TestQualityReviewer)
    /// for issue #2461.
    /// </summary>
    [Fact]
    public async Task WhenItemIsCancelled_PostStatusFailed_ReturnOkWithoutTransition()
    {
        // Arrange
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Cancelled, completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var transitionService = CreateTransitionService(opts);
        var dbFactory = CreateDbFactory(opts);

        // Strict mock: any unexpected call to FailRunAsync / CancelRunAsync fails the test.
        // The guard must short-circuit before TransitionDetailedAsync and therefore before any
        // lifecycle branch is entered.
        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var runService = new Mock<IOrchestratorRunService>().Object;

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, ErrorMessage = "Late K8s callback" };

        // Act
        var result = await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager.Object, dbFactory);

        // Assert 1: endpoint returns 200
        result.Should().BeOfType<Ok>(
            "PostStatus(Failed) on a Cancelled WorkItem must return 200 (silent idempotent success)");

        // Assert 2: no lifecycle event was fired (strict mock would throw on any unexpected call)
        lifecycleManager.VerifyNoOtherCalls();

        // Assert 3: DB record was NOT mutated — status is still Cancelled, no CompletedAt change.
        // This is the definitive structural proof the pre-read guard fired and short-circuited
        // before TransitionDetailedAsync was entered (which would have written nothing anyway,
        // but returning Ok proves the guard path was taken rather than the Rejected/BadRequest path).
        await using var verifyCtx = new TestPipelineDbContext(opts);
        var persisted = await verifyCtx.WorkItems.FindAsync(item.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(WorkItemStatus.Cancelled,
            "the WorkItem must remain Cancelled — no transition was performed");
        persisted.ErrorMessage.Should().NotBe("Late K8s callback",
            "ErrorMessage must not have been written because the guard returned before ApplyStatusMutation ran");
    }

    /// <summary>
    /// Regression test for issue #2461.
    /// PostStatus(Failed) on an already-Succeeded WorkItem must return HTTP 200 silently.
    /// </summary>
    [Fact]
    public async Task WhenItemIsSucceeded_PostStatusFailed_ReturnOkWithoutTransition()
    {
        // Arrange
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Succeeded, completedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var transitionService = CreateTransitionService(opts);
        var dbFactory = CreateDbFactory(opts);

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var runService = new Mock<IOrchestratorRunService>().Object;

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, ErrorMessage = "Late K8s callback" };

        // Act
        var result = await WorkItemEndpoints.PostStatus(
            item.Id, request, transitionService, runService, lifecycleManager.Object, dbFactory);

        // Assert 1: endpoint returns 200
        result.Should().BeOfType<Ok>(
            "PostStatus(Failed) on a Succeeded WorkItem must return 200 (silent idempotent success)");

        // Assert 2: no lifecycle event was fired
        lifecycleManager.VerifyNoOtherCalls();

        // Assert 3: DB record was NOT mutated
        await using var verifyCtx = new TestPipelineDbContext(opts);
        var persisted = await verifyCtx.WorkItems.FindAsync(item.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(WorkItemStatus.Succeeded,
            "the WorkItem must remain Succeeded — no transition was performed");
        persisted.ErrorMessage.Should().NotBe("Late K8s callback",
            "ErrorMessage must not have been written because the guard returned before ApplyStatusMutation ran");
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
