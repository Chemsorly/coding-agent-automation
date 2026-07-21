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

    [Fact]
    public async Task GetRunHistory_ReturnsPersistedRuns_OrderedByStartedAtDesc()
    {
        var run1 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-1", "First",
            startedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var run2 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-2", "Second",
            startedAt: DateTimeOffset.UtcNow.AddHours(-1));
        var run3 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-3", "Third",
            startedAt: DateTimeOffset.UtcNow);

        await _sut.AddRunToHistoryAsync(run1);
        await _sut.AddRunToHistoryAsync(run2);
        await _sut.AddRunToHistoryAsync(run3);

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(3);
        history[0].IssueIdentifier.Should().Be("issue-3"); // newest first
        history[1].IssueIdentifier.Should().Be("issue-2");
        history[2].IssueIdentifier.Should().Be("issue-1");
    }

    [Fact]
    public async Task GetRunHistory_EmptyDatabase_ReturnsEmptyList()
    {
        var history = await _sut.GetRunHistoryAsync();
        history.Should().BeEmpty();
    }

    [Fact]
    public async Task AddRunToHistory_Upsert_UpdatesExistingRow()
    {
        // TODO: Verify ProjectId is copied during upsert (both primary and retry paths) — currently only IssueTitle/FinalStep are asserted
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
        // TODO: Set and assert ProjectId in this round-trip test to verify ToEntity mapping and deserialization
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
    }

    [Fact]
    public async Task GetRunHistory_LimitedToMaxHistorySize()
    {
        // Insert more than the max (1000) rows
        const int maxHistory = 1000;
        using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            for (var i = 0; i < maxHistory + 50; i++)
            {
                db.PipelineRuns.Add(new PipelineRunEntity
                {
                    RunId = Guid.NewGuid(),
                    IssueIdentifier = $"issue-{i}",
                    IssueTitle = $"Run {i}",
                    FinalStep = PipelineStep.Completed,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                    SummaryJson = null
                });
            }
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(maxHistory);
    }

    // ── Consolidation filtering tests ───────────────────────────────────

    [Fact]
    public async Task GetRunHistory_ExcludesConsolidationRuns()
    {
        // Arrange: persist a normal run and a consolidation ghost entry
        var normalRun = CreateCompletedRun(Guid.NewGuid().ToString(), "org/repo#1", "Normal run");
        var consolidationRun = PipelineRun.Create(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: Guid.NewGuid().ToString(),
            issueTitle: Guid.NewGuid().ToString(),
            issueProviderConfigId: ConsolidationConstants.ProviderConfigId,
            repoProviderConfigId: "rp-1",
            initiatedBy: ConsolidationConstants.InitiatedBy);
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
        var consolidationRun = PipelineRun.Create(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: Guid.NewGuid().ToString(),
            issueTitle: Guid.NewGuid().ToString(),
            issueProviderConfigId: ConsolidationConstants.ProviderConfigId,
            repoProviderConfigId: "rp-1",
            initiatedBy: ConsolidationConstants.InitiatedBy);
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
        var consolidationRun = PipelineRun.Create(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: Guid.NewGuid().ToString(),
            issueTitle: "Consolidation",
            issueProviderConfigId: ConsolidationConstants.ProviderConfigId,
            repoProviderConfigId: "rp-1",
            initiatedBy: ConsolidationConstants.InitiatedBy);
        consolidationRun.CurrentStep = PipelineStep.Completed;
        consolidationRun.MarkCompleted();

        // Should not throw
        await _sut.AddRunToHistoryAsync(consolidationRun);

        // Should not persist to DB
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.PipelineRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task AddRunToHistoryAsync_RejectsConsolidationRun_Silently()
    {
        var consolidationRun = PipelineRun.Create(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: Guid.NewGuid().ToString(),
            issueTitle: "Consolidation",
            issueProviderConfigId: ConsolidationConstants.ProviderConfigId,
            repoProviderConfigId: "rp-1",
            initiatedBy: ConsolidationConstants.InitiatedBy);
        consolidationRun.CurrentStep = PipelineStep.Completed;
        consolidationRun.MarkCompleted();

        await _sut.AddRunToHistoryAsync(consolidationRun);

        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.PipelineRuns.Should().BeEmpty();
    }

    // ── Terminal Step Guard ──────────────────────────────────────────────

    [Fact]
    public async Task AddRunToHistoryAsync_NonTerminalStep_ForcedToFailed()
    {
        // Arrange: run with non-terminal step (should never happen, but defense-in-depth catches it)
        var runId = Guid.NewGuid().ToString();
        var run = PipelineRun.Create(runId, "owner/repo#99", "Bug fix",
            "ip-1", "rp-1");
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

    // TODO: This test is functionally identical to AddRunToHistoryAsync_NonTerminalStep_ForcedToFailed above — consider removing one to reduce maintenance burden.
    [Fact]
    public async Task AddRunToHistoryAsync_NonTerminalStep_DoesNotMutateCallerReference()
    {
        // Arrange: run with non-terminal step
        var runId = Guid.NewGuid().ToString();
        var run = PipelineRun.Create(runId, "owner/repo#102", "Mutation test",
            "ip-1", "rp-1");
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
        var run = PipelineRun.Create(runId, "owner/repo#100", "Feature",
            "ip-1", "rp-1");
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
        var run = PipelineRun.Create(runId, "owner/repo#101", "Sync test",
            "ip-1", "rp-1");
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

    // ── Helpers ──────────────────────────────────────────────────────────

    private static PipelineRun CreateCompletedRun(
        string runId,
        string issueIdentifier,
        string issueTitle,
        string? agentId = null,
        string? modelName = null,
        DateTimeOffset? startedAt = null)
    {
        var run = PipelineRun.Create(
            runId, issueIdentifier, issueTitle,
            "ip-1", "rp-1",
            startedAt: startedAt ?? DateTimeOffset.UtcNow,
            agentId: agentId);
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
