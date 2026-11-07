using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Spec 056 DD10 (task 4.9, S7): operator holds are fetched last, and against Postgres an item without a hold (NULL)
/// is not sorted with them. EF InMemory cannot show the NULL case: it evaluates <c>Contains</c> in .NET.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WorkItemHoldOrderingTests : IClassFixture<WorkItemPostgresFixture>
{
    private readonly WorkItemPostgresFixture _fixture;

    public WorkItemHoldOrderingTests(WorkItemPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static WorkItemEntity Item(string issue, string? holdReason, int minutesAgo) => new()
    {
        Id = Guid.NewGuid(),
        TaskType = WorkItemTaskType.Implementation,
        IssueIdentifier = issue,
        IssueProviderConfigId = "ip",
        Status = WorkItemStatus.Pending,
        Payload = "{}",
        AgentSelector = "",
        TimeoutSeconds = 3600,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        HoldReason = holdReason,
    };

    [Fact]
    public async Task OperatorHolds_SortLast_UnheldAndCapacityHoldsKeepTheirPlace_DD10()
    {
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the container must start and all migrations must apply");
        await _fixture.ResetAsync();
        await using (var db = _fixture.CreateDbContext())
        {
            db.WorkItems.AddRange(
                Item("paused-oldest", HoldReasons.CredentialPaused, 50),
                Item("no-route", HoldReasons.NoRoute, 40),
                Item("unheld", null, 30),
                Item("limit", HoldReasons.ConcurrencyLimit, 20),
                Item("unheld-newest", null, 10));
            await db.SaveChangesAsync();
        }

        await using var read = _fixture.CreateDbContext();
        var order = await read.WorkItems.AsNoTracking()
            .OrderByOperatorHoldsLast()
            .ThenBy(w => w.CreatedAt)
            .Select(w => w.IssueIdentifier)
            .ToListAsync();

        order.Should().Equal("unheld", "limit", "unheld-newest", "paused-oldest", "no-route");
    }
}
