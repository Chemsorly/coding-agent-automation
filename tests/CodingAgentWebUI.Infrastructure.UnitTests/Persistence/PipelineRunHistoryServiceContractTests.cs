using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Contract tests for <see cref="IPipelineRunHistoryService"/> implementations.
/// Both FileSystem-backed and Postgres-backed services must satisfy these behavioral contracts.
/// Prevents behavioral drift between legacy (filesystem) and DB (Postgres) modes.
///
/// Pattern follows <see cref="ConsolidationRunStoreContractTests"/>.
/// Derived classes provide a concrete service instance via <see cref="CreateService"/>.
/// </summary>
public abstract class PipelineRunHistoryServiceContractTests : IDisposable
{
    /// <summary>Create a fresh service instance for isolation between tests.</summary>
    protected abstract IPipelineRunHistoryService CreateService();

    /// <summary>
    /// When true, the derived class supports direct ghost-entry injection into the backing store,
    /// bypassing <see cref="IPipelineRunHistoryService.AddRunToHistoryAsync"/>'s write guard.
    /// This is needed to exercise the read-time dual-discriminator filter independently of the
    /// write guard. Postgres-backed implementations override this to true and also override
    /// <see cref="InsertGhostSummaryDirectlyAsync"/>.
    /// </summary>
    protected virtual bool SupportsDirectGhostInjection => false;

    /// <summary>
    /// Inserts a <see cref="PipelineRunSummary"/> directly into the backing store, bypassing
    /// <see cref="IPipelineRunHistoryService.AddRunToHistoryAsync"/> and its write guard.
    /// Only called when <see cref="SupportsDirectGhostInjection"/> returns true.
    /// </summary>
    protected virtual Task InsertGhostSummaryDirectlyAsync(PipelineRunSummary summary)
        => Task.CompletedTask; // Base: no-op; override in DB-backed implementations.

    /// <summary>Cleanup resources after each test.</summary>
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    // ── AddRunToHistoryAsync + GetRunHistoryAsync ────────────────────────

