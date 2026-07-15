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
    public void AddRunToHistory_PersistsToDatabase()
    {
        var runId = Guid.NewGuid().ToString();
        var run = CreateCompletedRun(runId, "owner/repo#1", "Fix bug");

        _sut.AddRunToHistory(run);

        using var db = new InMemoryPipelineDbContext(_dbOptions);
        var entities = db.PipelineRuns.ToList();
        entities.Should().HaveCount(1);
        entities[0].IssueIdentifier.Should().Be("owner/repo#1");
        entities[0].IssueTitle.Should().Be("Fix bug");
        entities[0].FinalStep.Should().Be(PipelineStep.Completed);
        entities[0].SummaryJson.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetRunHistory_ReturnsPersistedRuns_OrderedByStartedAtDesc()
    {
        var run1 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-1", "First",
            startedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var run2 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-2", "Second",
            startedAt: DateTimeOffset.UtcNow.AddHours(-1));
        var run3 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-3", "Third",
            startedAt: DateTimeOffset.UtcNow);

        _sut.AddRunToHistory(run1);
        _sut.AddRunToHistory(run2);
        _sut.AddRunToHistory(run3);

        var history = _sut.GetRunHistory();

        history.Should().HaveCount(3);
        history[0].IssueIdentifier.Should().Be("issue-3"); // newest first
        history[1].IssueIdentifier.Should().Be("issue-2");
        history[2].IssueIdentifier.Should().Be("issue-1");
    }

    [Fact]
    public void GetRunHistory_EmptyDatabase_ReturnsEmptyList()
    {
        var history = _sut.GetRunHistory();
        history.Should().BeEmpty();
    }

    [Fact]
    public void GetRunsByAgentId_FiltersCorrectly()
    {
        var run1 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-1", "A", agentId: "agent-1");
        var run2 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-2", "B", agentId: "agent-2");
        var run3 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-3", "C", agentId: "agent-1");

        _sut.AddRunToHistory(run1);
        _sut.AddRunToHistory(run2);
        _sut.AddRunToHistory(run3);

        var result = _sut.GetRunsByAgentId("agent-1");

        result.Should().HaveCount(2);
        result.Should().OnlyContain(r => r.AgentId == "agent-1");
    }

    [Fact]
    public void GetRunsByAgentId_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            var run = CreateCompletedRun(Guid.NewGuid().ToString(), $"issue-{i}", $"Run {i}",
                agentId: "agent-x", startedAt: DateTimeOffset.UtcNow.AddMinutes(-i));
            _sut.AddRunToHistory(run);
        }

        var result = _sut.GetRunsByAgentId("agent-x", limit: 3);

        result.Should().HaveCount(3);
    }

    [Fact]
    public void AddRunToHistory_Upsert_UpdatesExistingRow()
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

        // Complete the run — AddRunToHistory should upsert
        var run = CreateCompletedRun(runId.ToString(), "owner/repo#5", "Updated title");
        _sut.AddRunToHistory(run);

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
    public void GetRunHistory_DeserializesFullSummary_WithAllFields()
    {
        var runId = Guid.NewGuid().ToString();
        var run = CreateCompletedRun(runId, "issue-full", "Full fields",
            agentId: "agent-full", modelName: "gpt-4o");
        run.PullRequestUrl = "https://github.com/org/repo/pull/42";
        run.TotalTokens = 50000;

        _sut.AddRunToHistory(run);

        var history = _sut.GetRunHistory();
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
    public void GetRunHistory_FallsBackToColumns_WhenSummaryJsonIsNull()
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

        var history = _sut.GetRunHistory();

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
    public void GetRunHistory_LimitedToMaxHistorySize()
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

        var history = _sut.GetRunHistory();

        history.Should().HaveCount(maxHistory);
    }

    // ── Consolidation filtering tests ───────────────────────────────────

    [Fact]
    public void GetRunHistory_ExcludesConsolidationRuns()
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
        var history = _sut.GetRunHistory();

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
    public void GetRunsByAgentId_ExcludesConsolidationRuns()
    {
        const string agentId = "agent-test";
        var normalRun = CreateCompletedRun(Guid.NewGuid().ToString(), "org/repo#3", "Normal", agentId: agentId);
        var consolidationRun = PipelineRun.Create(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: Guid.NewGuid().ToString(),
            issueTitle: Guid.NewGuid().ToString(),
            issueProviderConfigId: ConsolidationConstants.ProviderConfigId,
            repoProviderConfigId: "rp-1",
            initiatedBy: ConsolidationConstants.InitiatedBy,
            agentId: agentId);
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
                AgentId = agentId,
                SummaryJson = System.Text.Json.JsonSerializer.Serialize(normalSummary, PipelineJsonOptions.Default)
            });
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.Parse(consolidationRun.RunId),
                IssueIdentifier = consolSummary.IssueIdentifier,
                IssueTitle = consolSummary.IssueTitle,
                FinalStep = consolSummary.FinalStep,
                StartedAt = consolSummary.StartedAtOffset,
                AgentId = agentId,
                SummaryJson = System.Text.Json.JsonSerializer.Serialize(consolSummary, PipelineJsonOptions.Default)
            });
            db.SaveChanges();
        }

        var result = _sut.GetRunsByAgentId(agentId);

        result.Should().HaveCount(1);
        result[0].IssueIdentifier.Should().Be("org/repo#3");
    }

    [Fact]
    public void AddRunToHistory_RejectsConsolidationRun_Silently()
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
        _sut.AddRunToHistory(consolidationRun);

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
