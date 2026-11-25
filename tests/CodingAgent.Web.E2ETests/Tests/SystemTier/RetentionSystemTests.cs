using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure.SystemTier;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Web.E2ETests.Tests.SystemTier;

/// <summary>
/// System-tier tests that run <see cref="DatabaseMaintenanceService.RunRetentionSweepAsync"/> against real
/// Postgres. The sweeps delete with <c>ExecuteDeleteAsync</c> and Postgres <c>DELETE ... USING</c> SQL, which
/// neither EF InMemory nor SQLite can run. Each sweep catches its own exceptions and returns 0, so a broken
/// statement would stop retention without an error: every test asserts on the rows, and uses <c>&gt;=</c> on the
/// counts because other tests' old rows in the shared database may be swept too.
///
/// <para>
/// Each test creates its own project, because rows of other projects would otherwise take rank places in the
/// per-project count sweeps. The stale windows are 7 days for work items and 30 days for runs; every seeded
/// timestamp keeps clear of those edges.
/// </para>
/// </summary>
[Collection(SystemE2ECollection.Name)]
[Trait("Category", "System")]
public sealed class RetentionSystemTests(SystemContainers containers) : SystemE2ETestBase(containers)
{
    [RequiresDockerFact]
    public async Task WhenStaleRowsExist_ShouldDeleteThemAndKeepRecentAndLiveOnes()
    {
        var api = await StartApiAsync();
        var projectId = await CreateProjectAsync(api);

        var old = await InsertWorkItemAsync(api, projectId, WorkItemStatus.Succeeded, DaysAgo(8));
        var recent = await InsertWorkItemAsync(api, projectId, WorkItemStatus.Failed, DaysAgo(1));
        var live = await InsertWorkItemAsync(api, projectId, WorkItemStatus.Running, completedAt: null);
        var oldRun = await InsertRunAsync(api, projectId, startedAt: DaysAgo(31), completedAt: DaysAgo(31));
        var recentRun = await InsertRunAsync(api, projectId, startedAt: DaysAgo(1), completedAt: DaysAgo(1));

        var result = await SweepAsync(api);

        result.StaleWorkItemsDeleted.Should().BeGreaterThanOrEqualTo(1);
        result.StalePipelineRunsDeleted.Should().BeGreaterThanOrEqualTo(1);

        var workItems = await ExistingWorkItemIdsAsync(api, old, recent, live);
        workItems.Should().NotContain(old, "a terminal work item completed 8 days ago is past the 7 day window");
        workItems.Should().Contain(recent, "a terminal work item completed 1 day ago is inside the window");
        workItems.Should().Contain(live, "a running work item has no CompletedAt and is never stale");

        var runs = await ExistingRunIdsAsync(api, oldRun, recentRun);
        runs.Should().NotContain(oldRun, "a run completed 31 days ago is past the 30 day window");
        runs.Should().Contain(recentRun, "a run completed 1 day ago is inside the window");
    }

    [RequiresDockerFact]
    public async Task WhenWorkItemCountExceedsLimit_ShouldKeepNewestPerProjectWithPostgresSql()
    {
        var api = await StartApiAsync();
        var projectP = await CreateProjectAsync(api);
        var projectQ = await CreateProjectAsync(api);

        await SetWorkItemRetentionCountAsync(api, 2);
        try
        {
            var day1 = await InsertWorkItemAsync(api, projectP, WorkItemStatus.Succeeded, DaysAgo(1));
            var day2 = await InsertWorkItemAsync(api, projectP, WorkItemStatus.Succeeded, DaysAgo(2));
            var day3 = await InsertWorkItemAsync(api, projectP, WorkItemStatus.Succeeded, DaysAgo(3));
            var day4 = await InsertWorkItemAsync(api, projectP, WorkItemStatus.Succeeded, DaysAgo(4));
            var running = await InsertWorkItemAsync(api, projectP, WorkItemStatus.Running, completedAt: null);
            var otherProject = await InsertWorkItemAsync(api, projectQ, WorkItemStatus.Succeeded, DaysAgo(5));

            var result = await SweepAsync(api);

            result.RetentionWorkItemsDeleted.Should().BeGreaterThanOrEqualTo(2);

            var workItems = await ExistingWorkItemIdsAsync(api, day1, day2, day3, day4, running, otherProject);
            workItems.Should().Contain(day1, "the newest terminal item of the project is within the limit of 2");
            workItems.Should().Contain(day2, "the second newest terminal item of the project is within the limit of 2");
            workItems.Should().Contain(running, "a running item is not terminal and is exempt from the count sweep");
            workItems.Should().NotContain(day3, "the third newest terminal item of the project is beyond the limit of 2");
            workItems.Should().NotContain(day4, "the fourth newest terminal item of the project is beyond the limit of 2");
            workItems.Should().Contain(otherProject, "the limit counts per project and the other project holds one item");
        }
        finally
        {
            await SetWorkItemRetentionCountAsync(api, -1);
        }
    }

