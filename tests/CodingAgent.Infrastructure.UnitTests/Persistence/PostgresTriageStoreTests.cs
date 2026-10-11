using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CodingAgent.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="PostgresTriageStore"/> on InMemory EF Core: the record round trip, the list filters,
/// result and attempt recording, the backfill and the retries. Row locks, ILIKE, the unique key and retention's
/// ExecuteDelete need PostgreSQL and are covered by the integration tests.
/// </summary>
public sealed class PostgresTriageStoreTests : IDisposable
{
    private static readonly string ProjectA = Guid.NewGuid().ToString("D");
    private static readonly string ProjectB = Guid.NewGuid().ToString("D");

    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public PostgresTriageStoreTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"TriageStoreTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new PipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    private PostgresTriageStore Store() => new(new InMemoryDbContextFactory(_dbOptions));

    // ── Get / Create ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ThenGet_RoundTripsTheRecord()
    {
        var record = Operator(ProjectA, "Orders stuck in Pending");

        await Store().CreateAsync(record);

        (await Store().GetAsync(record.Id)).Should().BeEquivalentTo(record);
        (await Store().GetAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task GetByIssue_FindsATrackerTriageByItsIssue()
    {
        var record = Issue(ProjectA, "tracker-1", "431");
        await Store().CreateAsync(record);

        (await Store().GetByIssueAsync("tracker-1", "431"))!.Id.Should().Be(record.Id);
        (await Store().GetByIssueAsync("tracker-1", "432")).Should().BeNull();
    }

    [Fact]
    public async Task GetActiveWorkItemId_ReturnsOnlyARunningWorkItem()
    {
        var record = Operator(ProjectA, "t");
        await Store().CreateAsync(record);
        var key = TriageConstants.IssueIdentifierFor(record.Id);
        await AddWorkItemAsync(key, TriageConstants.ProviderConfigId, WorkItemStatus.Failed);

        (await Store().GetActiveWorkItemIdAsync(record)).Should().BeNull();

        var activeId = await AddWorkItemAsync(key, TriageConstants.ProviderConfigId, WorkItemStatus.Pending);
        (await Store().GetActiveWorkItemIdAsync(record)).Should().Be(activeId.ToString());
    }

    // ── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ResolvesRunningFromWorkItems_AndFiltersByTabProjectSourceAndSearch()
    {
        var running = Operator(ProjectA, "Checkout returns 502") with { Attempts = [Attempt(Guid.NewGuid().ToString())] };
        var review = Operator(ProjectA, "Orders stuck in Pending") with
        {
            State = TriageState.NeedsReview,
            Attempts = [Attempt("w2") with { Result = Result(TriageVerdict.CauseFound), Outcome = TriageAttemptOutcome.Completed }],
        };
        var tracker = Issue(ProjectA, "tracker-1", "431") with
        {
            State = TriageState.NotABug,
            Attempts = [Attempt("w3") with { Result = Result(TriageVerdict.NotABug), Outcome = TriageAttemptOutcome.Completed }],
        };
        var otherProject = Operator(ProjectB, "Login slow");
        foreach (var r in new[] { running, review, tracker, otherProject })
            await Store().CreateAsync(r);
        await AddWorkItemAsync(TriageConstants.IssueIdentifierFor(running.Id), TriageConstants.ProviderConfigId, WorkItemStatus.Running);

        var all = await Store().ListAsync(new TriageListQuery { ProjectId = ProjectA });
        var needYou = await Store().ListAsync(new TriageListQuery { ProjectId = ProjectA, Tab = TriageListTab.NeedYou });
        var investigating = await Store().ListAsync(new TriageListQuery { ProjectId = ProjectA, Tab = TriageListTab.Investigating });
        var issues = await Store().ListAsync(new TriageListQuery { ProjectId = ProjectA, Source = TriageSource.Issue });
        var search = await Store().ListAsync(new TriageListQuery { ProjectId = ProjectA, Search = "  pEnDiNg " });
        var everything = await Store().ListAsync(new TriageListQuery());

        all.Total.Should().Be(3);
        all.Items.Single(i => i.Id == running.Id).Status.Should().Be(TriageStatus.Investigating);
        all.Items.Single(i => i.Id == running.Id).IssueIdentifier.Should().BeNull("an operator triage has no issue");
        needYou.Items.Select(i => i.Id).Should().Equal(review.Id);
        investigating.Items.Select(i => i.Id).Should().Equal(running.Id);
        var issue = issues.Items.Should().ContainSingle().Subject;
        issue.Source.Should().Be(TriageSource.Issue);
        issue.IssueProviderConfigId.Should().Be("tracker-1");
        issue.IssueIdentifier.Should().Be("431");
        issue.ProjectId.Should().Be(ProjectA);
        search.Items.Select(i => i.Id).Should().Equal(review.Id);
        everything.Total.Should().Be(4);
    }

    [Fact]
    public async Task List_InvalidProjectId_IsEmpty()
    {
        await Store().CreateAsync(Operator(ProjectA, "t"));

        var page = await Store().ListAsync(new TriageListQuery { ProjectId = "not-a-guid" });

        page.Items.Should().BeEmpty();
        page.Total.Should().Be(0);
    }

    [Fact]
    public async Task List_PagesTheNewestFirst_AndClampsThePage()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await Store().CreateAsync(Operator(ProjectA, $"t{i}") with { UpdatedAt = now.AddMinutes(i) });

        var second = await Store().ListAsync(new TriageListQuery { ProjectId = ProjectA, Page = 2, PageSize = 2 });
        var clamped = await Store().ListAsync(new TriageListQuery { ProjectId = ProjectA, Page = 0, PageSize = 0 });

        second.Total.Should().Be(5);
        second.Items.Select(i => i.Title).Should().Equal("t2", "t1");
        clamped.Items.Select(i => i.Title).Should().Equal("t4");
    }

    [Fact]
    public async Task ListRecent_ReturnsTheProjectsNewestTriages_WithoutTheExcludedOne()
    {
        var a = Operator(ProjectA, "a") with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var b = Operator(ProjectA, "b");
        var tooOld = Operator(ProjectA, "old") with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-60) };
        var other = Operator(ProjectB, "other");
        foreach (var r in new[] { a, b, tooOld, other })
            await Store().CreateAsync(r);

