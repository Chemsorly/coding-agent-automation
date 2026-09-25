using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Tests for the pipeline.run.outcomes and pipeline.run.duration metric emission
/// in <see cref="WorkItemStatusTransitionService.EmitTerminalStatusTelemetryAsync"/>.
///
/// Covers AC: "The outcome mapping is unit-tested for every value" and
/// "A terminal transition increments pipeline_run_outcomes_total exactly once."
/// </summary>
[Collection("PostStatusIdempotencyCollection")]
public sealed class PipelineRunOutcomeMetricsTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"OutcomeMetrics-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task<WorkItemEntity> SeedRunningItemAsync(DbContextOptions<PipelineDbContext> opts)
    {
        var item = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = $"org/repo#{Guid.NewGuid():N}",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Running,
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
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
        IDbContextFactory<PipelineDbContext>? dbFactory = null)
    {
        // TODO: [WARNING] This mock sets up FailRunAsync and CancelRunAsync but NOT FailRunWithLabelAsync,
        // which is the actual production path called for needs_refinement outcomes (Failed path with
        // GateRejected). With lenient MockBehavior (the default), unsetup calls silently return null/default,
        // which happens to work for metric assertions but provides no assurance that FailRunWithLabelAsync
        // is actually called on the correct path. Consider adding:
        //   lifecycleManager.Setup(m => m.FailRunWithLabelAsync(...)).ReturnsAsync((PipelineRun?)null);
        // and verifying it is called for the needs_refinement path to make the mock contract explicit.
        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(
                It.IsAny<RunId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);
        lifecycleManager
            .Setup(m => m.CancelRunAsync(
                It.IsAny<RunId>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync((PipelineRun?)null);
        return new WorkItemStatusTransitionService(
            CreateTransitionService(opts), lifecycleManager.Object,
            dbFactory ?? CreateDbFactory(opts));
    }

    private static string SerializePayload(JobCompletionPayload payload)
        => JsonSerializer.Serialize(payload, PipelineJsonOptions.Default);

    /// <summary>
    /// Sets up a MeterListener capturing (outcome, failure_reason, run_type) from
    /// pipeline.run.outcomes measurements.
    /// </summary>
    private static (MeterListener Listener, ConcurrentBag<(string Outcome, string FailureReason, string RunType)> Bag) SetupOutcomesListener()
    {
        var bag = new ConcurrentBag<(string, string, string)>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (measurement == 0) return; // skip pre-init Add(0)
            string outcome = "", failureReason = "", runType = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome") outcome = tag.Value?.ToString() ?? "";
                else if (tag.Key == "failure_reason") failureReason = tag.Value?.ToString() ?? "";
                else if (tag.Key == "run_type") runType = tag.Value?.ToString() ?? "";
            }
            bag.Add((outcome, failureReason, runType));
        });
        listener.Start();
        return (listener, bag);
    }

    // ── Outcome mapping: every value ──────────────────────────────────────────

    [Fact]
    public async Task Outcome_Cancelled_WhenStatusIsCancelled()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Cancelled },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.Outcome == "cancelled" && r.FailureReason == "none",
            "status=Cancelled must produce outcome='cancelled' with failure_reason='none'");
    }

    [Fact]
    public async Task Outcome_ConflictRestart_WhenFinalStepIsConflictRestart()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.ConflictRestart,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                Result = SerializePayload(payload)
            },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.Outcome == "conflict_restart" && r.FailureReason == "none",
            "FinalStep=ConflictRestart must produce outcome='conflict_restart' — survives #2956");
    }

    [Fact]
    public async Task Outcome_WontDo_WhenAnalysisRecommendationIsWontDo_FailureReasonNone()
    {
        // wont_do arrives as Succeeded + FailureReason=GateRejected (from AgentPhaseExecutor).
        // The outcome derivation must force failure_reason=none despite the GateRejected payload.
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            AnalysisRecommendation = AnalysisGateResult.WontDo,
            FailureCategory = FailureReason.GateRejected,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                FailureReason = "GateRejected", // arrives with GateRejected in the request
                Result = SerializePayload(payload)
            },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.Outcome == "wont_do" && r.FailureReason == "none",
            "wont_do must produce failure_reason='none' even though request.FailureReason='GateRejected'");
        bag.Should().NotContain(r => r.Outcome == "wont_do" && r.FailureReason == "gate_rejected",
            "failure_reason must NOT be 'gate_rejected' for wont_do outcome");
    }

    [Fact]
    public async Task Outcome_NeedsRefinement_WhenAnalysisRecommendationIsNotReady_FailureReasonNone()
    {
        // needs_refinement arrives as Failed + FailureReason=GateRejected.
        // The outcome derivation must force failure_reason=none.
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            AnalysisRecommendation = AnalysisGateResult.NotReady,
            FailureCategory = FailureReason.GateRejected,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Failed,
                FailureReason = "GateRejected",
                Result = SerializePayload(payload)
            },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.Outcome == "needs_refinement" && r.FailureReason == "none",
            "needs_refinement must produce failure_reason='none' even though request carries GateRejected");
    }

    [Fact]
    public async Task Outcome_PrCreated_WhenPullRequestUrlSet_NotDraft()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            PullRequestUrl = "https://github.com/org/repo/pull/42",
            IsDraftPr = false,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                Result = SerializePayload(payload)
            },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.Outcome == "pr_created" && r.FailureReason == "none");
    }

    [Fact]
    public async Task Outcome_DraftPr_WhenIsDraftPrTrue()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            PullRequestUrl = "https://github.com/org/repo/pull/43",
            IsDraftPr = true,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                Result = SerializePayload(payload)
            },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.Outcome == "draft_pr" && r.FailureReason == "none");
    }

    [Fact]
    public async Task Outcome_Succeeded_WhenSucceededNoSpecificPayload()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.Outcome == "succeeded" && r.FailureReason == "none");
    }

    [Fact]
    public async Task Outcome_Timeout_WhenFailureReasonIsTimeout()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Failed, FailureReason = "Timeout" },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.Outcome == "timeout" && r.FailureReason == "timeout");
    }

    [Fact]
    public async Task Outcome_Failed_WhenFailedWithAgentError()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Failed, FailureReason = "AgentError" },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.Outcome == "failed" && r.FailureReason == "agent_error");
    }

    [Theory]
    [InlineData("Timeout", "timeout")]
    [InlineData("InfrastructureFailure", "infrastructure_failure")]
    [InlineData("AgentError", "agent_error")]
    [InlineData("TokenRefreshFailure", "token_refresh_failure")]
    [InlineData("ExitCodeFailure", "exit_code_failure")]
    [InlineData("QualityGateExhausted", "quality_gate_exhausted")]
    [InlineData("GateRejected", "gate_rejected")]
    public async Task Outcome_Failed_AllSevenFailureReasons_ProduceSnakeCaseTag(
        string failureReasonStr, string expectedSnakeCase)
    {
        // Each of the 7 FailureReason values for the 'failed' outcome must produce snake_case.
        // Also covers the pre-existing gap where GateRejected was missing from instrumentation tests.
        // Note: Timeout produces outcome='timeout', but we test it here in the 'failed' branch by
        // using a payload that prevents the timeout detection (GateRejected payload, not timeout).
        // For FailureReason.Timeout: outcome='timeout' (not 'failed'), so we special-case it.
        if (failureReasonStr == "Timeout")
        {
            var timeoutOpts = CreateDbOptions();
            var timeoutItem = await SeedRunningItemAsync(timeoutOpts);
            var timeoutSvc = CreateService(timeoutOpts);
            var (timeoutListener, timeoutBag) = SetupOutcomesListener();
            using var tl = timeoutListener;

            await timeoutSvc.TransitionAsync(timeoutItem.Id,
                new WorkItemStatusRequest { Status = WorkItemStatus.Failed, FailureReason = "Timeout" },
                CancellationToken.None, awaitTelemetry: true);

            // TODO: [WARNING] The early-exit here only asserts outcome='timeout' — the expectedSnakeCase
            // parameter value ("timeout") is never asserted in this branch. A regression that changed
            // the snake-case conversion for Timeout (e.g. keeping it as "Timeout") would still pass
            // because the return skips the bag.Should().Contain(...FailureReason == expectedSnakeCase)
            // assertion at the bottom. The Timeout case is already covered by Outcome_Timeout_WhenFailureReasonIsTimeout;
            // consider removing the InlineData("Timeout","timeout") row from this theory to eliminate the gap.
            timeoutBag.Should().Contain(r => r.Outcome == "timeout" && r.FailureReason == "timeout",
                $"FailureReason.Timeout → outcome='timeout', failure_reason='timeout'");
            return;
        }

        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Failed, FailureReason = failureReasonStr },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.Outcome == "failed" && r.FailureReason == expectedSnakeCase,
            $"FailureReason.{failureReasonStr} must produce failure_reason='{expectedSnakeCase}' (snake_case)");
    }

    // ── run_type tag derivation ───────────────────────────────────────────────

    [Theory]
    [InlineData(WorkItemTaskType.Implementation, "implementation")]
    [InlineData(WorkItemTaskType.Review, "review")]
    [InlineData(WorkItemTaskType.Decomposition, "decompositionanalysis")]
    [InlineData(WorkItemTaskType.Consolidation, "consolidation")]
    public async Task RunType_DerivedFromTaskType_WhenNoPipelineRunRow(
        WorkItemTaskType taskType, string expectedRunType)
    {
        // When no PipelineRunEntity exists, RunType falls back to TaskType via safe switch.
        // TODO: [WARNING] There is no test for the "unknown" fallback when TaskType is an unrecognised
        // value (the _ => null branch in the switch). The requirement explicitly allows "unknown" as a
        // fallback for unresolvable run types. A regression that made the switch throw instead of
        // returning null would not be caught by existing tests. Add a test:
        //   RunType_IsUnknown_WhenTaskTypeIsUnrecognised (seed a WorkItem with a TaskType value that
        //   doesn't map to any PipelineRunType and assert run_type="unknown" in the emitted metric).
        var opts = CreateDbOptions();
        var item = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = $"org/repo#{Guid.NewGuid():N}",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Running,
            TaskType = taskType,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await using var ctx = new TestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();

        var svc = CreateService(opts);
        var (listener, bag) = SetupOutcomesListener();
        using var _ = listener;

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded },
            CancellationToken.None, awaitTelemetry: true);

        bag.Should().Contain(r => r.RunType == expectedRunType,
            $"TaskType.{taskType} must produce run_type='{expectedRunType}' when no PipelineRunEntity exists");
    }

    // ── Exactly-once counting: AlreadyAtTarget does not record ───────────────

    [Fact]
    public async Task AlreadyAtTarget_DoesNotRecordPipelineRunOutcome()
    {
        // If the work item is already in a terminal state, no metric should be recorded.
        var opts = CreateDbOptions();
        var item = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = $"org/repo#{Guid.NewGuid():N}",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Cancelled,
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        await using var ctx = new TestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();

        var svc = CreateService(opts);

        var countAfter = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
        {
            if (measurement > 0) Interlocked.Increment(ref countAfter);
        });
        listener.Start();

        var outcome = await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Failed },
            CancellationToken.None, awaitTelemetry: true);

        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget);
        countAfter.Should().Be(0,
            "pipeline.run.outcomes must NOT increment when the transition is a no-op (AlreadyAtTarget)");
        // TODO: [WARNING] This test does not verify that workdistribution.workitems_terminated is also
        // NOT emitted for AlreadyAtTarget no-op transitions. If the production code were changed to
        // emit workdistribution.workitems_terminated for no-ops, this test would not catch it.
        // workdistribution.workitems_terminated's pre-initialization design assumes it only increments
        // on real transitions. Add a MeterListener for WorkDistributionTelemetry.MeterName and assert
        // that WorkItemsTerminated does not increment during this test.
    }

    // ── Test infrastructure ────────────────────────────────────────────────────

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
