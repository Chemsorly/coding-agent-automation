using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Spec 057 req 10.5: Settings → Agents → Capacity shows the Helm limits, saves a UI limit, and the next dispatch
/// cycle holds work at that limit. <c>ResetAll</c> clears the key-value table and the fake Jobs before every test.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class AgentCapacityE2ETests : E2ETestBase
{
    public AgentCapacityE2ETests(E2EFixture fixture) : base(fixture) { }

    private async Task<Guid> InsertPendingAsync(string issueIdentifier)
    {
        var id = Guid.NewGuid();
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Pending,
            Payload = "{}",
            AgentSelector = "jc-e2e",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task CapacityPage_SavesTheAllAgentsLimit_AndTheNextCycleHoldsAtIt()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Capacity");
        await Page.WaitForSelectorAsync("[data-testid='capacity-table']", new() { Timeout = 15_000 });

        // The E2E job templates all set maxConcurrent: 5 in their Helm values.
        Assert.Equal("5", (await Page.Locator("[data-testid='capacity-row-jc-e2e'] [data-testid='capacity-helm']").TextContentAsync())?.Trim());

        var allInput = Page.Locator("input[data-capacity-key='all']");
        await allInput.FillAsync("1");
        await allInput.BlurAsync();
        await Page.Locator("[data-testid='capacity-save']").ClickAsync();
        await Page.WaitForFunctionAsync(
            "() => document.querySelector(\"[data-testid='capacity-row-all'] [data-testid='capacity-applies']\")?.textContent?.includes('1 (UI)')",
            null, new() { Timeout = 10_000 });

        var stored = await new AgentCapacityStore(Fixture.DbContextFactory).LoadAsync(CancellationToken.None);
        Assert.Equal(1, stored.MaxConcurrentAgents);

        var first = await InsertPendingAsync("capacity-e2e-1");
        var second = await InsertPendingAsync("capacity-e2e-2");
        using (var loop = new WorkItemDispatchLoop(Fixture.WorkItems, null, Serilog.Log.Logger))
            await loop.PollAndDispatchAsync(CancellationToken.None);

        var workItemJobs = Fixture.K8sClient.CreatedJobs.Values
            .Count(j => j.Metadata?.Labels?.ContainsKey("caa/work-item-id") == true);
        Assert.Equal(1, workItemJobs);
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var statuses = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == first || w.Id == second)
            .Select(w => w.Status)
            .ToListAsync();
        Assert.Contains(WorkItemStatus.Pending, statuses);
        Assert.Contains(WorkItemStatus.Dispatched, statuses);
    }
}