        var recent = await Store().ListRecentAsync(ProjectA, DateTimeOffset.UtcNow.AddDays(-30), 10, excludeId: b.Id);
        var all = await Store().ListRecentAsync(ProjectA, DateTimeOffset.MinValue, 10, excludeId: null);

        recent.Select(r => r.Id).Should().Equal(a.Id);
        all.Select(r => r.Id).Should().Equal(b.Id, a.Id, tooOld.Id);
    }

    [Theory]
    [InlineData("not-a-guid", 10)]
    [InlineData(null, 0)]
    public async Task ListRecent_InvalidProjectOrNoRoom_IsEmpty(string? projectId, int max)
    {
        await Store().CreateAsync(Operator(ProjectA, "a"));

        var recent = await Store().ListRecentAsync(projectId ?? ProjectA, DateTimeOffset.MinValue, max, excludeId: null);

        recent.Should().BeEmpty();
    }

    // ── Update ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_AppliesTheChange_AndStampsTheTime()
    {
        var time = new FixedTime(DateTimeOffset.Parse("2026-10-11T08:00:00Z"));
        var record = Operator(ProjectA, "t");
        await Store().CreateAsync(record);

        var updated = await new PostgresTriageStore(new InMemoryDbContextFactory(_dbOptions), time)
            .UpdateAsync(record.Id, r => r with { Title = "renamed" });

        updated!.Title.Should().Be("renamed");
        updated.UpdatedAt.Should().Be(time.Now);
        (await Store().GetAsync(record.Id))!.Title.Should().Be("renamed");
    }

    [Fact]
    public async Task Update_NoChange_ReturnsTheStoredRecord()
    {
        var record = Operator(ProjectA, "t");
        await Store().CreateAsync(record);

        var result = await Store().UpdateAsync(record.Id, _ => null);

        result.Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task Update_MissingTriage_ReturnsNull()
    {
        (await Store().UpdateAsync(Guid.NewGuid(), r => r)).Should().BeNull();
    }

    [Fact]
    public async Task Update_AVersionConflict_IsRetried()
    {
        var record = Operator(ProjectA, "t");
        await Store().CreateAsync(record);
        var store = new PostgresTriageStore(new FailingSaveFactory(_dbOptions, failures: 1, () => new DbUpdateConcurrencyException("conflict")));

        var updated = await store.UpdateAsync(record.Id, r => r with { Title = "renamed" });

        updated!.Title.Should().Be("renamed");
    }

    [Fact]
    public async Task Update_ConflictsOnEveryAttempt_Throws()
    {
        var record = Operator(ProjectA, "t");
        await Store().CreateAsync(record);
        var store = new PostgresTriageStore(new FailingSaveFactory(_dbOptions, failures: 3, () => new DbUpdateConcurrencyException("conflict")));

        var act = () => store.UpdateAsync(record.Id, r => r with { Title = "renamed" });

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    // ── RecordResult ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordResult_OperatorTriage_CompletesItsAttempt_AndReplacesTheDrafts()
    {
        var record = Operator(ProjectA, "t") with { Attempts = [Attempt("w1")], DismissReason = "old", DismissedBy = "bob" };
        await Store().CreateAsync(record);

        var saved = await Store().RecordResultAsync(Report(record, "w1", TriageVerdict.CauseFound, drafts: 2));

        saved.State.Should().Be(TriageState.NeedsReview);
        saved.Attempts.Should().ContainSingle().Which.Outcome.Should().Be(TriageAttemptOutcome.Completed);
        saved.Drafts.Select(d => d.Current.Id).Should().Equal("a1-d1", "a1-d2");
        saved.Drafts.Should().AllSatisfy(d => d.Original.Should().BeEquivalentTo(d.Current));
        saved.DraftsFromWorkItemId.Should().Be("w1");
        saved.DismissReason.Should().BeNull();
        saved.DismissedBy.Should().BeNull();
        (await Store().GetAsync(record.Id))!.Attempts[0].Result.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordResult_ASecondAttempt_PrefixesItsDraftsWithItsNumber()
    {
        var record = Operator(ProjectA, "t") with { Attempts = [Attempt("w1")] };
        await Store().CreateAsync(record);
        await Store().RecordResultAsync(Report(record, "w1", TriageVerdict.CauseFound, drafts: 1));
        await Store().UpdateAsync(record.Id, r => r with { Attempts = [.. r.Attempts, Attempt("w2")] });

        var saved = await Store().RecordResultAsync(Report(record, "w2", TriageVerdict.CauseFound, drafts: 1));

        saved.Drafts.Select(d => d.Current.Id).Should().Equal("a2-d1");
        saved.DraftsFromWorkItemId.Should().Be("w2");
    }

    [Fact]
    public async Task RecordResult_OperatorTriageWithoutRow_Throws()
    {
        var act = () => Store().RecordResultAsync(Report(Operator(ProjectA, "t"), "w1", TriageVerdict.CauseFound));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No triage exists*");
    }

    [Fact]
    public async Task RecordResult_TrackerIssue_FirstResultCreatesTheTriage_SecondAddsAnAttempt()
    {
        var report = TrackerReport("w1", TriageVerdict.Inconclusive) with { IssueTitle = "Order confirmation spins forever" };

        var first = await Store().RecordResultAsync(report);
        var second = await Store().RecordResultAsync(report with { WorkItemId = "w2", Result = Result(TriageVerdict.CauseFound) });

        first.Title.Should().Be("Order confirmation spins forever");
        first.IssueUrl.Should().Be("https://example.invalid/issues/431");
        first.State.Should().Be(TriageState.NeedsInput);
        second.Id.Should().Be(first.Id);
        second.Attempts.Select(a => a.WorkItemId).Should().Equal("w1", "w2");
        second.State.Should().Be(TriageState.NeedsReview);
    }

    [Fact]
    public async Task RecordResult_TrackerIssueWithoutTitle_IsNamedByItsNumber()
    {
        var saved = await Store().RecordResultAsync(TrackerReport("w1", TriageVerdict.NotABug) with { IssueTitle = " " });

        saved.Title.Should().Be("#431");
    }

    [Fact]
    public async Task RecordResult_TriageOfAnotherProject_IsRefused()
    {
        await Store().RecordResultAsync(TrackerReport("w1", TriageVerdict.CauseFound));

        var act = () => Store().RecordResultAsync(TrackerReport("w2", TriageVerdict.CauseFound) with { ProjectId = ProjectB });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*belongs to another project*");
    }

    [Fact]
    public async Task RecordResult_ASaveConflict_IsRetried()
    {
        var store = new PostgresTriageStore(new FailingSaveFactory(_dbOptions, failures: 2, () => new DbUpdateException("duplicate key")));

        var saved = await store.RecordResultAsync(TrackerReport("w1", TriageVerdict.CauseFound));

        saved.Attempts.Should().ContainSingle();
        (await Store().GetByIssueAsync("tracker-1", "431")).Should().NotBeNull();
    }

    // ── RecordAttemptStart ───────────────────────────────────────────────────

    [Fact]
    public async Task RecordAttemptStart_NewIssue_CreatesTheTriageWithAnOpenAttempt()
    {
        var saved = await Store().RecordAttemptStartAsync(Start("w1"));

        saved!.Source.Should().Be(TriageSource.Issue);
        saved.Title.Should().Be("Order confirmation spins forever");
        saved.IssueUrl.Should().Be("https://example.invalid/issues/431");
        var attempt = saved.Attempts.Should().ContainSingle().Subject;
        attempt.WorkItemId.Should().Be("w1");
        attempt.Result.Should().BeNull();
        (await Store().GetByIssueAsync("tracker-1", "431"))!.Id.Should().Be(saved.Id);
    }

    [Fact]
    public async Task RecordAttemptStart_SameRunTwice_RecordsOneAttempt_AndANewRunAppends()
    {
        await Store().RecordAttemptStartAsync(Start("w1"));

        var again = await Store().RecordAttemptStartAsync(Start("w1"));
        var next = await Store().RecordAttemptStartAsync(Start("w2") with { IssueTitle = null });

        again!.Attempts.Should().ContainSingle();
        next!.Attempts.Select(a => a.WorkItemId).Should().Equal("w1", "w2");
        next.Title.Should().Be("Order confirmation spins forever", "the stored title stays");
    }

    [Fact]
    public async Task RecordAttemptStart_TriageOfAnotherProject_ReturnsNull_AndChangesNothing()
    {
        var first = await Store().RecordAttemptStartAsync(Start("w1"));

        var other = await Store().RecordAttemptStartAsync(Start("w2") with { ProjectId = ProjectB });

        other.Should().BeNull();
        (await Store().GetAsync(first!.Id))!.Attempts.Should().ContainSingle();
    }

    [Fact]
    public async Task RecordAttemptStart_WithoutTitle_IsNamedByItsNumber()
    {
        var saved = await Store().RecordAttemptStartAsync(Start("w1") with { IssueTitle = null });

        saved!.Title.Should().Be("#431");
    }

    // ── Backfill ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_CopiesHowRunsEnded_AndLeavesRunningOnesAlone()
    {
        var failedId = Guid.NewGuid();
        var cancelledId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        var succeededId = Guid.NewGuid();
        var failed = Operator(ProjectA, "failed") with { Attempts = [Attempt(failedId.ToString())] };
        var cancelled = Operator(ProjectA, "cancelled") with { Attempts = [Attempt(cancelledId.ToString())] };
        var gone = Operator(ProjectA, "gone") with { Attempts = [Attempt("not-a-guid")] };
        var running = Operator(ProjectA, "running") with { Attempts = [Attempt(runningId.ToString())] };
        var succeeded = Operator(ProjectA, "succeeded") with { Attempts = [Attempt(succeededId.ToString())] };
        var done = Operator(ProjectA, "done") with
        {
            Attempts = [Attempt("w9") with { Result = Result(TriageVerdict.CauseFound), Outcome = TriageAttemptOutcome.Completed }],
        };
        foreach (var r in new[] { failed, cancelled, gone, running, succeeded, done })
            await Store().CreateAsync(r);
        await AddWorkItemAsync("a", "x", WorkItemStatus.Failed, failedId, error: "Agent timeout after 30 min");
        await AddWorkItemAsync("b", "x", WorkItemStatus.Cancelled, cancelledId);
        await AddWorkItemAsync("c", "x", WorkItemStatus.Running, runningId);
        await AddWorkItemAsync("d", "x", WorkItemStatus.Succeeded, succeededId);

        var changed = await Store().BackfillEndedAttemptsAsync();

        changed.Should().Be(4);
        var f = (await Store().GetAsync(failed.Id))!.Attempts.Single();
        f.Outcome.Should().Be(TriageAttemptOutcome.Failed);
        f.FailureReason.Should().Be("Agent timeout after 30 min");
        f.CompletedAt.Should().NotBeNull();
        var c = (await Store().GetAsync(cancelled.Id))!.Attempts.Single();
        c.Outcome.Should().Be(TriageAttemptOutcome.Cancelled);
        c.FailureReason.Should().BeNull();
        var g = (await Store().GetAsync(gone.Id))!.Attempts.Single();
        g.Outcome.Should().Be(TriageAttemptOutcome.Failed);
        g.FailureReason.Should().Be("The run ended without reporting a result.");
        (await Store().GetAsync(succeeded.Id))!.Attempts.Single().FailureReason
            .Should().Be("The run ended without reporting a result.", "a run that succeeded without a result still failed the attempt");
        (await Store().GetAsync(running.Id))!.Attempts.Single().Outcome.Should().BeNull();
        (await Store().BackfillEndedAttemptsAsync()).Should().Be(0, "only the running attempt is still open");
    }

    // ── Mapper ───────────────────────────────────────────────────────────────

    [Fact]
    public void Mapper_KeyOf_UsesTheIssueForATrackerTriage_AndTheSentinelForAnOperatorTriage()
    {
        var issue = Issue(ProjectA, "tracker-1", "431");
        var op = Operator(ProjectA, "t");

        TriageEntityMapper.KeyOf(issue).Should().Be(("tracker-1", "431"));
        TriageEntityMapper.KeyOf(op).Should().Be((TriageConstants.ProviderConfigId, TriageConstants.IssueIdentifierFor(op.Id)));
    }

    [Fact]
    public void Mapper_UnknownValues_FallBackToDefaults()
    {
        TriageEntityMapper.ParseState("gibberish").Should().Be(TriageState.New);
        TriageEntityMapper.ParseState("needsreview").Should().Be(TriageState.NeedsReview);
        TriageEntityMapper.ParseSource("gibberish").Should().Be(TriageSource.Operator);
        TriageEntityMapper.ParseSource("issue").Should().Be(TriageSource.Issue);
        TriageEntityMapper.ReadFacts("null").Should().BeEquivalentTo(new TriageListFacts());
    }

    [Fact]
    public void Mapper_ProjectIdThatIsNoGuid_IsRefused()
    {
        var act = () => TriageEntityMapper.ToEntity(Operator("not-a-guid", "t"));

        act.Should().Throw<ArgumentException>().WithMessage("*not a GUID*");
    }

    [Fact]
    public void Mapper_EntityWithoutData_Throws()
    {
        var act = () => TriageEntityMapper.ToRecord(new TriageEntity { Id = Guid.NewGuid(), Data = "null" });

        act.Should().Throw<InvalidOperationException>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> AddWorkItemAsync(
        string identifier, string provider, WorkItemStatus status, Guid? id = null, string? error = null)
    {
        await using var db = new PipelineDbContext(_dbOptions);
        var workItem = new WorkItemEntity
        {
            Id = id ?? Guid.NewGuid(),
            TaskType = WorkItemTaskType.Triage,
            IssueIdentifier = identifier,
            IssueProviderConfigId = provider,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = status is WorkItemStatus.Failed or WorkItemStatus.Cancelled or WorkItemStatus.Succeeded
                ? DateTimeOffset.UtcNow
                : null,
            ErrorMessage = error,
        };
        db.WorkItems.Add(workItem);
        await db.SaveChangesAsync();
        return workItem.Id;
    }

    private static TriageRecord Operator(string projectId, string title) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Source = TriageSource.Operator,
        Title = title,
        Request = new TriageRequest { Title = title, WhatHappened = "w", Expected = "e" },
        RequestedBy = "anna",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static TriageRecord Issue(string projectId, string tracker, string identifier) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Source = TriageSource.Issue,
        IssueProviderConfigId = tracker,
        IssueIdentifier = identifier,
        Title = "Issue " + identifier,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static TriageAttempt Attempt(string workItemId) => new()
    {
        WorkItemId = workItemId,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static TriageResult Result(TriageVerdict verdict, int drafts = 0) => new()
    {
        Verdict = verdict,
        Summary = "summary",
        Investigated = [new TriageCheck { Check = "c", Where = "grafana", Result = "r" }],
        Drafts = Enumerable.Range(1, drafts)
            .Select(i => new TriageDraft { Id = $"d{i}", TargetRepository = "api", Title = $"Fix {i}", Body = "body" })
            .ToList(),
    };

    private static TriageResultReport Report(TriageRecord record, string workItemId, TriageVerdict verdict, int drafts = 0) => new()
    {
        WorkItemId = workItemId,
        ProjectId = record.ProjectId,
        Source = record.Source,
        KeyProviderConfigId = TriageEntityMapper.KeyOf(record).ProviderConfigId,
        KeyIdentifier = TriageEntityMapper.KeyOf(record).Identifier,
        StartedAt = DateTimeOffset.UtcNow,
        Result = Result(verdict, drafts),
    };

    private static TriageResultReport TrackerReport(string workItemId, TriageVerdict verdict) => new()
    {
        WorkItemId = workItemId,
        ProjectId = ProjectA,
        Source = TriageSource.Issue,
        KeyProviderConfigId = "tracker-1",
        KeyIdentifier = "431",
        IssueTitle = "Order confirmation spins forever",
        IssueUrl = "https://example.invalid/issues/431",
        StartedAt = DateTimeOffset.UtcNow,
        Result = Result(verdict),
    };

    private static TriageAttemptStart Start(string workItemId) => new()
    {
        WorkItemId = workItemId,
        ProjectId = ProjectA,
        IssueProviderConfigId = "tracker-1",
        IssueIdentifier = "431",
        IssueTitle = "Order confirmation spins forever",
        IssueUrl = "https://example.invalid/issues/431",
        StartedAt = DateTimeOffset.UtcNow,
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>Contexts whose first <c>failures</c> saves (across all contexts) throw, then save normally.</summary>
    private sealed class FailingSaveFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        private readonly Func<Exception> _exception;
        private int _failuresLeft;

        public FailingSaveFactory(DbContextOptions<PipelineDbContext> options, int failures, Func<Exception> exception)
        {
            _options = options;
            _failuresLeft = failures;
            _exception = exception;
        }

        public PipelineDbContext CreateDbContext() => new FailingContext(_options, this);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());

        private Exception? NextFailure() => Interlocked.Decrement(ref _failuresLeft) >= 0 ? _exception() : null;

        private sealed class FailingContext(DbContextOptions<PipelineDbContext> options, FailingSaveFactory factory)
            : PipelineDbContext(options)
        {
            public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
                factory.NextFailure() is { } failure
                    ? Task.FromException<int>(failure)
                    : base.SaveChangesAsync(cancellationToken);
        }
    }
}
