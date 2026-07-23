using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="PostgresActiveRunQueryService"/>.
/// Validates that Active Runs query includes both DB-backed WorkItems AND
/// in-memory-only runs (e.g., restored from agent reconnection).
/// </summary>
public sealed class PostgresActiveRunQueryServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _options;
    private readonly Mock<IOrchestratorRunService> _mockRunService = new();

    public PostgresActiveRunQueryServiceTests()
    {
        _options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"ActiveRunQuery_{Guid.NewGuid()}")
            .Options;
    }

    /// <summary>
    /// Regression test: when an agent reconnects and restores a run via RegisterAgent,
    /// the run exists only in-memory (no WorkItem row in DB). The Active Runs query
    /// must still include it — otherwise monitoring shows fewer active runs than busy agents.
    /// </summary>
    [Fact]
    public async Task GetActiveRunsAsync_InMemoryRunWithoutWorkItem_IncludedInResults()
    {
        // Arrange — a run exists in-memory but has no matching WorkItem in DB
        var restoredRun = PipelineRun.Create(
            runId: "ae3d6ae3-b243-4e45-ba90-788a88737134",
            issueIdentifier: "992",
            issueTitle: "Extract duplicated JobDistributionRequest",
            issueProviderConfigId: "issue-cfg-1",
            repoProviderConfigId: "repo-cfg-1",
            runType: PipelineRunType.Implementation,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            initiatedBy: "loop",
            agentId: "agent-dotnet-2");
        restoredRun.CurrentStep = PipelineStep.GeneratingCode;

        _mockRunService.Setup(r => r.GetActiveRuns())
            .Returns(new List<PipelineRun> { restoredRun });
        _mockRunService.Setup(r => r.GetRun("ae3d6ae3-b243-4e45-ba90-788a88737134"))
            .Returns(restoredRun);

        var factory = new InMemoryDbContextFactory(_options);
        var service = new PostgresActiveRunQueryService(factory, _mockRunService.Object);

        // Act
        var results = await service.GetActiveRunsAsync();

        // Assert — the in-memory-only run must appear
        results.Should().ContainSingle(r => r.RunId == "ae3d6ae3-b243-4e45-ba90-788a88737134");
        var run = results.First(r => r.RunId == "ae3d6ae3-b243-4e45-ba90-788a88737134");
        run.AgentId.Should().Be("agent-dotnet-2");
        run.IssueTitle.Should().Be("Extract duplicated JobDistributionRequest");
        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public async Task GetActiveRunsAsync_BothDbAndInMemoryRuns_ReturnsAll()
    {
        // Arrange — one run in DB, one only in-memory
        var workItemId = Guid.NewGuid();
        await using (var db = new InMemoryPipelineDbContext(_options))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "988",
                IssueProviderConfigId = "issue-cfg-1",
                Status = WorkItemStatus.Dispatched,
                TaskType = WorkItemTaskType.Implementation,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Payload = "{}",
                AssignedAgentId = "agent-dotnet-1"
            });
            await db.SaveChangesAsync();
        }

        var dbRun = PipelineRun.Create(
            runId: workItemId.ToString(),
            issueIdentifier: "988",
            issueTitle: "Apply Facade Service pattern",
            issueProviderConfigId: "issue-cfg-1",
            repoProviderConfigId: "repo-cfg-1",
            runType: PipelineRunType.Implementation,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            initiatedBy: "loop",
            agentId: "agent-dotnet-1");
        dbRun.CurrentStep = PipelineStep.GeneratingCode;

        var restoredRun = PipelineRun.Create(
            runId: "ae3d6ae3-b243-4e45-ba90-788a88737134",
            issueIdentifier: "992",
            issueTitle: "Extract duplicated construction",
            issueProviderConfigId: "issue-cfg-1",
            repoProviderConfigId: "repo-cfg-1",
            runType: PipelineRunType.Implementation,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-3),
            initiatedBy: "loop",
            agentId: "agent-dotnet-2");
        restoredRun.CurrentStep = PipelineStep.ReviewingCode;

        _mockRunService.Setup(r => r.GetActiveRuns())
            .Returns(new List<PipelineRun> { dbRun, restoredRun });
        _mockRunService.Setup(r => r.GetRun(workItemId.ToString())).Returns(dbRun);
        _mockRunService.Setup(r => r.GetRun("ae3d6ae3-b243-4e45-ba90-788a88737134")).Returns(restoredRun);

        var factory = new InMemoryDbContextFactory(_options);
        var service = new PostgresActiveRunQueryService(factory, _mockRunService.Object);

        // Act
        var results = await service.GetActiveRunsAsync();

        // Assert — both runs appear
        results.Should().HaveCount(2);
        results.Should().Contain(r => r.AgentId == "agent-dotnet-1");
        results.Should().Contain(r => r.AgentId == "agent-dotnet-2");
    }

    [Fact]
    public async Task GetActiveRunsAsync_NoDuplicatesWhenRunExistsInBothDbAndMemory()
    {
        // Arrange — same run exists in DB (WorkItem) and in-memory
        var workItemId = Guid.NewGuid();
        await using (var db = new InMemoryPipelineDbContext(_options))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "988",
                IssueProviderConfigId = "issue-cfg-1",
                Status = WorkItemStatus.Dispatched,
                TaskType = WorkItemTaskType.Implementation,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Payload = "{}",
                AssignedAgentId = "agent-dotnet-1"
            });
            await db.SaveChangesAsync();
        }

        var liveRun = PipelineRun.Create(
            runId: workItemId.ToString(),
            issueIdentifier: "988",
            issueTitle: "Apply Facade Service pattern",
            issueProviderConfigId: "issue-cfg-1",
            repoProviderConfigId: "repo-cfg-1",
            runType: PipelineRunType.Implementation,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            initiatedBy: "loop",
            agentId: "agent-dotnet-1");

        _mockRunService.Setup(r => r.GetActiveRuns())
            .Returns(new List<PipelineRun> { liveRun });
        _mockRunService.Setup(r => r.GetRun(workItemId.ToString())).Returns(liveRun);

        var factory = new InMemoryDbContextFactory(_options);
        var service = new PostgresActiveRunQueryService(factory, _mockRunService.Object);

        // Act
        var results = await service.GetActiveRunsAsync();

        // Assert — exactly one entry (no duplicate)
        results.Should().ContainSingle();
    }

    /// <summary>
    /// Regression test: when a run is dispatched normally (no restart), the PipelineRuns
    /// table doesn't have a row yet (only created at completion). The Active Runs query
    /// does a LEFT JOIN which produces null ProjectName from DB. The enrichment loop must
    /// propagate ProjectName from the in-memory PipelineRun so the UI shows it.
    /// </summary>
    [Fact]
    public async Task GetActiveRunsAsync_EnrichesProjectNameFromInMemoryRun_WhenDbHasNoProjectName()
    {
        // Arrange — WorkItem in DB with no matching PipelineRuns row (normal active state)
        var workItemId = Guid.NewGuid();
        await using (var db = new InMemoryPipelineDbContext(_options))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "1572",
                IssueProviderConfigId = "issue-cfg-1",
                Status = WorkItemStatus.Running,
                TaskType = WorkItemTaskType.Implementation,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                Payload = "{}",
                AssignedAgentId = "caa-test-agent"
            });
            // No PipelineRuns row — normal for active runs before completion
            await db.SaveChangesAsync();
        }

        // In-memory PipelineRun has ProjectName set from dispatch
        var liveRun = PipelineRun.Create(
            runId: workItemId.ToString(),
            issueIdentifier: "1572",
            issueTitle: "Extract step-pipeline builders",
            issueProviderConfigId: "issue-cfg-1",
            repoProviderConfigId: "repo-cfg-1",
            runType: PipelineRunType.Implementation,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            initiatedBy: "loop",
            agentId: "caa-test-agent");
        liveRun.CurrentStep = PipelineStep.VerifyingBaseline;
        liveRun.ProjectId = "019f1860-8b18-7b7e-ba7c-89afe24853b1";
        liveRun.ProjectName = "Default";

        _mockRunService.Setup(r => r.GetActiveRuns())
            .Returns(new List<PipelineRun> { liveRun });
        _mockRunService.Setup(r => r.GetRun(workItemId.ToString())).Returns(liveRun);

        var factory = new InMemoryDbContextFactory(_options);
        var service = new PostgresActiveRunQueryService(factory, _mockRunService.Object);

        // Act
        var results = await service.GetActiveRunsAsync();

        // Assert — ProjectName must be enriched from in-memory run
        results.Should().ContainSingle();
        var result = results[0];
        result.ProjectName.Should().Be("Default");
        result.CurrentStep.Should().Be(PipelineStep.VerifyingBaseline);
    }

    public void Dispose() { }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<PipelineDbContext>(new InMemoryPipelineDbContext(_options));
    }

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
}
