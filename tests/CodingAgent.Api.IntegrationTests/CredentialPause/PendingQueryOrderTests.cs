using AwesomeAssertions;
using CodingAgent.Api.IntegrationTests.AgentRouting;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.CredentialPause;

/// <summary>
/// Spec 056 req 3.5 a, DD10 (task 4.7): items held for an operator hold do not fill the Scheduler's fetch window.
/// (Postgres NULL handling: <c>WorkItemHoldOrderingTests</c> in Infrastructure.IntegrationTests.)
/// </summary>
public sealed class PendingQueryOrderTests
{
    private readonly AgentRoutingTestSupport.InMemoryFactory _db = new(Guid.NewGuid().ToString());

    private static WorkItemEntity Item(string issue, string? holdReason, int minutesAgo, string? credential = null) => new()
    {
        Id = Guid.NewGuid(),
        TaskType = WorkItemTaskType.Implementation,
        IssueIdentifier = issue,
        IssueProviderConfigId = "ip",
        Status = WorkItemStatus.Pending,
        Payload = "{}",
        AgentSelector = holdReason is null ? "" : "dotnet,kiro",
        TimeoutSeconds = 3600,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        HoldReason = holdReason,
        HoldText = holdReason is null ? null : "Kiro paused — quota used up",
        Credential = credential,
    };

    [Fact]
    public async Task SixtyHeldKiroItems_AheadOfAClaudeItem_TheClaudeItemIsInTheWindow_First()
    {
        using (var db = _db.CreateDbContext())
        {
            for (var i = 0; i < 60; i++)
                db.WorkItems.Add(Item($"kiro-{i}", HoldReasons.CredentialPaused, 100 - i, AgentCredentials.Kiro));
            db.WorkItems.Add(Item("claude", null, 1));
            await db.SaveChangesAsync();
        }
        var config = new Mock<IPipelineConfigStore>();
        config.Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineConfiguration());

        var result = await WorkItemQueryEndpoints.GetPendingWorkItems(_db, config.Object, maxResults: 50, ct: CancellationToken.None);

        var items = ((Ok<IReadOnlyList<PendingWorkItemDto>>)result).Value!;
        items.Should().HaveCount(50);
        items[0].IssueIdentifier.Should().Be("claude");
        items[1].HoldReason.Should().Be(HoldReasons.CredentialPaused);
        items[1].Credential.Should().Be(AgentCredentials.Kiro);
        items[1].HoldText.Should().Be("Kiro paused — quota used up");
    }
}
