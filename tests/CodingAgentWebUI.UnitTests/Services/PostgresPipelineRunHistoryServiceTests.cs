using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="PostgresPipelineRunHistoryService"/>.
/// Uses in-memory EF Core provider for isolation.
/// </summary>
public sealed class PostgresPipelineRunHistoryServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly PostgresPipelineRunHistoryService _sut;

    public PostgresPipelineRunHistoryServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"RunHistoryTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _sut = new PostgresPipelineRunHistoryService(_dbFactory, new Mock<ILogger>().Object);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task AddRunToHistory_PersistsToDatabase()
    {
        var runId = Guid.NewGuid().ToString();
        var run = CreateCompletedRun(runId, "owner/repo#1", "Fix bug");

        await _sut.AddRunToHistoryAsync(run);

        using var db = new InMemoryPipelineDbContext(_dbOptions);
        var entities = db.PipelineRuns.ToList();
        entities.Should().HaveCount(1);
        entities[0].IssueIdentifier.Should().Be("owner/repo#1");
        entities[0].IssueTitle.Should().Be("Fix bug");
        entities[0].FinalStep.Should().Be(PipelineStep.Completed);
        entities[0].SummaryJson.Should().NotBeNullOrEmpty();
    }

    // Ordering (newest-first) and empty-history tests moved to shared contract:
    // PipelineRunHistoryServiceContractTests.GetHistory_ReturnsNewestFirst
    // PipelineRunHistoryServiceContractTests.EmptyHistory_ReturnsEmptyList

    [Fact]
    public async Task AddRunToHistory_Upsert_UpdatesExistingRow_IncludingIssueProviderConfigId()
    {
        var runId = Guid.NewGuid();

        // Pre-insert a row (simulating dispatch-time creation)
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#5",
                IssueTitle = "",
                FinalStep = PipelineStep.Created,
                StartedAt = DateTimeOffset.UtcNow,
                RunType = PipelineRunType.Implementation
            });
            db.SaveChanges();
        }

        // Complete the run — AddRunToHistoryAsync should upsert
        var run = CreateCompletedRun(runId.ToString(), "owner/repo#5", "Updated title");
        await _sut.AddRunToHistoryAsync(run);

        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            var entities = db.PipelineRuns.ToList();
            entities.Should().HaveCount(1); // no duplicate
            entities[0].IssueTitle.Should().Be("Updated title");
            entities[0].FinalStep.Should().Be(PipelineStep.Completed);
            entities[0].SummaryJson.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task GetRunHistory_DeserializesFullSummary_WithAllFields()
    {
        var runId = Guid.NewGuid().ToString();
        var run = CreateCompletedRun(runId, "issue-full", "Full fields",
            agentId: "agent-full", modelName: "gpt-4o");
        run.PullRequestUrl = "https://github.com/org/repo/pull/42";
        run.TotalTokens = 50000;

        await _sut.AddRunToHistoryAsync(run);

        var history = await _sut.GetRunHistoryAsync();
        history.Should().HaveCount(1);

        var restored = history[0];
        restored.RunId.Should().Be(runId);
        restored.IssueIdentifier.Should().Be("issue-full");
        restored.IssueTitle.Should().Be("Full fields");
        restored.AgentId.Should().Be("agent-full");
        restored.ModelName.Should().Be("gpt-4o");
        restored.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/42");
        restored.TotalTokens.Should().Be(50000);
        restored.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task GetRunHistory_FallsBackToColumns_WhenSummaryJsonIsNull()
    {
        // Insert a row without SummaryJson (legacy data)
        var runId = Guid.NewGuid();
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "legacy-issue",
                IssueTitle = "Legacy run",
                FinalStep = PipelineStep.Failed,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                CompletedAt = DateTimeOffset.UtcNow,
                AgentId = "agent-legacy",
                ProjectId = "proj-legacy",
                RunType = PipelineRunType.Review,
                SummaryJson = null // no JSON
            });
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(1);
        var restored = history[0];
        restored.RunId.Should().Be(runId.ToString());
        restored.IssueIdentifier.Should().Be("legacy-issue");
        restored.IssueTitle.Should().Be("Legacy run");
        restored.FinalStep.Should().Be(PipelineStep.Failed);
        restored.AgentId.Should().Be("agent-legacy");
        restored.RunType.Should().Be(PipelineRunType.Review);
        restored.ProjectId.Should().Be("proj-legacy"); // fallback path must preserve ProjectId
    }

    [Fact]
    public async Task GetRunHistory_CorruptSummaryJson_FallsBackToColumns_RowIsReturned()
    {
        // Arrange: insert a row with corrupt SummaryJson — simulates a row whose JSON was
        // corrupted in the DB (truncation, encoding issue, manual edit).
        var runId = Guid.NewGuid();
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "org/repo#99",
                IssueTitle = "Corrupt JSON run",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
                CompletedAt = DateTimeOffset.UtcNow.AddHours(-1),
                AgentId = "agent-corrupt",
                ProjectId = "proj-corrupt",
                RunType = PipelineRunType.Implementation,
                SummaryJson = "{ CORRUPT JSON !!!" // unparseable
            });
            db.SaveChanges();
        }

        // Act
        var history = await _sut.GetRunHistoryAsync();

        // Assert: row is returned (not dropped), fallback path fired
        history.Should().HaveCount(1);
        var restored = history[0];
        restored.RunId.Should().Be(runId.ToString());
        restored.IssueIdentifier.Should().Be("org/repo#99");
        restored.IssueTitle.Should().Be("Corrupt JSON run");
        restored.FinalStep.Should().Be(PipelineStep.Completed);
        restored.AgentId.Should().Be("agent-corrupt");
        restored.ProjectId.Should().Be("proj-corrupt");
        restored.RunType.Should().Be(PipelineRunType.Implementation);
        // InitiatedBy must default to "manual" (not "consolidation") so the row passes the
        // consolidation filter and is visible in user-facing history.
        restored.InitiatedBy.Should().Be("manual");
    }

    [Fact]
    public async Task GetRunHistory_CorruptSummaryJson_ConsolidationGhostRow_StillExcluded()
    {
        // Arrange: a ghost consolidation row with corrupt SummaryJson (bypassed write guard,
        // or pre-guard legacy data). The fallback path must read IssueProviderConfigId from the
        // entity column and set InitiatedBy="consolidation" so the read-time filter excludes it.
        var runId = Guid.NewGuid();
        var normalId = Guid.NewGuid();
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            // Normal run with valid SummaryJson containing InitiatedBy="manual"
            var normalSummary = new PipelineRunSummary
            {
                RunId = normalId.ToString(),
                IssueIdentifier = "org/repo#1",
                IssueTitle = "Normal run",
                FinalStep = PipelineStep.Completed,
                StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-3),
                InitiatedBy = "manual"
            };
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = normalId,
                IssueIdentifier = normalSummary.IssueIdentifier,
                IssueTitle = normalSummary.IssueTitle,
                FinalStep = normalSummary.FinalStep,
                StartedAt = normalSummary.StartedAtOffset,
                SummaryJson = System.Text.Json.JsonSerializer.Serialize(normalSummary, PipelineJsonOptions.Default)
            });
            // Consolidation ghost row with corrupt SummaryJson — IssueProviderConfigId column
            // set to the consolidation sentinel so the fallback path can detect it.
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "consolidation-ghost",
                IssueTitle = "Ghost",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                SummaryJson = "{ CORRUPT JSON !!!"
            });
            db.SaveChanges();
        }

        // Act
        var history = await _sut.GetRunHistoryAsync();

        // The normal run must always appear
        history.Should().Contain(s => s.IssueIdentifier == "org/repo#1");
        // The consolidation ghost row must be excluded: the fallback path reads IssueProviderConfigId
        // from the entity column and sets InitiatedBy="consolidation", causing the read-time filter
        // to exclude it from user-facing history.
        history.Should().NotContain(s => s.IssueIdentifier == "consolidation-ghost",
            "consolidation ghost rows with corrupt SummaryJson must be excluded via the column-level IssueProviderConfigId discriminant");
    }

    [Fact]
    public async Task GetRunHistoryPaged_CorruptSummaryJson_ConsolidationGhostRow_StillExcluded()
    {
        // Regression test for the paged path (GetRunHistoryPagedInternalAsync).
        // The paged path has its own batching loop and .Where(...) filter — a distinct code path
        // from the unpaged overload. This test verifies that a consolidation ghost row with
        // corrupt SummaryJson is excluded from GetRunHistoryAsync(page, pageSize) results.
        var ghostId = Guid.NewGuid();
        var normalId = Guid.NewGuid();
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            // Normal run — valid SummaryJson, InitiatedBy="manual".
            var normalSummary = new PipelineRunSummary
            {
                RunId = normalId.ToString(),
                IssueIdentifier = "org/repo#paged-1",
                IssueTitle = "Normal paged run",
                FinalStep = PipelineStep.Completed,
                StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-3),
                InitiatedBy = "manual"
            };
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = normalId,
                IssueIdentifier = normalSummary.IssueIdentifier,
                IssueTitle = normalSummary.IssueTitle,
                FinalStep = normalSummary.FinalStep,
                StartedAt = normalSummary.StartedAtOffset,
                SummaryJson = System.Text.Json.JsonSerializer.Serialize(normalSummary, PipelineJsonOptions.Default)
            });
            // Consolidation ghost row with corrupt SummaryJson and the consolidation sentinel
            // as IssueProviderConfigId. The fallback path must detect this via the column and
            // set InitiatedBy="consolidation" so the paged filter excludes it.
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = ghostId,
                IssueIdentifier = "consolidation-ghost-paged",
                IssueTitle = "Ghost paged",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                SummaryJson = "{ CORRUPT JSON !!!"
            });
            db.SaveChanges();
        }

        // Act: use the paged overload — exercises GetRunHistoryPagedInternalAsync
        var result = await _sut.GetRunHistoryAsync(page: 1, pageSize: 10);

        // Assert: only the normal run appears; ghost is excluded by the paged filter
        result.Items.Should().HaveCount(1,
            "the paged filter must exclude consolidation ghost rows with corrupt SummaryJson");
        result.Items.Should().Contain(s => s.IssueIdentifier == "org/repo#paged-1");
        result.Items.Should().NotContain(s => s.IssueIdentifier == "consolidation-ghost-paged",
            "consolidation ghost rows with corrupt SummaryJson must be excluded from GetRunHistoryPaged results");
        result.HasMore.Should().BeFalse();
    }

    // Max history size test moved to shared contract:
    // PipelineRunHistoryServiceContractTests.MaxHistorySize_OldestEvicted

    // ── Consolidation filtering tests ───────────────────────────────────

    [Fact]
    public async Task GetRunHistory_ExcludesConsolidationRuns()
    {
        // Arrange: persist a normal run and a consolidation ghost entry
        var normalRun = CreateCompletedRun(Guid.NewGuid().ToString(), "org/repo#1", "Normal run");
        var consolidationRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = Guid.NewGuid().ToString(),
            IssueTitle = Guid.NewGuid().ToString(),
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1",
            InitiatedBy = ConsolidationConstants.InitiatedBy
        });
        consolidationRun.CurrentStep = PipelineStep.Completed;
        consolidationRun.MarkCompleted();

        // Persist both directly to DB (bypassing the guard to simulate pre-existing ghost entries)
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            var normalSummary = normalRun.ToSummary();
            var consolSummary = consolidationRun.ToSummary();
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.Parse(normalRun.RunId),
                IssueIdentifier = normalSummary.IssueIdentifier,
                IssueTitle = normalSummary.IssueTitle,
                FinalStep = normalSummary.FinalStep,
                StartedAt = normalSummary.StartedAtOffset,
                SummaryJson = System.Text.Json.JsonSerializer.Serialize(normalSummary, PipelineJsonOptions.Default)
            });
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.Parse(consolidationRun.RunId),
                IssueIdentifier = consolSummary.IssueIdentifier,
                IssueTitle = consolSummary.IssueTitle,
                FinalStep = consolSummary.FinalStep,
                StartedAt = consolSummary.StartedAtOffset,
                SummaryJson = System.Text.Json.JsonSerializer.Serialize(consolSummary, PipelineJsonOptions.Default)
            });
            db.SaveChanges();
        }

        // Act
        var history = await _sut.GetRunHistoryAsync();

        // Assert: only the normal run should appear
        history.Should().HaveCount(1);
        history[0].IssueIdentifier.Should().Be("org/repo#1");
    }

    [Fact]
    public async Task GetRunHistoryAsync_ExcludesConsolidationRuns()
    {
        var normalRun = CreateCompletedRun(Guid.NewGuid().ToString(), "org/repo#2", "Async normal");
        var consolidationRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = Guid.NewGuid().ToString(),
            IssueTitle = Guid.NewGuid().ToString(),
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1",
            InitiatedBy = ConsolidationConstants.InitiatedBy
        });
        consolidationRun.CurrentStep = PipelineStep.Completed;
        consolidationRun.MarkCompleted();

        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            var normalSummary = normalRun.ToSummary();
            var consolSummary = consolidationRun.ToSummary();
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.Parse(normalRun.RunId),
                IssueIdentifier = normalSummary.IssueIdentifier,
                IssueTitle = normalSummary.IssueTitle,
                FinalStep = normalSummary.FinalStep,
                StartedAt = normalSummary.StartedAtOffset,
                SummaryJson = System.Text.Json.JsonSerializer.Serialize(normalSummary, PipelineJsonOptions.Default)
            });
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.Parse(consolidationRun.RunId),
                IssueIdentifier = consolSummary.IssueIdentifier,
                IssueTitle = consolSummary.IssueTitle,
                FinalStep = consolSummary.FinalStep,
                StartedAt = consolSummary.StartedAtOffset,
                SummaryJson = System.Text.Json.JsonSerializer.Serialize(consolSummary, PipelineJsonOptions.Default)
            });
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(1);
        history[0].IssueIdentifier.Should().Be("org/repo#2");
    }

    [Fact]
    public async Task AddRunToHistory_RejectsConsolidationRun_Silently()
    {
        var consolidationRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = Guid.NewGuid().ToString(),
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1",
            InitiatedBy = ConsolidationConstants.InitiatedBy
        });
        consolidationRun.CurrentStep = PipelineStep.Completed;
        consolidationRun.MarkCompleted();

        // Should not throw
        await _sut.AddRunToHistoryAsync(consolidationRun);

        // Should not persist to DB
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.PipelineRuns.Should().BeEmpty();
    }

    // ── Terminal Step Guard ──────────────────────────────────────────────

    [Fact]
    public async Task AddRunToHistoryAsync_NonTerminalStep_ForcedToFailed()
    {
        // Arrange: run with non-terminal step (should never happen, but defense-in-depth catches it)
        var runId = Guid.NewGuid().ToString();
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#99",
            IssueTitle = "Bug fix",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        });
        run.CurrentStep = PipelineStep.RunningQualityGates;
        run.MarkCompleted();

        // Act
        await _sut.AddRunToHistoryAsync(run);

        // Assert: persisted with FinalStep corrected to Failed
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        var entity = db.PipelineRuns.Single();
        entity.FinalStep.Should().Be(PipelineStep.Failed);

        // Assert: caller's reference is NOT mutated
        run.CurrentStep.Should().Be(PipelineStep.RunningQualityGates,
            "AddRunToHistoryAsync must not mutate the caller's PipelineRun.CurrentStep");
    }

    [Fact]
    public async Task AddRunToHistoryAsync_NonTerminalStep_DoesNotMutateCallerReference()
    {
        // Arrange: run with non-terminal step — verifies the caller's object is not modified
        var runId = Guid.NewGuid().ToString();
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#102",
            IssueTitle = "Mutation test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        });
        run.CurrentStep = PipelineStep.RunningQualityGates;
        run.MarkCompleted();

        // Act
        await _sut.AddRunToHistoryAsync(run);

        // Assert: caller's reference is unchanged
        run.CurrentStep.Should().Be(PipelineStep.RunningQualityGates,
            "AddRunToHistoryAsync must not mutate the caller's PipelineRun.CurrentStep");

        // Assert: DB still gets Failed
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        var entity = db.PipelineRuns.Single();
        entity.FinalStep.Should().Be(PipelineStep.Failed);
    }

    [Fact]
    public async Task AddRunToHistoryAsync_TerminalStep_NotMutated()
    {
        // Arrange: run with terminal step (normal flow)
        var runId = Guid.NewGuid().ToString();
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#100",
            IssueTitle = "Feature",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        });
        run.CurrentStep = PipelineStep.Completed;
        run.MarkCompleted();

        // Act
        await _sut.AddRunToHistoryAsync(run);

        // Assert: persisted with FinalStep unchanged
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        var entity = db.PipelineRuns.Single();
        entity.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task AddRunToHistory_NonTerminalStep_ForcedToFailed()
    {
        // Arrange: non-terminal step via sync (obsolete) method
        var runId = Guid.NewGuid().ToString();
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#101",
            IssueTitle = "Sync test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        });
        run.CurrentStep = PipelineStep.ReviewingCode;
        run.MarkCompleted();

        // Act
        await _sut.AddRunToHistoryAsync(run);

        // Assert: persisted with FinalStep corrected to Failed
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        var entity = db.PipelineRuns.Single();
        entity.FinalStep.Should().Be(PipelineStep.Failed);

        // Assert: caller's reference is NOT mutated
        run.CurrentStep.Should().Be(PipelineStep.ReviewingCode,
            "AddRunToHistoryAsync must not mutate the caller's PipelineRun.CurrentStep");
    }

    // ── Pagination tests ──────────────────────────────────────────────────

    [Fact]
    public async Task GetRunHistoryPaged_InvalidArgs_ThrowArgumentOutOfRange()
    {
        await ((Func<Task>)(() => _sut.GetRunHistoryAsync(page: 0, pageSize: 10)))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await ((Func<Task>)(() => _sut.GetRunHistoryAsync(page: 1, pageSize: 0)))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetRunHistoryPaged_ReturnsFirstPage_WithCorrectItems()
    {
        // Arrange: seed 5 runs with distinct timestamps
        for (var i = 0; i < 5; i++)
        {
            var run = CreateCompletedRun(
                Guid.NewGuid().ToString(),
                $"org/repo#{i + 1}",
                $"Run {i + 1}",
                startedAt: DateTimeOffset.UtcNow.AddMinutes(-i));
            await _sut.AddRunToHistoryAsync(run);
        }

        // Act: request page 1, pageSize 3
        var result = await _sut.GetRunHistoryAsync(page: 1, pageSize: 3);

        // Assert
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(3);
        result.Items.Should().HaveCount(3);
        result.HasMore.Should().BeTrue();
        // Newest first (StartedAt descending)
        result.Items[0].IssueIdentifier.Should().Be("org/repo#1");
        result.Items[1].IssueIdentifier.Should().Be("org/repo#2");
        result.Items[2].IssueIdentifier.Should().Be("org/repo#3");
    }

    [Fact]
    public async Task GetRunHistoryPaged_ReturnsSecondPage_WithCorrectSkip()
    {
        // Arrange: seed 5 runs
        for (var i = 0; i < 5; i++)
        {
            var run = CreateCompletedRun(
                Guid.NewGuid().ToString(),
                $"org/repo#{i + 1}",
                $"Run {i + 1}",
                startedAt: DateTimeOffset.UtcNow.AddMinutes(-i));
            await _sut.AddRunToHistoryAsync(run);
        }

        // Act: request page 2, pageSize 3
        var result = await _sut.GetRunHistoryAsync(page: 2, pageSize: 3);

        // Assert: skips first 3, returns remaining 2
        result.Page.Should().Be(2);
        result.PageSize.Should().Be(3);
        result.Items.Should().HaveCount(2);
        result.HasMore.Should().BeFalse();
        result.Items[0].IssueIdentifier.Should().Be("org/repo#4");
        result.Items[1].IssueIdentifier.Should().Be("org/repo#5");
    }

    [Fact]
    public async Task GetRunHistoryPaged_HasMoreFalse_WhenExactlyPageSizeItems()
    {
        // Arrange: seed exactly 3 runs
        for (var i = 0; i < 3; i++)
        {
            var run = CreateCompletedRun(
                Guid.NewGuid().ToString(),
                $"org/repo#{i + 1}",
                $"Run {i + 1}",
                startedAt: DateTimeOffset.UtcNow.AddMinutes(-i));
            await _sut.AddRunToHistoryAsync(run);
        }

        // Act: request page 1, pageSize 3
        var result = await _sut.GetRunHistoryAsync(page: 1, pageSize: 3);

        // Assert: exactly pageSize items, no more beyond
        result.Items.Should().HaveCount(3);
        result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GetRunHistoryPaged_EmptyPage_WhenBeyondData()
    {
        // Arrange: seed 2 runs
        for (var i = 0; i < 2; i++)
        {
            var run = CreateCompletedRun(
                Guid.NewGuid().ToString(),
                $"org/repo#{i + 1}",
                $"Run {i + 1}",
                startedAt: DateTimeOffset.UtcNow.AddMinutes(-i));
            await _sut.AddRunToHistoryAsync(run);
        }

        // Act: request page 2 (beyond data with pageSize=3)
        var result = await _sut.GetRunHistoryAsync(page: 2, pageSize: 3);

        // Assert: no items, no more
        result.Items.Should().BeEmpty();
        result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GetRunHistoryPaged_FiltersConsolidationRuns_MaintainsCorrectPageSize()
    {
        // Arrange: seed 4 normal runs + 2 consolidation ghost entries in between
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            for (var i = 0; i < 6; i++)
            {
                var isConsolidation = (i == 2 || i == 4); // Ghost entries at positions 2 and 4
                var summary = new PipelineRunSummary
                {
                    RunId = Guid.NewGuid().ToString(),
                    IssueIdentifier = isConsolidation ? $"consolidation-{i}" : $"org/repo#{i}",
                    IssueTitle = isConsolidation ? "Consolidation" : $"Run {i}",
                    FinalStep = PipelineStep.Completed,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-i),
                    InitiatedBy = isConsolidation ? ConsolidationConstants.InitiatedBy : "manual"
                };
                db.PipelineRuns.Add(new PipelineRunEntity
                {
                    RunId = Guid.Parse(summary.RunId),
                    IssueIdentifier = summary.IssueIdentifier,
                    IssueTitle = summary.IssueTitle,
                    FinalStep = summary.FinalStep,
                    StartedAt = summary.StartedAtOffset,
                    SummaryJson = System.Text.Json.JsonSerializer.Serialize(summary, PipelineJsonOptions.Default)
                });
            }
            db.SaveChanges();
        }

        // Act: request page 1, pageSize 3 — should get 3 valid items despite consolidation rows
        var result = await _sut.GetRunHistoryAsync(page: 1, pageSize: 3);

        // Assert: 3 non-consolidation items returned, HasMore true (4th valid item exists)
        result.Items.Should().HaveCount(3);
        result.HasMore.Should().BeTrue();
        result.Items.Should().NotContain(s => s.InitiatedBy == ConsolidationConstants.InitiatedBy);
    }

    // ── TryDeleteWorkspace tests ──────────────────────────────────────────

    [Fact]
    public void TryDeleteWorkspace_NullPath_DoesNothing()
    {
        // No exception should be thrown for null/empty path
        _sut.TryDeleteWorkspace(null, "run-1", Path.GetTempPath());
        _sut.TryDeleteWorkspace("", "run-1", Path.GetTempPath());
        _sut.TryDeleteWorkspace("  ", "run-1", Path.GetTempPath());
    }

    [Fact]
    public void TryDeleteWorkspace_NonExistentPath_DoesNothing()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        // Should not throw even for a path that doesn't exist
        _sut.TryDeleteWorkspace(nonExistent, "run-1", Path.GetTempPath());
    }

    [Fact]
    public void TryDeleteWorkspace_PathOutsideBase_DoesNothing()
    {
        // Create a real temp directory
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(baseDir);
        try
        {
            // Provide a path that is outside the base
            var outsidePath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            _sut.TryDeleteWorkspace(outsidePath, "run-1", baseDir);
            // Directory should still exist (not deleted)
            Directory.Exists(outsidePath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void TryDeleteWorkspace_ValidSubdir_DeletesDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var runId = Guid.NewGuid().ToString();
        var workspacePath = Path.Combine(baseDir, runId);
        Directory.CreateDirectory(workspacePath);
        try
        {
            _sut.TryDeleteWorkspace(workspacePath, runId, baseDir);
            Directory.Exists(workspacePath).Should().BeFalse("valid workspace should be deleted");
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
    }

    // ── CleanupExpiredWorkspaces tests ────────────────────────────────────

    [Fact]
    public void CleanupExpiredWorkspaces_NegativeRetention_DoesNothing()
    {
        var config = new PipelineConfiguration { FailedWorkspaceRetentionDays = -1, WorkspaceBaseDirectory = Path.GetTempPath() };
        // Should return immediately without doing anything
        _sut.CleanupExpiredWorkspaces(config);
    }

    [Fact]
    public void CleanupExpiredWorkspaces_NoExpiredRuns_DoesNothing()
    {
        // Seed a run that is NOT expired (completed recently)
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.NewGuid(),
                IssueIdentifier = "org/repo#1",
                IssueTitle = "Recent",
                FinalStep = PipelineStep.Failed,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow // Just completed, not expired
            });
            db.SaveChanges();
        }

        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(baseDir);
        try
        {
            var config = new PipelineConfiguration { FailedWorkspaceRetentionDays = 7, WorkspaceBaseDirectory = baseDir };
            _sut.CleanupExpiredWorkspaces(config); // Should not throw
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void CleanupExpiredWorkspaces_ExpiredRun_DeletesWorkspace()
    {
        var runId = Guid.NewGuid();
        // Seed an expired failed run
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "org/repo#expired",
                IssueTitle = "Expired",
                FinalStep = PipelineStep.Failed,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-10),
                CompletedAt = DateTimeOffset.UtcNow.AddDays(-10)
            });
            db.SaveChanges();
        }

        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var workspacePath = Path.Combine(baseDir, runId.ToString());
        Directory.CreateDirectory(workspacePath);
        try
        {
            var config = new PipelineConfiguration { FailedWorkspaceRetentionDays = 7, WorkspaceBaseDirectory = baseDir };
            _sut.CleanupExpiredWorkspaces(config);
            Directory.Exists(workspacePath).Should().BeFalse("expired workspace should be deleted");
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void CleanupExpiredWorkspaces_ActiveRunIdExcluded_SkipsActiveRun()
    {
        var activeRunId = Guid.NewGuid();
        var expiredRunId = Guid.NewGuid();
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = activeRunId,
                IssueIdentifier = "org/repo#active",
                IssueTitle = "Active",
                FinalStep = PipelineStep.Failed,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-10),
                CompletedAt = DateTimeOffset.UtcNow.AddDays(-10)
            });
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = expiredRunId,
                IssueIdentifier = "org/repo#expired",
                IssueTitle = "Expired",
                FinalStep = PipelineStep.Failed,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-10),
                CompletedAt = DateTimeOffset.UtcNow.AddDays(-10)
            });
            db.SaveChanges();
        }

        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var activeWorkspace = Path.Combine(baseDir, activeRunId.ToString());
        var expiredWorkspace = Path.Combine(baseDir, expiredRunId.ToString());
        Directory.CreateDirectory(activeWorkspace);
        Directory.CreateDirectory(expiredWorkspace);
        try
        {
            var config = new PipelineConfiguration { FailedWorkspaceRetentionDays = 7, WorkspaceBaseDirectory = baseDir };
            _sut.CleanupExpiredWorkspaces(config, activeRunId: activeRunId.ToString());

            Directory.Exists(activeWorkspace).Should().BeTrue("active run workspace must not be deleted");
            Directory.Exists(expiredWorkspace).Should().BeFalse("expired run workspace should be deleted");
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
    }

    // ── DeserializeSummary fallback coverage ──────────────────────────────

    [Fact]
    public async Task GetRunHistory_NullSummaryJson_FallsBackToColumns_IssueProviderConfigIdPreserved()
    {
        // Verifies that when SummaryJson is null, the fallback path copies IssueProviderConfigId from the column
        var runId = Guid.NewGuid();
        var providerConfigId = "my-provider-config";
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "org/repo#fallback",
                IssueTitle = "Fallback",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow,
                IssueProviderConfigId = providerConfigId,
                SummaryJson = null
            });
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(1);
        history[0].IssueProviderConfigId.Should().Be(providerConfigId);
        history[0].InitiatedBy.Should().Be("manual");
    }

    [Fact]
    public async Task GetRunHistory_Upsert_CopiesIssueProviderConfigId_ToExistingRow()
    {
        // Verifies that AddRunToHistoryAsync copies IssueProviderConfigId when upserting
        // (fixes the gap where rows pre-created at dispatch time had null IssueProviderConfigId)
        var runId = Guid.NewGuid();
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "org/repo#upsert",
                IssueTitle = "",
                FinalStep = PipelineStep.Created,
                StartedAt = DateTimeOffset.UtcNow,
                IssueProviderConfigId = null // Pre-created without IssueProviderConfigId
            });
            db.SaveChanges();
        }

        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId.ToString(),
            IssueIdentifier = "org/repo#upsert",
            IssueTitle = "Upsert test",
            IssueProviderConfigId = "my-provider",
            RepoProviderConfigId = "rp-1"
        });
        run.CurrentStep = PipelineStep.Completed;
        run.MarkCompleted();

        await _sut.AddRunToHistoryAsync(run);

        using var verifyDb = new InMemoryPipelineDbContext(_dbOptions);
        var entity = verifyDb.PipelineRuns.Single();
        entity.IssueProviderConfigId.Should().Be("my-provider",
            "upsert path must copy IssueProviderConfigId so the fallback filter works correctly");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static PipelineRun CreateCompletedRun(
        string runId,
        string issueIdentifier,
        string issueTitle,
        string? agentId = null,
        string? modelName = null,
        DateTimeOffset? startedAt = null)
    {
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = issueTitle,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = startedAt ?? DateTimeOffset.UtcNow,
            AgentId = agentId
        });
        run.CurrentStep = PipelineStep.Completed;
        run.ModelName = modelName;
        run.MarkCompleted();
        return run;
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

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
                {
                    entityType.RemoveIndex(index);
                }
            }
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => _options = options;

        public PipelineDbContext CreateDbContext()
            => new InMemoryPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
