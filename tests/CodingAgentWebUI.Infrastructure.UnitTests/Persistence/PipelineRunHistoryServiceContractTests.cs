using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
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
        history[0].IssueIdentifier.Should().Be((IssueIdentifier)"org/repo#42");
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
        history[0].IssueIdentifier.Should().Be((IssueIdentifier)"issue-3"); // newest first
        history[1].IssueIdentifier.Should().Be((IssueIdentifier)"issue-2");
        history[2].IssueIdentifier.Should().Be((IssueIdentifier)"issue-1"); // oldest last
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

        var consolidationRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "consolidation-issue",
            IssueTitle = "Consolidation run",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1",
            InitiatedBy = ConsolidationConstants.InitiatedBy
        });
        consolidationRun.CurrentStep = PipelineStep.Completed;
        consolidationRun.MarkCompleted();

        // Should not throw
        await service.AddRunToHistoryAsync(consolidationRun);

        // Should not appear in history
        var history = await service.GetRunHistoryAsync();
        history.Should().BeEmpty();
    }

    [Fact]
    public async Task AddRun_PreservesKeyProperties()
    {
        var service = CreateService();
        var runId = Guid.NewGuid().ToString();
        var startedAt = new DateTimeOffset(2026, 6, 15, 10, 30, 0, TimeSpan.Zero);

        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Preserve all fields",
            IssueProviderConfigId = "ip-fidelity",
            RepoProviderConfigId = "rp-fidelity",
            StartedAt = startedAt
        });
        run.CurrentStep = PipelineStep.Completed;
        run.RetryCount = 3;
        run.MarkCompleted(new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero));

        await service.AddRunToHistoryAsync(run);

        var history = await service.GetRunHistoryAsync();
        history.Should().HaveCount(1);

        var restored = history[0];
        restored.RunId.Should().Be(runId);
        restored.IssueIdentifier.Should().Be((IssueIdentifier)"org/repo#99");
        restored.IssueTitle.Should().Be("Preserve all fields");
        restored.FinalStep.Should().Be(PipelineStep.Completed);
        restored.StartedAtOffset.Should().Be(startedAt);
        restored.CompletedAtOffset.Should().Be(new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero));
        restored.RetryCount.Should().Be(3);
    }

    // ── Pagination edge cases ─────────────────────────────────────────────

    [Fact]
    public async Task GetRunHistoryPaged_ExtremelyLargePage_ThrowsArgumentOutOfRangeException()
    {
        var service = CreateService();

        // page=2_147_485 with pageSize=1000 causes (page-1)*pageSize to overflow int.MaxValue:
        // (2_147_485 - 1) * 1000 = 2_147_484_000 > int.MaxValue (2_147_483_647)
        // The pre-check guard uses (long)(page - 1) * pageSize > int.MaxValue, which fires before
        // the checked() expression is reached, converting the unhandled OverflowException into a
        // descriptive ArgumentOutOfRangeException. Both implementations share this guard via the
        // same validation block.
        Func<Task> act = () => service.GetRunHistoryAsync(page: 2_147_485, pageSize: 1000);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetRunHistoryPaged_MaxValidPage_DoesNotThrow()
    {
        var service = CreateService();

        // page=2_147_484 with pageSize=1000 gives (2_147_484-1)*1000 = 2_147_483_000, which is just
        // under int.MaxValue (2_147_483_647) and must not throw. This pins the exact overflow threshold:
        // an over-conservative guard using integer division (int.MaxValue / pageSize = 2_147_483) would
        // incorrectly reject this valid page value and fail here.
        Func<Task> act = () => service.GetRunHistoryAsync(page: 2_147_484, pageSize: 1000);

        // TODO: Add a complementary assertion on the return value (e.g. non-null PagedResult, HasMore==false)
        // to confirm the method completed a successful query path. Currently, NotThrowAsync() would pass even
        // if the implementation silently swallowed an unexpected exception and returned a default/null value.
        await act.Should().NotThrowAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a completed <see cref="PipelineRun"/> with terminal step.
    /// </summary>
    private static PipelineRun CreateCompletedRun(
        string runId,
        string issueIdentifier,
        string issueTitle,
        DateTimeOffset? startedAt = null)
    {
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = issueTitle,
            IssueProviderConfigId = "ip-contract",
            RepoProviderConfigId = "rp-contract",
            StartedAt = startedAt ?? DateTimeOffset.UtcNow
        });
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
        GC.SuppressFinalize(this);
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

    protected override IPipelineRunHistoryService CreateService()
    {
        var factory = new RunHistoryContractTestDbContextFactory(_dbOptions);
        return new PostgresPipelineRunHistoryService(factory, new Mock<ILogger>().Object);
    }

    public override void Dispose()
    {
        GC.SuppressFinalize(this);
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        base.Dispose();
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
