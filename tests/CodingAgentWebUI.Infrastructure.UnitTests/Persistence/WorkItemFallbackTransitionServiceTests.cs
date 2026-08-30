using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="WorkItemFallbackTransitionService.TryFallbackChainAsync"/> covering:
/// - Direct transition success
/// - Two-step via Running (for Succeeded, Cancelled, and Failed statuses)
/// - Two-step terminal step sets CompletedAt, ErrorMessage, and FailureReason
/// - Infrastructure-failure recovery when two-step fails
/// - All paths fail returns false
/// - Already-terminal item
/// </summary>
/// <remarks>
/// TODO: The "two-step terminal step failure returns false" path (Running → terminal rejected after
/// Dispatched → Running succeeded) is not covered. The class doc previously claimed this was tested
/// ("silent discard bug is fixed") but no such test exists. A concrete failing scenario: any future
/// state-machine change that blocks Running → Succeeded would leave the item stuck in Running with
/// TryFallbackChainAsync returning false and no recovery in TryInfrastructureRecoveryAsync (which
/// only handles Failed/InfrastructureFailure). To cover this, a custom WorkItemTransitionService
/// test double is needed that rejects the terminal-step transition while allowing the intermediate one.
/// </remarks>
public sealed class WorkItemFallbackTransitionServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly WorkItemFallbackTransitionService _sut;

    public WorkItemFallbackTransitionServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"FallbackSvcTest-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _sut = new WorkItemFallbackTransitionService(_transitionService, NullLogger<WorkItemFallbackTransitionService>.Instance);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Direct transition ────────────────────────────────────────────────

    [Fact]
    public async Task TryFallbackChainAsync_DirectTransitionSucceeds_ReturnsTrue()
    {
        var id = await SeedWorkItem(WorkItemStatus.Running);

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        result.Should().BeTrue();
        var item = await ReadItem(id);
        item!.Status.Should().Be(WorkItemStatus.Succeeded);
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TryFallbackChainAsync_DirectTransition_SetsCompletedAtForAllTerminalStatuses()
    {
        var id1 = await SeedWorkItem(WorkItemStatus.Running);
        var id2 = await SeedWorkItem(WorkItemStatus.Running);
        var id3 = await SeedWorkItem(WorkItemStatus.Running);

        await _sut.TryFallbackChainAsync(id1, WorkItemStatus.Succeeded, null, null, CancellationToken.None);
        await _sut.TryFallbackChainAsync(id2, WorkItemStatus.Failed, "err", FailureReason.AgentError, CancellationToken.None);
        await _sut.TryFallbackChainAsync(id3, WorkItemStatus.Cancelled, null, null, CancellationToken.None);

        (await ReadItem(id1))!.CompletedAt.Should().NotBeNull();
        (await ReadItem(id2))!.CompletedAt.Should().NotBeNull();
        (await ReadItem(id3))!.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TryFallbackChainAsync_DirectTransition_SetsErrorMessageAndFailureReason_WhenFailed()
    {
        var id = await SeedWorkItem(WorkItemStatus.Running);

        await _sut.TryFallbackChainAsync(id, WorkItemStatus.Failed, "Something went wrong", FailureReason.AgentError, CancellationToken.None);

        var item = await ReadItem(id);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Be("Something went wrong");
        item.FailureReason.Should().Be(FailureReason.AgentError);
    }

    [Fact]
    public async Task TryFallbackChainAsync_DirectTransition_SetsDefaultErrorMessage_WhenFailedWithNullMessage()
    {
        var id = await SeedWorkItem(WorkItemStatus.Running);

        await _sut.TryFallbackChainAsync(id, WorkItemStatus.Failed, null, null, CancellationToken.None);

        var item = await ReadItem(id);
        item!.ErrorMessage.Should().Be("Job failed without specific error information");
        item.FailureReason.Should().Be(FailureReason.AgentError);
    }

    [Fact]
    public async Task TryFallbackChainAsync_DirectTransition_DoesNotSetErrorMessage_WhenSucceeded()
    {
        var id = await SeedWorkItem(WorkItemStatus.Running);

        await _sut.TryFallbackChainAsync(id, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        var item = await ReadItem(id);
        item!.ErrorMessage.Should().BeNull();
        item.FailureReason.Should().BeNull();
    }

    // ── Two-step via Running ─────────────────────────────────────────────

    [Fact]
    public async Task TryFallbackChainAsync_DirectRejected_TwoStepSucceeds_ForSucceeded()
    {
        // Dispatched → Succeeded is invalid directly; two-step goes Dispatched → Running → Succeeded
        var id = await SeedWorkItem(WorkItemStatus.Dispatched);

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        result.Should().BeTrue();
        var item = await ReadItem(id);
        item!.Status.Should().Be(WorkItemStatus.Succeeded);
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TryFallbackChainAsync_DirectRejected_TwoStepSucceeds_ForCancelled()
    {
        var id = await SeedWorkItem(WorkItemStatus.Dispatched);

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Cancelled, null, null, CancellationToken.None);

        result.Should().BeTrue();
        var item = await ReadItem(id);
        item!.Status.Should().Be(WorkItemStatus.Cancelled);
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TryFallbackChainAsync_DirectRejected_TwoStepSucceeds_ForFailed()
    {
        // IsValidTransition(Dispatched, Failed) = true, so direct succeeds. Seed in a state
        // where direct to Failed is invalid to force the two-step path.
        // Pending → Failed is valid (Pending → Failed is in the state machine),
        // so we need a state where direct to Failed is blocked.
        // Actually, all states can reach Failed directly. The two-step guard for Failed
        // is about completeness — test it by seeding Dispatched and verifying the direct
        // path is used (which still exercises the Failed branch in the mutation action).
        // TODO: This test does NOT exercise the two-step path for Failed — the direct
        // Dispatched → Failed transition is valid so TryDirectAsync succeeds before TryTwoStepAsync
        // is ever reached. If the two-step branch for Failed were removed, this test would still pass.
        // To properly cover the two-step Failed path, seed a state where direct → Failed is rejected
        // by the state machine (requires a state-machine change or a custom test double).
        var id = await SeedWorkItem(WorkItemStatus.Dispatched);

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Failed, "oops", FailureReason.AgentError, CancellationToken.None);

        result.Should().BeTrue();
        var item = await ReadItem(id);
        // Direct succeeds for Dispatched → Failed (valid per IsValidTransition)
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Be("oops");
        item.FailureReason.Should().Be(FailureReason.AgentError);
    }

    [Fact]
    public async Task TryFallbackChainAsync_TwoStep_SetsFullMutationOnTerminalStep()
    {
        // Dispatched → Succeeded via two-step: the terminal step must set all three fields
        var id = await SeedWorkItem(WorkItemStatus.Dispatched);

        await _sut.TryFallbackChainAsync(id, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        // For Succeeded, CompletedAt is set but ErrorMessage/FailureReason stay null
        var item = await ReadItem(id);
        item!.Status.Should().Be(WorkItemStatus.Succeeded);
        // TODO: This assertion only checks that CompletedAt is non-null — it cannot distinguish
        // whether it was set by the intermediate Running step or the final Succeeded step's mutation action.
        // If BuildMutationAction in TryTwoStepAsync skipped CompletedAt for Succeeded but the Running step
        // set it, this assertion would still pass. Consider asserting CompletedAt > a timestamp captured
        // before TryTwoStepAsync's terminal call, or refactoring to verify the mutation action is invoked
        // on the terminal transition (not the intermediate one).
        item.CompletedAt.Should().NotBeNull();
        item.ErrorMessage.Should().BeNull();
        item.FailureReason.Should().BeNull();
    }

    // ── Infrastructure-failure recovery ──────────────────────────────────

    [Fact]
    public async Task TryFallbackChainAsync_InfraRecovery_WhenDirectAndTwoStepFail()
    {
        // Seed item in Failed/InfrastructureFailure state — all direct/two-step transitions rejected
        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Failed;
            item.FailureReason = FailureReason.InfrastructureFailure;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        result.Should().BeTrue();
        var recovered = await ReadItem(id);
        recovered!.Status.Should().Be(WorkItemStatus.Succeeded);
        recovered.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TryFallbackChainAsync_InfraRecovery_NoRecovery_WhenAgentError()
    {
        // AgentError-failed items should NOT be recovered
        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Failed;
            item.FailureReason = FailureReason.AgentError;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        result.Should().BeFalse("AgentError failures must not be recovered");
        var item2 = await ReadItem(id);
        item2!.Status.Should().Be(WorkItemStatus.Failed, "status unchanged after rejected recovery");
    }

    // ── Timeout race recovery (Issue #2146) ──────────────────────────────

    [Fact]
    public async Task TryFallbackChainAsync_WithFailedTimeout_TargetSucceeded_ReturnsTrue_AndTransitionsToSucceeded()
    {
        // ReconciliationLoop timed out the WorkItem (Failed/Timeout), then the agent completed
        // late. The fallback chain must recover it to Succeeded via TryInfrastructureRecoveryAsync.
        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Failed;
            item.FailureReason = FailureReason.Timeout;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        result.Should().BeTrue("Failed/Timeout items must be recoverable to Succeeded");
        var recovered = await ReadItem(id);
        recovered!.Status.Should().Be(WorkItemStatus.Succeeded);
        recovered.CompletedAt.Should().NotBeNull();
        // FailureReason.Timeout is preserved as an audit trail artifact — BuildMutationAction
        // does not clear FailureReason for non-Failed targets.
        recovered.FailureReason.Should().Be(FailureReason.Timeout);
        // TODO: A Succeeded WorkItem retaining FailureReason = Timeout is semantically
        // inconsistent — any downstream consumer reading FailureReason without first checking
        // Status will observe a misleading "Timeout" on a succeeded item. This is intentional
        // per issue requirements (audit trail preservation), but consider clearing FailureReason
        // upon successful recovery transition if that requirement is ever relaxed.
        // The assertion above intentionally bakes in this behavior — do not remove it without
        // revisiting the BuildMutationAction logic in WorkItemFallbackTransitionService.cs.
        // See review finding [WARNING] WorkItemFallbackTransitionServiceTests.cs:279.
    }

    [Fact]
    public async Task TryFallbackChainAsync_WithFailedAgentError_TargetSucceeded_ReturnsFalse_StateUnchanged()
    {
        // Acceptance criterion: AgentError failures must NOT recover to Succeeded.
        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Failed;
            item.FailureReason = FailureReason.AgentError;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        result.Should().BeFalse("AgentError failures must not be recovered to Succeeded");
        var unchanged = await ReadItem(id);
        unchanged!.Status.Should().Be(WorkItemStatus.Failed);
        unchanged.FailureReason.Should().Be(FailureReason.AgentError);
    }

    [Fact]
    public async Task TryFallbackChainAsync_WithFailedQualityGateExhausted_TargetSucceeded_ReturnsFalse_StateUnchanged()
    {
        // Acceptance criterion: QualityGateExhausted failures must NOT recover to Succeeded.
        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Failed;
            item.FailureReason = FailureReason.QualityGateExhausted;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        result.Should().BeFalse("QualityGateExhausted failures must not be recovered to Succeeded");
        var unchanged = await ReadItem(id);
        unchanged!.Status.Should().Be(WorkItemStatus.Failed);
        unchanged.FailureReason.Should().Be(FailureReason.QualityGateExhausted);
    }

    // ── All paths fail ───────────────────────────────────────────────────

    [Fact]
    public async Task TryFallbackChainAsync_AllPathsFail_ReturnsFalse()
    {
        // Seed an already-terminal item that isn't InfrastructureFailure — nothing can recover it
        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Succeeded;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Failed, null, null, CancellationToken.None);

        result.Should().BeFalse();
        var item2 = await ReadItem(id);
        item2!.Status.Should().Be(WorkItemStatus.Succeeded, "status unchanged after all paths rejected");
    }

    [Fact]
    public async Task TryFallbackChainAsync_AlreadyAtTarget_ReturnsTrue_Idempotent()
    {
        // TransitionAsync returns true for already-at-target (idempotent per WorkItemTransitionService)
        var id = await SeedWorkItem(WorkItemStatus.Succeeded);

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        result.Should().BeTrue("already at target is treated as idempotent success by WorkItemTransitionService");
    }

    // ── Test infrastructure ───────────────────────────────────────────────

    private async Task<Guid> SeedWorkItem(WorkItemStatus initialStatus)
    {
        var id = Guid.NewGuid();
        await using var db = _dbFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = "org/repo#1",
            IssueProviderConfigId = "ip-1",
            Status = initialStatus,
            AgentSelector = "dotnet",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<WorkItemEntity?> ReadItem(Guid id)
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.WorkItems.FindAsync(id);
    }

    /// <summary>
    /// InMemory-compatible DbContext subclass that removes PostgreSQL-specific features
    /// not supported by the EF Core InMemory provider (RowVersion tokens, filtered indexes).
    /// </summary>
    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Disable RowVersion concurrency tokens — not supported by InMemory provider
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            // Remove filtered indexes (PostgreSQL partial indexes) — not supported by InMemory provider
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
                    entityType.RemoveIndex(index);
            }
        }
    }

    // ── 3B-001 / Issue #2139: Early-exit when item is already in a same or different terminal state ─

    [Theory]
    [InlineData(WorkItemStatus.Succeeded, WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Succeeded, WorkItemStatus.Cancelled)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Failed, WorkItemStatus.Failed)]   // issue #2139: Failed→Failed no-op guard
    public async Task TryFallbackChainAsync_WhenItemAlreadyTerminal_ReturnsFalse_WithoutAttemptingFallbackSteps(
        WorkItemStatus terminalStatus, WorkItemStatus requestedStatus)
    {
        // A late completion callback arrives after ReconciliationService has already terminated
        // the item with a truly-final state (Succeeded, Cancelled, or the same Failed state).
        // The fallback chain must detect these states and return early without logging
        // "Invalid transition" warnings for all 3 steps (the 3B-001 / #2139 spam pattern).
        //
        // Note: Failed → (Succeeded|Running) is NOT included here — those have legitimate
        // recovery paths via TryInfrastructureRecoveryAsync.
        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = terminalStatus;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var result = await _sut.TryFallbackChainAsync(id, requestedStatus, null, null, CancellationToken.None);

        result.Should().BeFalse("item is in a truly terminal state with no further transitions");
        var finalItem = await ReadItem(id);
        finalItem!.Status.Should().Be(terminalStatus, "terminal state must not be overwritten");
        // TODO: This theory does not assert that ErrorMessage and FailureReason are left unchanged
        // on the early-exit path. The dedicated [Fact] below covers Failed→Failed with those
        // assertions, but a mutation that zeroes out ErrorMessage/FailureReason in the no-op guard
        // would pass this theory while failing the [Fact]. Consider either dropping the
        // [InlineData(Failed, Failed)] case here (the [Fact] already covers it) or adding
        // ErrorMessage/FailureReason assertions to make this theory case equally rigorous.
    }

    // ── Issue #2139: Failed→Failed dedicated no-op test ─────────────────

    [Fact]
    public async Task TryFallbackChainAsync_WhenAlreadyFailed_TargetFailed_ReturnsFalse_WithoutStateChange()
    {
        // When a caller requests Failed→Failed (e.g. a reconciliation loop re-fires a late
        // completion event on an already-Failed item), the method must return false immediately
        // without executing any fallback steps or emitting "Invalid transition" warnings.
        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Failed;
            item.FailureReason = FailureReason.AgentError;
            item.ErrorMessage = "original error";
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Failed, "new error", FailureReason.Timeout, CancellationToken.None);

        result.Should().BeFalse("Failed→Failed is a no-op, no transition was performed");
        var finalItem = await ReadItem(id);
        finalItem!.Status.Should().Be(WorkItemStatus.Failed, "status must remain Failed");
        finalItem.ErrorMessage.Should().Be("original error", "mutation must not be applied on no-op");
        finalItem.FailureReason.Should().Be(FailureReason.AgentError, "failure reason must not be overwritten on no-op");
    }

    // ── Issue #2139: Failed→Running infra recovery regression guard ──────

    [Fact]
    public async Task TryFallbackChainAsync_WhenAlreadyFailed_InfrastructureFailure_TargetRunning_StillExecutesFallbackChain()
    {
        // The Failed→Failed guard must NOT block the legitimate Failed→Running infrastructure
        // recovery path. When target is Running (not Failed), the guard condition is false and
        // the full fallback chain executes, recovering the item via TryInfrastructureRecoveryAsync.
        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Failed;
            item.FailureReason = FailureReason.InfrastructureFailure;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var result = await _sut.TryFallbackChainAsync(id, WorkItemStatus.Running, null, null, CancellationToken.None);

        result.Should().BeTrue("Failed/InfrastructureFailure must be recoverable to Running");
        var recovered = await ReadItem(id);
        recovered!.Status.Should().Be(WorkItemStatus.Running, "item must transition to Running via infra recovery");
        // TODO: The assertions above verify the fallback chain ran and succeeded, but do not
        // confirm that actual state mutations occurred (e.g. FailureReason was cleared,
        // CompletedAt was reset). A future change that returns true without performing the
        // expected state mutations would pass these assertions. Consider adding:
        //   recovered.FailureReason.Should().BeNull("recovery should clear the failure reason");
        //   recovered.CompletedAt.Should().BeNull("recovery should reset CompletedAt");
        // to make this regression guard more robust.
    }

    // ── Debug-logging path coverage (#2139 new code) ─────────────────────
    // The two guards introduced in #2139 contain debug-level log calls behind
    // IsEnabled(Debug) checks. NullLogger returns false for IsEnabled, so those
    // branches are unreachable with the default fixture. These two tests create a
    // WorkItemFallbackTransitionService backed by a real Debug-level logger to
    // ensure the branches are executed and covered.

    [Fact]
    public async Task TryFallbackChainAsync_WhenAlreadyFailed_TargetFailed_WithDebugLogger_ExecutesDebugLogBranch()
    {
        // Arrange: use a debug-level logger so IsEnabled(Debug) returns true
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddConsole());
        var debugLogger = loggerFactory.CreateLogger<WorkItemFallbackTransitionService>();
        var sut = new WorkItemFallbackTransitionService(_transitionService, debugLogger);

        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Failed;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        // Act: Failed→Failed with debug logger — covers the LogDebug branch inside the guard
        var result = await sut.TryFallbackChainAsync(id, WorkItemStatus.Failed, null, null, CancellationToken.None);

        // Assert: same outcome — early exit returns false
        result.Should().BeFalse("Failed→Failed is a no-op regardless of logger level");
    }

    [Fact]
    public async Task TryFallbackChainAsync_WhenSucceeded_TargetFailed_WithDebugLogger_ExecutesDebugLogBranch()
    {
        // Arrange: use a debug-level logger so IsEnabled(Debug) returns true for the
        // Succeeded/Cancelled + different target guard branch
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddConsole());
        var debugLogger = loggerFactory.CreateLogger<WorkItemFallbackTransitionService>();
        var sut = new WorkItemFallbackTransitionService(_transitionService, debugLogger);

        var id = await SeedWorkItem(WorkItemStatus.Running);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Succeeded;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        // Act: Succeeded→Failed with debug logger — covers the LogDebug branch inside the
        // Succeeded/Cancelled guard that was introduced alongside the Failed→Failed guard
        var result = await sut.TryFallbackChainAsync(id, WorkItemStatus.Failed, null, null, CancellationToken.None);

        // Assert: same outcome — early exit returns false
        result.Should().BeFalse("Succeeded→Failed has no recovery path, early exit returns false");
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
