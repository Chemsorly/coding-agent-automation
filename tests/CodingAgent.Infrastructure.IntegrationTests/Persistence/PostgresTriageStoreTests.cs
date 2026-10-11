using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CodingAgent.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// <see cref="PostgresTriageStore"/> against a real PostgreSQL database: the JSONB record, the live
/// "is running" check against WorkItems, ILIKE search, optimistic concurrency, the backfill of attempts that
/// ended without a result, and retention.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresTriageStoreTests : IClassFixture<TriagePostgresFixture>, IAsyncLifetime
{
    private static readonly string ProjectA = Guid.NewGuid().ToString("D");
    private static readonly string ProjectB = Guid.NewGuid().ToString("D");

    private readonly TriagePostgresFixture _fixture;

    public PostgresTriageStoreTests(TriagePostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (_fixture.IsDockerUnavailable || _fixture.InitializationException is not null)
            return;
        await using var db = _fixture.CreateDbContext();
        await db.Triages.ExecuteDeleteAsync();
        await db.WorkItems.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private bool Skip()
    {
        // Skip gracefully when Docker is unavailable; CI runs these in the Docker-enabled integration job.
        if (_fixture.IsDockerUnavailable) return true;
        _fixture.InitializationException.Should().BeNull("the container must start and all migrations must apply");
        return false;
    }

    private PostgresTriageStore Store() => new(_fixture.Factory);

    [Fact]
    public async Task Create_ThenGet_RoundTripsTheRecord()
    {
        if (Skip()) return;
        var record = Operator(ProjectA, "Orders stuck in Pending");

        await Store().CreateAsync(record);
        var back = await Store().GetAsync(record.Id);

        back.Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task RecordResult_OperatorTriage_CompletesItsAttempt_AndReplacesTheDrafts()
    {
        if (Skip()) return;
        var record = Operator(ProjectA, "t") with { Attempts = [Attempt("w1")] };
        await Store().CreateAsync(record);

        var saved = await Store().RecordResultAsync(Report(record, "w1", TriageVerdict.CauseFound, drafts: 2));

        saved.State.Should().Be(TriageState.NeedsReview);
        saved.Attempts.Should().ContainSingle().Which.Result.Should().NotBeNull();
        saved.Attempts[0].Outcome.Should().Be(TriageAttemptOutcome.Completed);
        saved.Drafts.Should().HaveCount(2);
        saved.DraftsFromWorkItemId.Should().Be("w1");
        (await Store().GetAsync(record.Id))!.Attempts[0].Result.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordResult_OperatorTriageWithoutRow_Throws()
    {
        if (Skip()) return;
        var record = Operator(ProjectA, "t");

        var act = () => Store().RecordResultAsync(Report(record, "w1", TriageVerdict.CauseFound));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RecordResult_TrackerIssue_FirstResultCreatesTheTriage_SecondAddsAnAttempt()
    {
        if (Skip()) return;
        var report = new TriageResultReport
        {
            WorkItemId = "w1",
            ProjectId = ProjectA,
            Source = TriageSource.Issue,
            KeyProviderConfigId = "tracker-1",
            KeyIdentifier = "431",
            IssueTitle = "Order confirmation spins forever",
            IssueUrl = "https://example.invalid/issues/431",
            StartedAt = DateTimeOffset.UtcNow,
            Result = Result(TriageVerdict.Inconclusive),
        };

        var first = await Store().RecordResultAsync(report);
        var second = await Store().RecordResultAsync(report with { WorkItemId = "w2", Result = Result(TriageVerdict.CauseFound) });

        first.Title.Should().Be("Order confirmation spins forever");
        first.State.Should().Be(TriageState.NeedsInput);
        second.Id.Should().Be(first.Id);
        second.Attempts.Select(a => a.WorkItemId).Should().Equal("w1", "w2");
        second.State.Should().Be(TriageState.NeedsReview);
        (await Store().GetByIssueAsync("tracker-1", "431"))!.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task Create_SameTrackerIssueTwice_IsRefused()
    {
        if (Skip()) return;
        var a = Issue(ProjectA, "tracker-1", "7");
        var b = Issue(ProjectA, "tracker-1", "7");
        await Store().CreateAsync(a);

        var act = () => Store().CreateAsync(b);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task List_ResolvesRunningFromWorkItems_AndFiltersByTabProjectSourceAndSearch()
    {
        if (Skip()) return;
        var running = Operator(ProjectA, "Checkout returns 502") with { Attempts = [Attempt("00000000-0000-0000-0000-0000000000a1")] };
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
        var search = await Store().ListAsync(new TriageListQuery { ProjectId = ProjectA, Search = "pEnDiNg" });
        var everything = await Store().ListAsync(new TriageListQuery());

        all.Total.Should().Be(3);
        all.Items.Single(i => i.Id == running.Id).Status.Should().Be(TriageStatus.Investigating);
        needYou.Items.Select(i => i.Id).Should().Equal(review.Id);
        investigating.Items.Select(i => i.Id).Should().Equal(running.Id);
        issues.Items.Should().ContainSingle().Which.IssueIdentifier.Should().Be("431");
        search.Items.Select(i => i.Id).Should().Equal(review.Id);
        everything.Total.Should().Be(4);
    }

    [Fact]
    public async Task List_SearchEscapesLikeWildcards()
    {
        if (Skip()) return;
        await Store().CreateAsync(Operator(ProjectA, "100% CPU"));
        await Store().CreateAsync(Operator(ProjectA, "1000 CPUs"));

        var result = await Store().ListAsync(new TriageListQuery { ProjectId = ProjectA, Search = "100%" });

        result.Items.Should().ContainSingle().Which.Title.Should().Be("100% CPU");
    }

    [Fact]
    public async Task GetActiveWorkItemId_ReturnsOnlyARunningWorkItem()
    {
        if (Skip()) return;
        var record = Operator(ProjectA, "t");
        await Store().CreateAsync(record);
        var key = TriageConstants.IssueIdentifierFor(record.Id);
        await AddWorkItemAsync(key, TriageConstants.ProviderConfigId, WorkItemStatus.Failed);

        (await Store().GetActiveWorkItemIdAsync(record)).Should().BeNull();

        var activeId = await AddWorkItemAsync(key, TriageConstants.ProviderConfigId, WorkItemStatus.Pending);
        (await Store().GetActiveWorkItemIdAsync(record)).Should().Be(activeId.ToString());
    }

    [Fact]
    public async Task Update_ConcurrentWriters_BothChangesSurvive()
    {
        if (Skip()) return;
        var record = Operator(ProjectA, "t");
        await Store().CreateAsync(record);

        await Task.WhenAll(Enumerable.Range(1, 6).Select(i => Task.Run(() =>
            Store().UpdateAsync(record.Id, r => r with { Attempts = [.. r.Attempts, Attempt($"w{i}")] }))));

        (await Store().GetAsync(record.Id))!.Attempts.Should().HaveCount(6);
    }

    [Fact]
    public async Task Update_MissingTriage_ReturnsNull()
    {
        if (Skip()) return;
        (await Store().UpdateAsync(Guid.NewGuid(), r => r)).Should().BeNull();
    }

    [Fact]
    public async Task Backfill_CopiesHowRunsEnded_AndLeavesRunningOnesAlone()
    {
        if (Skip()) return;
        var failedId = Guid.NewGuid();
        var cancelledId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        var failed = Operator(ProjectA, "failed") with { Attempts = [Attempt(failedId.ToString())] };
        var cancelled = Operator(ProjectA, "cancelled") with { Attempts = [Attempt(cancelledId.ToString())] };
        var gone = Operator(ProjectA, "gone") with { Attempts = [Attempt(Guid.NewGuid().ToString())] };
        var running = Operator(ProjectA, "running") with { Attempts = [Attempt(runningId.ToString())] };
        foreach (var r in new[] { failed, cancelled, gone, running })
            await Store().CreateAsync(r);
        await AddWorkItemAsync("a", "x", WorkItemStatus.Failed, failedId, error: "Agent timeout after 30 min");
        await AddWorkItemAsync("b", "x", WorkItemStatus.Cancelled, cancelledId);
        await AddWorkItemAsync("c", "x", WorkItemStatus.Running, runningId);

        var changed = await Store().BackfillEndedAttemptsAsync();

        changed.Should().Be(3);
        var f = (await Store().GetAsync(failed.Id))!.Attempts.Single();
        f.Outcome.Should().Be(TriageAttemptOutcome.Failed);
        f.FailureReason.Should().Be("Agent timeout after 30 min");
        (await Store().GetAsync(cancelled.Id))!.Attempts.Single().Outcome.Should().Be(TriageAttemptOutcome.Cancelled);
        (await Store().GetAsync(gone.Id))!.Attempts.Single().Outcome.Should().Be(TriageAttemptOutcome.Failed);
        (await Store().GetAsync(running.Id))!.Attempts.Single().Outcome.Should().BeNull();
        (await Store().BackfillEndedAttemptsAsync()).Should().Be(0, "only the running attempt is still open");
    }

    [Fact]
    public async Task DeleteExpired_KeepsRecentAndRunningTriages()
    {
        if (Skip()) return;
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        var expired = Operator(ProjectA, "expired") with { UpdatedAt = old };
        var oldButRunning = Operator(ProjectA, "running") with { UpdatedAt = old };
        var recent = Operator(ProjectA, "recent");
        foreach (var r in new[] { expired, oldButRunning, recent })
            await Store().CreateAsync(r);
        await AddWorkItemAsync(TriageConstants.IssueIdentifierFor(oldButRunning.Id), TriageConstants.ProviderConfigId, WorkItemStatus.Dispatched);

        var deleted = await Store().DeleteExpiredAsync(DateTimeOffset.UtcNow.AddDays(-30));

        deleted.Should().Be(1);
        (await Store().GetAsync(expired.Id)).Should().BeNull();
        (await Store().GetAsync(oldButRunning.Id)).Should().NotBeNull();
        (await Store().GetAsync(recent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task ListRecent_ReturnsTheProjectsNewestTriages_WithoutTheExcludedOne()
    {
        if (Skip()) return;
        var a = Operator(ProjectA, "a") with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var b = Operator(ProjectA, "b");
        var tooOld = Operator(ProjectA, "old") with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-60) };
        var other = Operator(ProjectB, "other");
        foreach (var r in new[] { a, b, tooOld, other })
            await Store().CreateAsync(r);

        var recent = await Store().ListRecentAsync(ProjectA, DateTimeOffset.UtcNow.AddDays(-30), 10, excludeId: b.Id);

        recent.Select(r => r.Id).Should().Equal(a.Id);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> AddWorkItemAsync(
        string identifier, string provider, WorkItemStatus status, Guid? id = null, string? error = null)
    {
        await using var db = _fixture.CreateDbContext();
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
}

/// <summary>Starts one PostgreSQL container for the triage store tests and applies all migrations.</summary>
public sealed class TriagePostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public Exception? InitializationException { get; private set; }

    public bool IsDockerUnavailable => _container is null;

    public IDbContextFactory<PipelineDbContext> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("triage_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();

            await _container.StartAsync();
            Factory = new ContextFactory(_container.GetConnectionString());

            await using var db = CreateDbContext();
            await db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            InitializationException = ex;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    public PipelineDbContext CreateDbContext() => Factory.CreateDbContext();

    /// <summary>
    /// Configured like the API's factory, retrying execution strategy included: that strategy refuses
    /// transactions it does not run itself, which a factory without it would hide.
    /// </summary>
    private sealed class ContextFactory(string connectionString) : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<PipelineDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null))
                .Options);
    }
}
