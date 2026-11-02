using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.Infrastructure.SystemTier;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Web.E2ETests.Tests.SystemTier;

/// <summary>
/// System-tier tests that verify the partial unique index on <c>WorkItems (IssueIdentifier,
/// IssueProviderConfigId) WHERE Status NOT IN (3, 4, 5)</c> is enforced by real Postgres and
/// that the application layer interacts correctly with it.
/// </summary>
[Collection(SystemE2ECollection.Name)]
[Trait("Category", "System")]
public sealed class WorkItemUniqueIndexSystemTests(SystemContainers containers) : SystemE2ETestBase(containers)
{
    [RequiresDockerFact]
    public async Task WhenManyCreatesRaceAcrossReplicas_ShouldLeaveExactlyOneLiveItem()
    {
        // Start two replicas sequentially — they share environment variables so parallel
        // startup would create a data race on Environment.SetEnvironmentVariable calls.
        var a = await StartApiAsync();
        var b = await StartApiAsync();

        // Seed the routing once; both replicas share the same Postgres so both pick it up
        await a.Services.GetRequiredService<IAgentProfileStore>().SaveAgentProfileAsync(
            AgentRoutingTestData.Profile("system-profile", jobTemplate: "e2e"),
            CancellationToken.None);
        await AgentRoutingTestData.SeedRoutingAsync(
            a.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            AgentRoutingTestData.Routing("system-profile"));

        // Create master-key HTTP clients pointing at each replica's real Kestrel port.
        // HttpClient is thread-safe for concurrent calls.
        using var clientA = new HttpClient
        {
            BaseAddress = new Uri(a.ServerAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };
        clientA.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        using var clientB = new HttpClient
        {
            BaseAddress = new Uri(b.ServerAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };
        clientB.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var dbFactory = a.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();

        // 10 rounds of 16 concurrent POSTs.
        // The pre-check in WorkItemStore.CreateAsync (TOCTOU guard) catches most losers but a
        // single round may not reach the unique index backstop — 10 rounds make it statistically
        // certain that at least one loser slips through to the index.
        for (var round = 0; round < 10; round++)
        {
            var roundIssue = $"system-race-{Guid.NewGuid():N}";
            var request = new JobDistributionRequest
            {
                IssueIdentifier = roundIssue,
                IssueProviderConfigId = "issue-e2e",
                RepoProviderConfigId = "repo-e2e",
                InitiatedBy = "system-test",
                TaskType = WorkItemTaskType.Implementation,
                AgentSelector = "e2e",
                TimeoutSeconds = 3600,
                RunId = null  // null → each POST gets a fresh GUID; a shared RunId would make them idempotent
            };

            // TaskCompletionSource acts as a starting gun so all 16 tasks fire at the same instant
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await (i % 2 == 0 ? clientA : clientB)
                    .PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
            })).ToArray();

            gate.SetResult();
            var responses = await Task.WhenAll(tasks);

            // Exactly one winner; every other request must have been rejected
            responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1,
                $"round {round}: exactly one POST should have returned 201 Created");
            responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(15,
                $"round {round}: all other POSTs should have returned 409 Conflict");

            // The database must have exactly one live row for this issue
            await using var db = await dbFactory.CreateDbContextAsync();
            var count = await db.WorkItems.CountAsync(w =>
                w.IssueIdentifier == roundIssue && w.IssueProviderConfigId == "issue-e2e");
            count.Should().Be(1, $"round {round}: exactly one work item should exist in the database");
        }
    }

    [RequiresDockerFact]
    public async Task WhenLiveItemEnds_ShouldAllowNewItemForSameIssue()
    {
        var issue = $"system-{Guid.NewGuid():N}";
        var a = await StartApiAsync();

        // Seed the routing (upsert — idempotent if a prior test already ran)
        await a.Services.GetRequiredService<IAgentProfileStore>().SaveAgentProfileAsync(
            AgentRoutingTestData.Profile("system-profile", jobTemplate: "e2e"),
            CancellationToken.None);
        await AgentRoutingTestData.SeedRoutingAsync(
            a.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            AgentRoutingTestData.Routing("system-profile"));

        using var client = new HttpClient
        {
            BaseAddress = new Uri(a.ServerAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var request = new JobDistributionRequest
        {
            IssueIdentifier = issue,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "system-test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunId = null
        };

        // First POST — creates the work item (Status=Pending after the creation path)
        var response1 = await client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
        response1.StatusCode.Should().Be(HttpStatusCode.Created);

        // Move the work item to a terminal state directly in the database.
        // ExecuteUpdateAsync bypasses tracked-entity concurrency checks (xmin token),
        // eliminating any DbUpdateConcurrencyException risk.
        var dbFactory = a.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        {
            await using var dbUpdate = await dbFactory.CreateDbContextAsync();
            await dbUpdate.WorkItems
                .Where(w => w.IssueIdentifier == issue && w.IssueProviderConfigId == "issue-e2e")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.Status, WorkItemStatus.Failed)
                    .SetProperty(w => w.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow));
        }

        // Second POST for the same issue — must succeed now that the previous item is terminal
        // (Status=Failed=4 is excluded from the partial unique index filter NOT IN (3,4,5))
        var response2 = await client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
        response2.StatusCode.Should().Be(HttpStatusCode.Created);

        // The database must have exactly two rows: one Failed, one Pending
        await using var dbAssert = await dbFactory.CreateDbContextAsync();
        var items = await dbAssert.WorkItems
            .Where(w => w.IssueIdentifier == issue && w.IssueProviderConfigId == "issue-e2e")
            .ToListAsync();
        items.Should().HaveCount(2);
        items.Should().ContainSingle(w => w.Status == WorkItemStatus.Failed);
        items.Should().ContainSingle(w => w.Status == WorkItemStatus.Pending);
    }

    [RequiresDockerFact]
    public async Task WhenSecondLiveRowIsInsertedDirectly_ShouldBeRejectedByPostgres()
    {
        var issue = $"system-{Guid.NewGuid():N}";
        var api = await StartApiAsync();

        // Seed a Running work item (Status=Running=2, within the partial unique index scope
        // "Status" NOT IN (3, 4, 5))
        await SeedRunningWorkItemAsync(api, issue, "agent-a");

        var dbFactory = api.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        // Attempt to insert a second live row (Status=Pending=0, also within the index scope)
        // with the same (IssueIdentifier, IssueProviderConfigId) — Postgres must reject this
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = issue,
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Pending,
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
        });

        // Assert: Postgres enforces the unique index; PostgresErrorClassifier recognises SQLSTATE 23505
        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            async () => await db.SaveChangesAsync());
        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeTrue();
    }
}