    [RequiresDockerFact]
    public async Task WhenPipelineRunCountExceedsLimit_ShouldKeepNewestPerProjectWithPostgresSql()
    {
        var api = await StartApiAsync();
        var projectP = await CreateProjectAsync(api);
        var projectQ = await CreateProjectAsync(api);

        await SetPipelineRunRetentionCountAsync(api, 2);
        try
        {
            var day1 = await InsertRunAsync(api, projectP, startedAt: DaysAgo(1), completedAt: DaysAgo(1));
            var day2 = await InsertRunAsync(api, projectP, startedAt: DaysAgo(2), completedAt: DaysAgo(2));
            var day3 = await InsertRunAsync(api, projectP, startedAt: DaysAgo(3), completedAt: DaysAgo(3));
            var day4 = await InsertRunAsync(api, projectP, startedAt: DaysAgo(4), completedAt: DaysAgo(4));

            // FinalStep Created, not Completed: the same sweep's orphan reconciliation backfills
            // CompletedAt on runs whose FinalStep is terminal, which would make this run eligible.
            var open = await InsertRunAsync(
                api, projectP, startedAt: DaysAgo(10), completedAt: null, finalStep: PipelineStep.Created);

            // Older than every run of P: ranked across all projects it would fall beyond the limit of 2.
            var otherProject = await InsertRunAsync(api, projectQ, startedAt: DaysAgo(5), completedAt: DaysAgo(5));

            var result = await SweepAsync(api);

            result.RetentionPipelineRunsDeleted.Should().BeGreaterThanOrEqualTo(2);

            var runs = await ExistingRunIdsAsync(api, day1, day2, day3, day4, open, otherProject);
            runs.Should().Contain(day1, "the newest completed run of the project is within the limit of 2");
            runs.Should().Contain(day2, "the second newest completed run of the project is within the limit of 2");
            runs.Should().Contain(open, "a run without CompletedAt is still open and exempt from the count sweep");
            runs.Should().NotContain(day3, "the third newest completed run of the project is beyond the limit of 2");
            runs.Should().NotContain(day4, "the fourth newest completed run of the project is beyond the limit of 2");
            runs.Should().Contain(otherProject, "the limit counts per project and the other project holds one run");
        }
        finally
        {
            await SetPipelineRunRetentionCountAsync(api, -1);
        }
    }

    private static DateTimeOffset DaysAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days);

    /// <summary>
    /// Saves a new project. <c>WorkItems.ProjectId</c> has a foreign key to <c>Projects</c>, so a project
    /// must exist before work items reference it.
    /// </summary>
    private static async Task<Guid> CreateProjectAsync(SystemApiFactory api)
    {
        var projectId = Guid.NewGuid();
        await api.Services.GetRequiredService<IProjectStore>().SaveProjectAsync(
            new PipelineProject { Id = projectId.ToString(), Name = $"retention-{projectId:N}", Enabled = true, TemplateIds = [] },
            CancellationToken.None);
        return projectId;
    }

    private static async Task<Guid> InsertWorkItemAsync(
        SystemApiFactory api, Guid projectId, WorkItemStatus status, DateTimeOffset? completedAt)
    {
        var dbFactory = api.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var id = Guid.NewGuid();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"retention-{Guid.NewGuid():N}",
            IssueProviderConfigId = "issue-e2e",
            Status = status,
            Payload = "{}",
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            CreatedAt = DaysAgo(60),
            CompletedAt = completedAt,
            ProjectId = projectId
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> InsertRunAsync(
        SystemApiFactory api, Guid projectId, DateTimeOffset startedAt, DateTimeOffset? completedAt,
        PipelineStep finalStep = PipelineStep.Completed)
    {
        var dbFactory = api.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var id = Guid.NewGuid();
        db.PipelineRuns.Add(new PipelineRunEntity
        {
            RunId = id,
            IssueIdentifier = $"owner/repo#{id}",
            FinalStep = finalStep,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ProjectId = projectId.ToString()
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<List<Guid>> ExistingWorkItemIdsAsync(SystemApiFactory api, params Guid[] ids)
    {
        var dbFactory = api.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WorkItems.AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .Select(w => w.Id)
            .ToListAsync();
    }

    private static async Task<List<Guid>> ExistingRunIdsAsync(SystemApiFactory api, params Guid[] ids)
    {
        var dbFactory = api.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PipelineRuns.AsNoTracking()
            .Where(r => ids.Contains(r.RunId))
            .Select(r => r.RunId)
            .ToListAsync();
    }

    private static Task<DatabaseMaintenanceService.RetentionSweepResult> SweepAsync(SystemApiFactory api) =>
        api.Services.GetRequiredService<DatabaseMaintenanceService>().RunRetentionSweepAsync(CancellationToken.None);

    // The retention counts live in the global pipeline config of the shared database, so a test that
    // changes one restores -1 (keep everything) in a finally block.
    private static Task SetWorkItemRetentionCountAsync(SystemApiFactory api, int count) =>
        api.Services.GetRequiredService<IPipelineConfigStore>().UpdatePipelineConfigAsync(
            c => c with { WorkItemRetentionCount = count }, CancellationToken.None);

    private static Task SetPipelineRunRetentionCountAsync(SystemApiFactory api, int count) =>
        api.Services.GetRequiredService<IPipelineConfigStore>().UpdatePipelineConfigAsync(
            c => c with { PipelineRunRetentionCount = count }, CancellationToken.None);
}
