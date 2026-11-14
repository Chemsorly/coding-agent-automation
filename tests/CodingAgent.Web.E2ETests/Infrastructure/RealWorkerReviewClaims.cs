using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Claims a PR review work item for the real agent worker (<see cref="RealAgentWorkerHarness"/>), the way
/// <c>RealAgentWorkerSmokeTests</c> claims implementation and decomposition items.
/// </summary>
internal static class RealWorkerReviewClaims
{
    /// <summary>
    /// Creates the Review work item for pull request <paramref name="prNumber"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would. Returns the work item GUID.
    ///
    /// <para>
    /// The row is inserted Pending and routed first: the claim refuses an item whose payload has no pinned route
    /// (spec 056), and only a Pending item can be routed. The row goes straight to Dispatched afterwards, so
    /// <c>FakeJobController</c> never hands it to a fake agent: it can only start items for a <c>FakeAgentClient</c>,
    /// and the real worker is none.
    /// </para>
    ///
    /// <para>
    /// The payload is the identity-only one the review dispatch writes (<c>PayloadSchemaVersion</c> 1); the claim
    /// rebuilds the pull request from it and adds the configs, so the tracker is not read for the PR.
    /// </para>
    /// </summary>
    internal static async Task<Guid> ClaimReviewWorkItemAsync(
        this E2EFixture fixture,
        int prNumber,
        string agentId,
        string prBranch = "feature/auto-42-review",
        string targetBranch = "main",
        string prDescription = "",
        string? prAuthor = null,
        CancellationToken ct = default)
    {
        var identifier = prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var workItemId = Guid.NewGuid();

        // Enrichment resolves the agent profile from the "e2e" selector; seeded before the route is pinned.
        await fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-e2e-real-review",
            DisplayName = "Real review smoke profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

        var payload = new JobDistributionRequest
        {
            IssueIdentifier = identifier,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-review-test",
            TaskType = WorkItemTaskType.Review,
            RunType = PipelineRunType.Review,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            PayloadSchemaVersion = 1,
            IssueDetail = new IssueDetail
            {
                IsAuthorVerified = true,
                Identifier = identifier,
                Title = "Review PR",
                Description = prDescription,
                Labels = []
            },
            LinkedPullRequest = new LinkedPullRequest
            {
                Number = prNumber,
                BranchName = prBranch,
                IsDraft = false,
                Url = $"https://github.com/e2e-org/e2e-repo/pull/{prNumber}"
            },
            ReviewPrTargetBranch = targetBranch,
            ReviewPrDescription = prDescription,
            ReviewPrAuthor = prAuthor
        };

        await using (var db = fixture.DbContextFactory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Review,
                IssueIdentifier = identifier,
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Pending,
                AgentSelector = "e2e",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600,
                ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
                Payload = System.Text.Json.JsonSerializer.Serialize(payload, PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync(ct);
        }

        // Spec 056: POST /dispatch routes the item and pins its route; the claim refuses an item dispatched without one.
        var route = await fixture.ApiServices.GetRequiredService<CodingAgent.Api.Dispatch.DispatchRouter>().RouteAsync(workItemId, ct);
        if (route.Status != CodingAgent.Api.Dispatch.DispatchRouteStatus.Routed)
            throw new InvalidOperationException($"Review work item {workItemId} was not routed: {route.Status}");

        // Marked Dispatched to the real agent, as the dispatch path would.
        await using (var dispatchDb = fixture.DbContextFactory.CreateDbContext())
        {
            var dispatched = await dispatchDb.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct)
                ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after routing");
            dispatched.Status = WorkItemStatus.Dispatched;
            dispatched.AssignedAgentId = agentId;
            dispatched.DispatchedAt = DateTimeOffset.UtcNow;
            await dispatchDb.SaveChangesAsync(ct);
        }

        return workItemId;
    }
}
