using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Base class for DB-mode E2E tests. No Playwright — pure SignalR + DB assertions.
/// Provides helpers for dispatching issues and waiting for WorkItem state transitions.
/// </summary>
public abstract class DbModeE2ETestBase : IAsyncLifetime
{
    protected DbModeE2EFixture Fixture { get; }
    protected string BaseUrl => Fixture.ServerAddress;

    protected DbModeE2ETestBase(DbModeE2EFixture fixture)
    {
        Fixture = fixture;
    }

    public Task InitializeAsync()
    {
        // Reset all state between tests
        Fixture.Factory.ResetAll();

        // Guard: verify DI replacement worked
        var factory = Fixture.Factory.Services.GetRequiredService<IProviderFactory>();
        if (factory is not Fakes.FakeProviderFactory)
            throw new InvalidOperationException(
                $"DI replacement failed: IProviderFactory resolved as {factory.GetType().Name} instead of FakeProviderFactory");

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Dispatch Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Dispatches an issue through the full DB-mode pipeline:
    /// IDispatchOrchestrationService.PrepareDistributionRequestAsync → IWorkDistributor.DistributeAsync
    /// Returns the DistributionResult (contains WorkItem ID).
    /// </summary>
    protected async Task<DistributionResult> DispatchIssueAsync(
        string issueIdentifier,
        string? templateId = null,
        string? projectId = null,
        CancellationToken ct = default)
    {
        var orchService = Fixture.Factory.Services.GetRequiredService<IDispatchOrchestrationService>();
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();

        projectId ??= WellKnownIds.DefaultProjectId;
        var project = await Fixture.ConfigStore.GetProjectByIdAsync(projectId, ct)
            ?? throw new InvalidOperationException($"Project '{projectId}' not found in ConfigStore");

        // PrepareDistributionRequestAsync performs full orchestration:
        // issue fetch, label swap, profile/QG resolution, run creation, provider config preparation
        var request = await orchService.PrepareDistributionRequestAsync(
            issueIdentifier,
            issueProviderId: "issue-e2e",
            repoProviderId: "repo-e2e",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "db-e2e-test",
            project: project,
            ct: ct);

        if (request is null)
            return new DistributionResult(
                Success: false,
                WorkItemId: null,
                ErrorMessage: $"Orchestration failed for issue '{issueIdentifier}' " +
                    "(issue not found, no matching profile, or dedup guard rejected).");

        var distResult = await distributor.DistributeAsync(request, ct);
        return distResult;
    }

    // ── Wait Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Polls the WorkItems table until a WorkItem matching the predicate appears, or times out.
    /// </summary>
    protected async Task<WorkItemEntity> WaitForWorkItemAsync(
        Func<WorkItemEntity, bool> predicate,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var match = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => predicate(w));
            if (match is not null) return match;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"No matching WorkItem found within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }

    /// <summary>
    /// Polls until a specific WorkItem reaches the expected status, or times out.
    /// </summary>
    protected async Task<WorkItemEntity> WaitForWorkItemStatusAsync(
        Guid workItemId,
        WorkItemStatus expectedStatus,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
            if (item?.Status == expectedStatus) return item;
            await Task.Delay(interval);
        }

        // Final check with diagnostic info
        await using var finalDb = Fixture.DbContextFactory.CreateDbContext();
        var finalItem = await finalDb.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        throw new TimeoutException(
            $"WorkItem {workItemId} did not reach status {expectedStatus} within " +
            $"{(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s. " +
            $"Current status: {finalItem?.Status.ToString() ?? "NOT FOUND"}");
    }

    /// <summary>
    /// Polls the history service until a run matching the predicate appears, or times out.
    /// </summary>
    protected async Task<PipelineRunSummary> WaitForHistoryAsync(
        Func<PipelineRunSummary, bool> predicate,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            var runs = (await Fixture.HistoryService.GetRunHistoryAsync());
            var match = runs.FirstOrDefault(predicate);
            if (match is not null) return match;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"No matching run appeared in history within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }

    /// <summary>
    /// Polls a condition until it returns true, or times out.
    /// </summary>
    protected static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"Condition not met within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }

    /// <summary>
    /// Polls an async condition until it returns true, or times out.
    /// </summary>
    protected static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"Condition not met within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }
}
