using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for the active pipeline run rehydration logic that populates
/// <see cref="OrchestratorRunService"/> from the WorkItems table on startup (DB mode).
/// </summary>
public sealed class ActiveRunRehydrationTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly OrchestratorRunService _runService;

    public ActiveRunRehydrationTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"Rehydration-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(new Mock<ILogger>().Object);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task Rehydrates_DispatchedAndRunning_WorkItems()
    {
        // Arrange
        var dispatchedRunId = Guid.NewGuid().ToString();
        var runningRunId = Guid.NewGuid().ToString();

        await InsertWorkItem(dispatchedRunId, "owner/repo#1", WorkItemStatus.Dispatched,
            WorkItemTaskType.Implementation, CreatePayload(dispatchedRunId, "owner/repo#1", "Fix bug"));
        await InsertWorkItem(runningRunId, "owner/repo#2", WorkItemStatus.Running,
            WorkItemTaskType.Implementation, CreatePayload(runningRunId, "owner/repo#2", "Add feature"));

        // Act
        await RehydrateAsync();

        // Assert
        var runs = _runService.GetActiveRuns();
        runs.Should().HaveCount(2);

        var dispatchedRun = _runService.GetRun(dispatchedRunId);
        dispatchedRun.Should().NotBeNull();
        dispatchedRun!.IssueIdentifier.Value.Should().Be("owner/repo#1");
        dispatchedRun.IssueTitle.Should().Be("Fix bug");
        dispatchedRun.AgentId.Should().BeNull();
        // TODO: Assert StartedAtOffset matches the InsertWorkItem's DispatchedAt/CreatedAt value
        // to validate the timestamp propagation fix (BUG-08). Currently this test would pass
        // even if the startedAt parameter were removed from the FromDistributionRequest call.

        var runningRun = _runService.GetRun(runningRunId);
        runningRun.Should().NotBeNull();
        runningRun!.IssueIdentifier.Value.Should().Be("owner/repo#2");
        runningRun.IssueTitle.Should().Be("Add feature");
        runningRun.AgentId.Should().BeNull();
    }

    [Fact]
    public async Task Sets_CurrentStep_BasedOnWorkItemStatus()
    {
        // Arrange
        var dispatchedRunId = Guid.NewGuid().ToString();
        var runningRunId = Guid.NewGuid().ToString();

        await InsertWorkItem(dispatchedRunId, "owner/repo#10", WorkItemStatus.Dispatched,
            WorkItemTaskType.Implementation, CreatePayload(dispatchedRunId, "owner/repo#10", "Dispatched"));
        await InsertWorkItem(runningRunId, "owner/repo#11", WorkItemStatus.Running,
            WorkItemTaskType.Implementation, CreatePayload(runningRunId, "owner/repo#11", "Running"));

        // Act
        await RehydrateAsync();

        // Assert
        _runService.GetRun(dispatchedRunId)!.CurrentStep.Should().Be(PipelineStep.Created);
        _runService.GetRun(runningRunId)!.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public async Task Skips_Consolidation_WorkItems()
    {
        // Arrange
        var consolidationRunId = Guid.NewGuid().ToString();
        await InsertWorkItem(consolidationRunId, "consolidation-run-1", WorkItemStatus.Running,
            WorkItemTaskType.Consolidation, CreatePayload(consolidationRunId, "consolidation-run-1", "Consolidation"));

        // Act
        await RehydrateAsync();

        // Assert
        _runService.GetActiveRuns().Should().BeEmpty();
    }

    // TODO: Add a test for Pending status exclusion. Pending is a non-terminal, active-adjacent status
    // that should NOT be rehydrated (DrainService handles those separately). This would prevent
    // accidental widening of the rehydration filter to include Pending items.

    [Fact]
    public async Task Skips_Terminal_Statuses()
    {
        // Arrange
        await InsertWorkItem(Guid.NewGuid().ToString(), "owner/repo#20", WorkItemStatus.Succeeded,
            WorkItemTaskType.Implementation, CreatePayload(Guid.NewGuid().ToString(), "owner/repo#20", "Succeeded"));
        await InsertWorkItem(Guid.NewGuid().ToString(), "owner/repo#21", WorkItemStatus.Failed,
            WorkItemTaskType.Implementation, CreatePayload(Guid.NewGuid().ToString(), "owner/repo#21", "Failed"));
        await InsertWorkItem(Guid.NewGuid().ToString(), "owner/repo#22", WorkItemStatus.Cancelled,
            WorkItemTaskType.Implementation, CreatePayload(Guid.NewGuid().ToString(), "owner/repo#22", "Cancelled"));

        // Act
        await RehydrateAsync();

        // Assert
        _runService.GetActiveRuns().Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_Items_With_NullOrEmpty_Payload()
    {
        // Arrange — null payload
        await InsertWorkItem(Guid.NewGuid().ToString(), "owner/repo#30", WorkItemStatus.Running,
            WorkItemTaskType.Implementation, payload: null);
        // Empty payload
        await InsertWorkItem(Guid.NewGuid().ToString(), "owner/repo#31", WorkItemStatus.Running,
            WorkItemTaskType.Implementation, payload: "");

        // Act
        await RehydrateAsync();

        // Assert — no exception, no runs added
        _runService.GetActiveRuns().Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_Items_With_Corrupt_Payload_Without_Aborting()
    {
        // Arrange — one corrupt, one valid
        var validRunId = Guid.NewGuid().ToString();
        await InsertWorkItem(Guid.NewGuid().ToString(), "owner/repo#40", WorkItemStatus.Running,
            WorkItemTaskType.Implementation, payload: "{{invalid json not parseable");
        await InsertWorkItem(validRunId, "owner/repo#41", WorkItemStatus.Running,
            WorkItemTaskType.Implementation, CreatePayload(validRunId, "owner/repo#41", "Valid run"));

        // Act
        await RehydrateAsync();

        // Assert — corrupt item skipped, valid item rehydrated
        _runService.GetActiveRuns().Should().HaveCount(1);
        _runService.GetRun(validRunId).Should().NotBeNull();
    }

    [Fact]
    public async Task Skips_Items_With_Null_RunId_In_Payload()
    {
        // Arrange — payload with null RunId
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#50",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 1800,
            RunId = null
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await InsertWorkItem(Guid.NewGuid().ToString(), "owner/repo#50", WorkItemStatus.Running,
            WorkItemTaskType.Implementation, payload);

        // Act
        await RehydrateAsync();

        // Assert
        _runService.GetActiveRuns().Should().BeEmpty();
    }

    [Fact]
    public async Task No_Duplicates_When_Run_Already_Exists()
    {
        // Arrange — pre-populate with an existing run (simulating agent already reconnected)
        var runId = Guid.NewGuid().ToString();
        var existingRun = PipelineRun.Create(
            runId: runId,
            issueIdentifier: "owner/repo#60",
            issueTitle: "Already here",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            agentId: "agent-1");
        existingRun.CurrentStep = PipelineStep.ReviewingCode;
        _runService.AddRun(existingRun);

        await InsertWorkItem(runId, "owner/repo#60", WorkItemStatus.Running,
            WorkItemTaskType.Implementation, CreatePayload(runId, "owner/repo#60", "Already here"));

        // Act
        await RehydrateAsync();

        // Assert — still only 1 run, and it's the original (TryAdd is no-op)
        _runService.GetActiveRuns().Should().HaveCount(1);
        var run = _runService.GetRun(runId)!;
        run.AgentId.Should().Be("agent-1"); // Original preserved
        run.CurrentStep.Should().Be(PipelineStep.ReviewingCode); // Original preserved
    }

    [Fact]
    public async Task Rehydrates_Review_And_Decomposition_TaskTypes()
    {
        // Arrange
        var reviewRunId = Guid.NewGuid().ToString();
        var decompositionRunId = Guid.NewGuid().ToString();

        var reviewPayload = CreatePayload(reviewRunId, "owner/repo#70", "Review PR", PipelineRunType.Review);
        var decompPayload = CreatePayload(decompositionRunId, "owner/repo#71", "Decompose epic", PipelineRunType.Decomposition);

        await InsertWorkItem(reviewRunId, "owner/repo#70", WorkItemStatus.Running,
            WorkItemTaskType.Review, reviewPayload);
        await InsertWorkItem(decompositionRunId, "owner/repo#71", WorkItemStatus.Dispatched,
            WorkItemTaskType.Decomposition, decompPayload);

        // Act
        await RehydrateAsync();

        // Assert
        _runService.GetActiveRuns().Should().HaveCount(2);
        _runService.GetRun(reviewRunId)!.RunType.Should().Be(PipelineRunType.Review);
        _runService.GetRun(decompositionRunId)!.RunType.Should().Be(PipelineRunType.Decomposition);
    }

    // ── Helpers ──

    /// <summary>
    /// Simulates the rehydration logic from Program.cs — queries active WorkItems and adds to RunService.
    /// </summary>
    // TODO: This helper duplicates the logic from ActiveRunRehydrationExtensions.RehydrateActiveRunsAsync()
    // rather than calling the actual production code. Tests will still pass if the extension method regresses.
    // Refactor to invoke RehydrateActiveRunsAsync() directly for accurate coverage. (review-findings)
    private async Task RehydrateAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var activeItems = await db.WorkItems
            .AsNoTracking()
            .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                     && w.TaskType != WorkItemTaskType.Consolidation)
            .ToListAsync();

        foreach (var item in activeItems)
        {
            if (string.IsNullOrEmpty(item.Payload)) continue;

            try
            {
                var request = JsonSerializer.Deserialize<JobDistributionRequest>(
                    item.Payload, PipelineJsonOptions.Default);
                if (request is null || string.IsNullOrEmpty(request.RunId)) continue;

                var initialStep = item.Status == WorkItemStatus.Running
                    ? PipelineStep.GeneratingCode
                    : PipelineStep.Created;

                var run = PipelineRunFactory.FromDistributionRequest(
                    request, agentId: null, initialStep,
                    startedAt: item.DispatchedAt ?? item.CreatedAt);
                _runService.AddRun(run);
            }
            catch (JsonException)
            {
                // Skip corrupt payloads — matches production behavior
            }
        }
    }

    private static string CreatePayload(string runId, string issueIdentifier, string title,
        PipelineRunType runType = PipelineRunType.Implementation)
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 1800,
            RunId = runId,
            RunType = runType,
            IssueDetail = new IssueDetail
            {
                Identifier = issueIdentifier,
                Title = title,
                Description = "",
                Labels = []
            }
        };
        return JsonSerializer.Serialize(request, PipelineJsonOptions.Default);
    }

    private async Task InsertWorkItem(string runId, string issueId, WorkItemStatus status,
        WorkItemTaskType taskType, string? payload)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-2);
        // TODO: DispatchedAt is set equal to CreatedAt here, so the `?? item.CreatedAt` fallback
        // path is never exercised. Add a separate test with DispatchedAt = null to verify the
        // fallback scenario (BUG-08: items created but not yet dispatched when orchestrator crashes).
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.TryParse(runId, out var parsed) ? parsed : Guid.NewGuid(),
            IssueIdentifier = issueId,
            IssueProviderConfigId = "ip-1",
            Status = status,
            TaskType = taskType,
            AgentSelector = "",
            CreatedAt = createdAt,
            DispatchedAt = createdAt,
            TimeoutSeconds = 1800,
            Payload = payload,
            AssignedAgentId = "agent-assigned"
        });
        await db.SaveChangesAsync();
    }

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
