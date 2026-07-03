using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Base class for K8s-mode E2E tests. Pure DB + fake K8s client assertions.
/// Provides helpers for inserting WorkItems and waiting for K8s Job creation.
/// </summary>
public abstract class K8sModeE2ETestBase : IAsyncLifetime
{
    protected K8sModeE2EFixture Fixture { get; }

    protected K8sModeE2ETestBase(K8sModeE2EFixture fixture)
    {
        Fixture = fixture;
    }

    public Task InitializeAsync()
    {
        Fixture.Factory.ResetAll();

        var factory = Fixture.Factory.Services.GetRequiredService<IProviderFactory>();
        if (factory is not FakeProviderFactory)
            throw new InvalidOperationException(
                $"DI replacement failed: IProviderFactory resolved as {factory.GetType().Name}");

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Dispatch Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Inserts a WorkItem directly into the DB as Pending (simulating what
    /// KubernetesWorkDistributor.DistributeAsync does).
    /// </summary>
    protected async Task<Guid> InsertPendingWorkItemAsync(
        string issueIdentifier,
        string agentSelector = "kiro,dotnet",
        int timeoutSeconds = 3600,
        string? projectId = null)
    {
        var workItemId = Guid.NewGuid();
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Pending,
            Payload = "{}",
            AgentSelector = agentSelector,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = timeoutSeconds,
            ProjectId = projectId ?? WellKnownIds.DefaultProjectId
        });
        await db.SaveChangesAsync();
        return workItemId;
    }

    /// <summary>
    /// Uses KubernetesWorkDistributor.DistributeAsync via DI to distribute work
    /// (inserts WorkItem as Pending).
    /// </summary>
    protected async Task<DistributionResult> DistributeViaK8sAsync(
        string issueIdentifier,
        string agentSelector = "kiro,dotnet")
    {
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            AgentSelector = agentSelector,
            TimeoutSeconds = 3600,
            TaskType = WorkItemTaskType.Implementation,
            ProjectId = WellKnownIds.DefaultProjectId,
            InitiatedBy = "k8s-e2e-test"
        };
        return await distributor.DistributeAsync(request, CancellationToken.None);
    }

    // ── Wait Helpers ──────────────────────────────────────────────────────

    /// <summary>Polls until the FakeK8sClient has at least the expected number of created jobs.</summary>
    protected async Task WaitForK8sJobCreatedAsync(int expectedCount = 1, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (Fixture.K8sClient.CreatedJobs.Count >= expectedCount)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        throw new TimeoutException(
            $"Expected {expectedCount} K8s Job(s), got {Fixture.K8sClient.CreatedJobs.Count} within " +
            $"{(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }

    /// <summary>Polls until a WorkItem reaches the expected status.</summary>
    protected async Task<WorkItemEntity> WaitForWorkItemStatusAsync(
        Guid workItemId,
        WorkItemStatus expectedStatus,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
            if (item?.Status == expectedStatus) return item;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        await using var finalDb = Fixture.DbContextFactory.CreateDbContext();
        var finalItem = await finalDb.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        throw new TimeoutException(
            $"WorkItem {workItemId} did not reach status {expectedStatus}. " +
            $"Current: {finalItem?.Status.ToString() ?? "NOT FOUND"}");
    }

    /// <summary>Polls a condition until true.</summary>
    protected static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        throw new TimeoutException("Condition not met within timeout");
    }
}