    [Fact]
    public async Task AddRun_ThenGetHistory_ContainsRun()
    {
        var service = CreateService();
        var run = CreateCompletedRun(
            Guid.NewGuid().ToString(),
            "org/repo#42",
            "Fix the flaky test");

        await service.AddRunToHistoryAsync(run);

        var history = await service.GetRunHistoryAsync();

        history.Should().HaveCount(1);
        history[0].RunId.Should().Be(run.RunId);
        history[0].IssueIdentifier.Should().Be("org/repo#42");
        history[0].IssueTitle.Should().Be("Fix the flaky test");
        history[0].FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task GetHistory_ReturnsNewestFirst()
    {
        var service = CreateService();
        var baseTime = DateTimeOffset.UtcNow;

        // CRITICAL: Insert in chronological order (oldest first, newest last).
        // Filesystem uses LIFO (Insert(0,...)), Postgres uses ORDER BY StartedAt DESC.
        // Both produce newest-first ONLY when insertion order matches chronological order.
        // TODO(#1776): Add a complementary test where runs are inserted out-of-chronological-order
        // (e.g., newest first, then oldest) to detect ordering parity divergence between
        // filesystem (insertion-order) and Postgres (timestamp-order) implementations.
        var oldest = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-1", "Oldest",
            startedAt: baseTime.AddHours(-2));
        var middle = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-2", "Middle",
            startedAt: baseTime.AddHours(-1));
        var newest = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-3", "Newest",
            startedAt: baseTime);

        await service.AddRunToHistoryAsync(oldest);
        await service.AddRunToHistoryAsync(middle);
        await service.AddRunToHistoryAsync(newest);

        var history = await service.GetRunHistoryAsync();

        history.Should().HaveCount(3);
        history[0].IssueIdentifier.Should().Be("issue-3"); // newest first
        history[1].IssueIdentifier.Should().Be("issue-2");
        history[2].IssueIdentifier.Should().Be("issue-1"); // oldest last
    }

    [Fact]
    public async Task MaxHistorySize_OldestEvicted()
    {
        var service = CreateService();
        // TODO(#1776): MaxHistorySize is referenced from PipelineRunHistoryService (filesystem class).
        // PostgresPipelineRunHistoryService has its own constant. If they diverge, the Postgres
        // contract test will silently use the wrong boundary. Consider an interface-level constant
        // or asserting both implementations share the same value.
        const int maxSize = PipelineRunHistoryService.MaxHistorySize; // 1000
        const int overflow = 5;
        var baseTime = DateTimeOffset.UtcNow.AddHours(-maxSize - overflow);

        // Insert runs in chronological order (oldest first)
        // TODO(#1776): The filesystem implementation uses fire-and-forget PersistRunSummaryAsync.
        // With 1005 iterations, hundreds of concurrent file writes may still be in-flight,
        // potentially causing flaky Dispose() failures under CI load.
        for (var i = 0; i < maxSize + overflow; i++)
        {
            var run = CreateCompletedRun(
                Guid.NewGuid().ToString(),
                $"issue-{i}",
                $"Run {i}",
                startedAt: baseTime.AddMinutes(i));
            await service.AddRunToHistoryAsync(run);
        }

        var history = await service.GetRunHistoryAsync();

        // Should be capped at MaxHistorySize
        history.Should().HaveCount(maxSize);

        // The oldest 5 should have been evicted
        history.Should().NotContain(s => s.IssueIdentifier == "issue-0");
        history.Should().NotContain(s => s.IssueIdentifier == "issue-1");
        history.Should().NotContain(s => s.IssueIdentifier == "issue-2");
        history.Should().NotContain(s => s.IssueIdentifier == "issue-3");
        history.Should().NotContain(s => s.IssueIdentifier == "issue-4");

        // The newest should still be present
        history.Should().Contain(s => s.IssueIdentifier == $"issue-{maxSize + overflow - 1}");
    }

    [Fact]
    public async Task EmptyHistory_ReturnsEmptyList()
    {
        var service = CreateService();

        var history = await service.GetRunHistoryAsync();

        history.Should().BeEmpty();
    }

    [Fact]
    public async Task AddSameRunTwice_HandledGracefully()
    {
        var service = CreateService();
        var runId = Guid.NewGuid().ToString();
        var run = CreateCompletedRun(runId, "org/repo#10", "Duplicate test");

        // Should not throw when adding the same RunId twice
        await service.AddRunToHistoryAsync(run);
        var act = () => service.AddRunToHistoryAsync(run);
        await act.Should().NotThrowAsync();

        // At least one entry with that RunId exists in history
        // (Postgres upserts → 1, Filesystem inserts duplicates → 2; both are valid)
        // TODO(#1776): Strengthen assertion to verify count is >= 1 && <= 2 to rule out data corruption
        // while remaining permissive about implementation-specific deduplication behavior.
        var history = await service.GetRunHistoryAsync();
        history.Should().Contain(s => s.RunId == runId);
    }

    [Fact]
    public async Task AddRun_ConsolidationRun_NotPersisted()
    {
        var service = CreateService();

        var consolidationRun = PipelineRun.CreateImplementation(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: "consolidation-issue",
            issueTitle: "Consolidation run",
            issueProviderConfigId: ConsolidationConstants.ProviderConfigId,
            repoProviderConfigId: "rp-1",
            initiatedBy: ConsolidationConstants.InitiatedBy);
        consolidationRun.CurrentStep = PipelineStep.Completed;
        consolidationRun.MarkCompleted();

        // Should not throw
        await service.AddRunToHistoryAsync(consolidationRun);

        // Should not appear in history
        var history = await service.GetRunHistoryAsync();
        history.Should().BeEmpty();
    }

    // TODO: [WARNING] No contract-level test covers the symmetric discriminator: a run with
    // InitiatedBy = ConsolidationConstants.InitiatedBy but IssueProviderConfigId = null (legacy row).
    // The existing AddRun_ConsolidationRun_NotPersisted test sets both discriminators via
    // ConsolidationConstants.InitiatedBy + ConsolidationConstants.ProviderConfigId, so the InitiatedBy
    // arm of the read filter is not independently verified. A regression that removes the InitiatedBy
    // && clause would go undetected at this contract layer. Add an overload of
    // GetHistory_ExcludesConsolidationRun that uses SupportsDirectGhostInjection to insert a row with
    // InitiatedBy = ConsolidationConstants.InitiatedBy and IssueProviderConfigId = null to cover this gap.

    [Fact]
    public async Task AddRun_PreservesKeyProperties()
    {
        var service = CreateService();
        var runId = Guid.NewGuid().ToString();
        var startedAt = new DateTimeOffset(2026, 6, 15, 10, 30, 0, TimeSpan.Zero);

        var run = PipelineRun.CreateImplementation(
            runId: runId,
            issueIdentifier: "org/repo#99",
            issueTitle: "Preserve all fields",
            issueProviderConfigId: "ip-fidelity",
            repoProviderConfigId: "rp-fidelity",
            startedAt: startedAt);
        run.CurrentStep = PipelineStep.Completed;
        run.RetryCount = 3;
        run.MarkCompleted(new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero));

        await service.AddRunToHistoryAsync(run);

        var history = await service.GetRunHistoryAsync();
        history.Should().HaveCount(1);

        var restored = history[0];
        restored.RunId.Should().Be(runId);
        restored.IssueIdentifier.Should().Be("org/repo#99");
        restored.IssueTitle.Should().Be("Preserve all fields");
        restored.FinalStep.Should().Be(PipelineStep.Completed);
        restored.StartedAtOffset.Should().Be(startedAt);
        restored.CompletedAtOffset.Should().Be(new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero));
        restored.RetryCount.Should().Be(3);
    }

    /// <summary>
    /// Regression test for the dual-discriminator filter fix.
    /// A consolidation ghost entry with null/missing InitiatedBy (e.g., from a corrupt/fallback
    /// deserialization path) must still be excluded from pipeline history because its
    /// IssueProviderConfigId matches the consolidation sentinel.
    ///
    /// When <see cref="SupportsDirectGhostInjection"/> is true (Postgres-backed), the ghost entry
    /// is inserted directly into the backing store, bypassing the write guard — this exercises the
    /// read-time dual-discriminator filter independently.
    ///
    /// When <see cref="SupportsDirectGhostInjection"/> is false (filesystem-backed), the ghost entry
    /// is submitted via <see cref="IPipelineRunHistoryService.AddRunToHistoryAsync"/>; the write guard
    /// rejects it, so the test validates write-guard behavior (the entry never reaches the read filter).
    /// </summary>
    [Fact]
    public async Task GetHistory_ExcludesConsolidationRun_WhenInitiatedByIsNullButProviderConfigIdMatches()
    {
        var service = CreateService();

        // A normal run — should appear in history
        var normalRun = CreateCompletedRun(
            Guid.NewGuid().ToString(),
            "org/repo#1",
            "Normal run");
        await service.AddRunToHistoryAsync(normalRun);

        // Build a ghost summary: IssueProviderConfigId = consolidation sentinel, but InitiatedBy = "manual".
        // This simulates a row whose SummaryJson was written before InitiatedBy was set, or a corrupt/fallback
        // deserialization path that produces InitiatedBy=null (serialized as absent, deserialized as "manual").
        // The old filter only checked InitiatedBy != "consolidation", so this entry would leak through.
        // The new dual-discriminator filter also checks IssueProviderConfigId != sentinel, closing the gap.
        var ghostSummary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "ghost-consolidation",
            IssueTitle = "Ghost consolidation entry",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-1),
            InitiatedBy = "manual", // NOT the consolidation sentinel — old filter would pass this
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId // discriminator the new filter must catch
        };

        if (SupportsDirectGhostInjection)
        {
            // Bypass the write guard: insert the ghost entry directly into the backing store so that
            // only the read-time filter can prevent it from appearing in history.
            await InsertGhostSummaryDirectlyAsync(ghostSummary);
        }
        else
        {
            // TODO: [WARNING] Filesystem-backed implementations do not support direct ghost injection,
            // so this else branch submits the ghost run through AddRunToHistoryAsync, which has its own
            // write guard that rejects it at write time. The assertion passes because the write guard
            // prevented storage — not because the read-time dual-discriminator filter worked correctly.
            // The read-time filter for the new IssueProviderConfigId discriminator is therefore never
            // exercised on non-Postgres implementations. A regression in the read-time filter would be
            // invisible for filesystem-backed implementations. To close this gap, either add a direct
            // injection path to non-Postgres implementations or document that read-time filter coverage
            // only applies to Postgres-backed tests.
            // Filesystem-backed: no direct injection path. Submit via the normal API; the write guard
            // will reject it. The assertion below still verifies the entry is absent, validating the
            // write guard's IssueProviderConfigId check.
            var ghostRun = PipelineRun.CreateImplementation(
                runId: ghostSummary.RunId,
                issueIdentifier: ghostSummary.IssueIdentifier,
                issueTitle: ghostSummary.IssueTitle,
                issueProviderConfigId: ConsolidationConstants.ProviderConfigId,
                repoProviderConfigId: "rp-1",
                initiatedBy: "manual");
            ghostRun.CurrentStep = PipelineStep.Completed;
            ghostRun.MarkCompleted();
            await service.AddRunToHistoryAsync(ghostRun);
        }

        var history = await service.GetRunHistoryAsync();

        // Only the normal run should appear. When SupportsDirectGhostInjection=true, the read-time
        // dual-discriminator filter must exclude the ghost entry. When false, the write guard does it.
        history.Should().HaveCount(1, "consolidation run with matching ProviderConfigId must be excluded even when InitiatedBy does not match consolidation sentinel");
        history[0].IssueIdentifier.Should().Be("org/repo#1");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a completed <see cref="PipelineRun"/> with terminal step.
    /// Uses terminal steps exclusively to avoid BUG-12 divergence
    /// (Postgres forces non-terminal to Failed; filesystem does not).
    /// </summary>
    private static PipelineRun CreateCompletedRun(
        string runId,
        string issueIdentifier,
        string issueTitle,
        DateTimeOffset? startedAt = null)
    {
        var run = PipelineRun.CreateImplementation(
            runId,
            issueIdentifier,
            issueTitle,
            "ip-contract",
            "rp-contract",
            startedAt: startedAt ?? DateTimeOffset.UtcNow);
        run.CurrentStep = PipelineStep.Completed;
        run.MarkCompleted();
        return run;
    }
}

