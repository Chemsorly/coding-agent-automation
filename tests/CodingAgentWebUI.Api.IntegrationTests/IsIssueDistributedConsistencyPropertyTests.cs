// Feature: 035a-postgres-work-queue
// Property 3: IsIssueDistributed Consistency
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Property-based test asserting that <c>GET /api/work-items/is-distributed</c> answers true
/// exactly when the issue has at least one WorkItem that is active — status in
/// {Pending, Dispatched, Running} — or terminal but completed within
/// <see cref="PipelineConstants.DefaultRestartDedupCooldown"/>.
/// **Validates: Requirements 4.6**
/// </summary>
/// <remarks>
/// Originally ran against KubernetesWorkDistributor, then against DbWorkDistributorBase after that
/// distributor became a pure Pipeline API client. DbWorkDistributorBase had no production subclass
/// by then, so the property was guarding a copy of the predicate that nothing called; the class is
/// now deleted and this points at the endpoint that actually decides dispatch dedup.
///
/// The generator produces a *set* of WorkItems per issue rather than one, so the property covers
/// the "any row" semantics across mixed statuses and cooldown positions — the combinatorial part
/// that the exhaustive single-row theories in <see cref="DispatchDedupEndpointTests"/> do not reach.
/// </remarks>
[Collection(ApiIntegrationTestCollection.Name)]
public class IsIssueDistributedConsistencyPropertyTests
{
    private static readonly HashSet<WorkItemStatus> ActiveStatuses =
    [
        WorkItemStatus.Pending,
        WorkItemStatus.Dispatched,
        WorkItemStatus.Running,
    ];

    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public IsIssueDistributedConsistencyPropertyTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    /// <summary>
    /// Property 3: IsIssueDistributed Consistency.
    /// For any generated set of WorkItems sharing one (issue, provider config) pair, the endpoint
    /// returns true iff at least one of them is active, or is terminal and completed inside the
    /// restart-dedup cooldown.
    /// **Validates: Requirements 4.6**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(IsIssueDistributedArbitraries) })]
    public async Task<bool> IsIssueDistributed_ReturnsTrue_IffAnyWorkItemCountsAsActive(SeedRow[] rows)
    {
        // Fresh identifiers per iteration — the InMemory database is shared across the whole
        // collection fixture, so each run must be isolated by key, not by cleanup.
        var issueId = $"owner/repo#{Guid.NewGuid():N}";
        var providerId = $"provider-{Guid.NewGuid():N}";

        foreach (var row in rows)
            Seed(issueId, providerId, row);

        var actual = await IsDistributedAsync(issueId, providerId);

        var expected = rows.Any(r => ActiveStatuses.Contains(r.Status))
                    || rows.Any(r => !ActiveStatuses.Contains(r.Status) && r.CompletedInsideCooldown);

        return actual == expected;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void Seed(string issueIdentifier, string issueProviderConfigId, SeedRow row)
    {
        // An in-flight WorkItem has no CompletedAt; only terminal rows carry one, positioned either
        // side of the cooldown edge. The 30s offset absorbs the drift between seeding here and the
        // endpoint deriving its cutoff from its own UtcNow.
        var margin = TimeSpan.FromSeconds(30);
        DateTimeOffset? completedAt = ActiveStatuses.Contains(row.Status)
            ? null
            : row.CompletedInsideCooldown
                ? DateTimeOffset.UtcNow - (PipelineConstants.DefaultRestartDedupCooldown - margin)
                : DateTimeOffset.UtcNow - (PipelineConstants.DefaultRestartDedupCooldown + margin);

        using var db = _factory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            Status = row.Status,
            Payload = null,
            AgentSelector = "test",
            TimeoutSeconds = 1800,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = completedAt
        });
        db.SaveChanges();
    }

    private async Task<bool> IsDistributedAsync(string issueIdentifier, string issueProviderConfigId)
    {
        var url = $"/api/work-items/is-distributed?issueIdentifier={Uri.EscapeDataString(issueIdentifier)}"
                + $"&issueProviderConfigId={Uri.EscapeDataString(issueProviderConfigId)}";
        var response = await _client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        return body.GetProperty("isDistributed").GetBoolean();
    }
}

/// <summary>One generated WorkItem: its status, and for terminal statuses which side of the
/// restart-dedup cooldown its CompletedAt falls on.</summary>
public sealed record SeedRow(WorkItemStatus Status, bool CompletedInsideCooldown);

/// <summary>
/// FsCheck arbitrary generators for Property 3. Statuses are drawn uniformly from all 6 values;
/// each issue gets 1–4 WorkItems so mixed-status sets are common.
/// </summary>
public class IsIssueDistributedArbitraries
{
    public static Arbitrary<SeedRow[]> SeedRowsArb()
    {
        var rowGen =
            from status in Gen.Elements(
                WorkItemStatus.Pending,
                WorkItemStatus.Dispatched,
                WorkItemStatus.Running,
                WorkItemStatus.Succeeded,
                WorkItemStatus.Failed,
                WorkItemStatus.Cancelled)
            from completedInsideCooldown in Gen.Elements(true, false)
            select new SeedRow(status, completedInsideCooldown);

        return (from count in Gen.Choose(1, 4)
                from rows in rowGen.ArrayOf(count)
                select rows).ToArbitrary();
    }
}
