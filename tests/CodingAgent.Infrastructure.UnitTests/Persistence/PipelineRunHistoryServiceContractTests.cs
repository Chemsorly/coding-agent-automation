using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Contract tests for <see cref="IPipelineRunHistoryService"/> implementations.
/// <see cref="PostgresPipelineRunHistoryService"/>, the only persistent implementation,
/// must satisfy these behavioral contracts.
///
/// Pattern follows the established contract test structure used across persistence services.
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

        // Insert in chronological order; Postgres returns history ordered by StartedAt DESC.
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
        const int maxSize = PostgresPipelineRunHistoryService.MaxHistorySize; // 1000
        const int overflow = 5;
        var baseTime = DateTimeOffset.UtcNow.AddHours(-maxSize - overflow);

        // Insert runs in chronological order (oldest first)
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

        // Postgres upserts by RunId — exactly one entry must exist
        var history = await service.GetRunHistoryAsync();
        history.Where(s => s.RunId == runId).Should().ContainSingle();
    }

    [Fact]
    public async Task AddRun_ConsolidationRun_DoesNotThrow()
    {
        // Issue #3024: consolidation exclusion guards were removed from all history service
        // implementations. Consolidation runs must be accepted (no silent drop, no exception).
        // The contract here is: must not throw.
        // TODO: This test creates a run via PipelineRun.CreateImplementation with
        // IssueProviderConfigId = ConsolidationConstants.ProviderConfigId and
        // InitiatedBy = ConsolidationConstants.InitiatedBy, which causes IsConsolidationGhost
        // to return true in PostgresPipelineRunHistoryService — the row is silently filtered from
        // GetRunHistoryAsync. The test name "DoesNotThrow" is therefore the complete contract here:
        // this is a ghost row and is not expected to appear in history. For the retrieval contract
        // on a real consolidation run (RunType = Consolidation), see
        // AddRunSummaryAsync_ConsolidationRunType_IsReturnedByGetRunHistory.
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

        // Must complete without throwing
        var act = async () => await service.AddRunToHistoryAsync(consolidationRun);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AddRunSummaryAsync_ConsolidationSummary_DoesNotThrow()
    {
        // Issue #3024: consolidation exclusion guards were removed. AddRunSummaryAsync must
        // accept consolidation-prefixed summaries without throwing.
        var service = CreateService();

        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "consolidation-summary-test",
            IssueTitle = "Consolidation summary",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            InitiatedBy = ConsolidationConstants.InitiatedBy,   // "consolidation:manual"
        };

        // Must complete without throwing
        var act = async () => await service.AddRunSummaryAsync(summary);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AddRunSummaryAsync_ConsolidationRunType_IsReturnedByGetRunHistory()
    {
        // Issue #3025 read-path guarantee: a summary with RunType = Consolidation and
        // InitiatedBy = ConsolidationConstants.InitiatedBy must be retrievable via GetRunHistoryAsync.
        // RunId must be a GUID string because PostgresPipelineRunHistoryService.ToEntity parses it
        // with Guid.TryParse; a non-GUID RunId causes a new GUID to be generated as the PK, which
        // would break the SummaryJson round-trip lookup.
        var service = CreateService();
        var runId = Guid.NewGuid().ToString();

        var summary = new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "consolidation-readback",
            IssueTitle = "Consolidation readback",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            InitiatedBy = ConsolidationConstants.InitiatedBy,
            RunType = PipelineRunType.Consolidation
        };

        await service.AddRunSummaryAsync(summary);

        var history = await service.GetRunHistoryAsync();
        var returned = history.Where(s => s.RunId == runId).Should().ContainSingle().Subject;
        returned.RunType.Should().Be(PipelineRunType.Consolidation);
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
        // descriptive ArgumentOutOfRangeException.
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