// ── FileSystem-backed implementation ────────────────────────────────────────

/// <summary>
/// Runs the contract tests against <see cref="PipelineRunHistoryService"/> (filesystem-backed).
/// </summary>
public class FilePipelineRunHistoryServiceContractTests : PipelineRunHistoryServiceContractTests
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"contract-history-fs-{Guid.NewGuid()}");

    public FilePipelineRunHistoryServiceContractTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    protected override IPipelineRunHistoryService CreateService()
        => new PipelineRunHistoryService(new Mock<ILogger>().Object, _tempDir);

    public override void Dispose()
    {
        if (!Directory.Exists(_tempDir))
            return;

        // The file-based implementation uses fire-and-forget PersistRunSummaryAsync which may
        // still hold a .tmp file open via AtomicFileWriter (Windows FlushFileBuffers can take
        // 100-500ms). Retry with fixed delay; on final failure delete files individually,
        // skipping locked .tmp files, so the directory itself can be removed.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
                base.Dispose();
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                // Final attempt: delete files individually, skipping locked .tmp files.
                // Locked .tmp files are in-flight AtomicFileWriter temp files; the OS reclaims
                // them when the process exits. Do not propagate cleanup failures as test failures.
                try
                {
                    foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                        try { File.Delete(file); } catch (IOException) { }
                    Directory.Delete(_tempDir, recursive: true);
                }
                catch { /* best-effort — leftover temp files are harmless */ }
            }
        }
        base.Dispose();
    }
}

// ── Postgres-backed implementation (InMemory EF) ────────────────────────────

/// <summary>
/// Runs the contract tests against <see cref="PostgresPipelineRunHistoryService"/> using InMemory EF Core.
/// </summary>
// TODO(#1776): InMemory EF provider does not faithfully replicate Postgres DateTimeOffset/timezone handling.
// The ordering guarantee test cannot surface real Postgres timezone edge cases with this approach.
// Consider a Testcontainers-based integration test for full Postgres fidelity.
public class PostgresPipelineRunHistoryServiceContractTests : PipelineRunHistoryServiceContractTests
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public PostgresPipelineRunHistoryServiceContractTests()
    {
        var dbName = $"RunHistoryContractTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new PipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();
    }

    protected override bool SupportsDirectGhostInjection => true;

    /// <summary>
    /// Inserts the ghost summary directly into the in-memory EF store, bypassing
    /// <see cref="PostgresPipelineRunHistoryService.AddRunToHistoryAsync"/> and its write guard.
    /// This exercises the read-time dual-discriminator filter in isolation.
    /// </summary>
    protected override async Task InsertGhostSummaryDirectlyAsync(PipelineRunSummary summary)
    {
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.PipelineRuns.Add(new PipelineRunEntity
        {
            RunId = Guid.Parse(summary.RunId),
            IssueIdentifier = summary.IssueIdentifier,
            IssueTitle = summary.IssueTitle,
            FinalStep = summary.FinalStep,
            StartedAt = summary.StartedAtOffset,
            SummaryJson = JsonSerializer.Serialize(summary, PipelineJsonOptions.Default)
        });
        await db.SaveChangesAsync();
    }

    protected override IPipelineRunHistoryService CreateService()
    {
        var factory = new RunHistoryContractTestDbContextFactory(_dbOptions);
        return new PostgresPipelineRunHistoryService(factory, new Mock<ILogger>().Object);
    }

    public override void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        base.Dispose();
    }

    /// <summary>
    /// InMemory EF provider does not support concurrency tokens (xmin) or partial indexes.
    /// This subclass disables them to allow EF model creation against InMemory.
    /// </summary>
    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            // Remove partial indexes (not supported by InMemory provider)
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
                    modelBuilder.Entity(entityType.ClrType).HasIndex(
                        index.Properties.Select(p => p.Name).ToArray())
                        .HasFilter(null);
            }
        }
    }
}

/// <summary>Helper: IDbContextFactory for InMemory provider.</summary>
file class RunHistoryContractTestDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;
    public RunHistoryContractTestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
    public PipelineDbContext CreateDbContext() => new(_options);
    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}
