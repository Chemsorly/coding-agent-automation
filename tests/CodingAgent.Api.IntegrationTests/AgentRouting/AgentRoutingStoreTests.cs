using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using RoutingTable = CodingAgent.Pipeline.Models.AgentRouting;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>Spec 053 task 3.3: <see cref="AgentRoutingStore"/> keeps the routing table in one key-value entry.</summary>
public sealed class AgentRoutingStoreTests
{
    [Fact]
    public async Task LoadAsync_NoEntry_ReturnsNull()
    {
        var name = Guid.NewGuid().ToString();
        var store = new AgentRoutingStore(new AgentRoutingTestSupport.InMemoryFactory(name));

        (await store.LoadAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsTheTable()
    {
        var name = Guid.NewGuid().ToString();
        var store = new AgentRoutingStore(new AgentRoutingTestSupport.InMemoryFactory(name));
        var routing = AgentRoutingTestData.Routing(
            "profile-kiro", "sonnet", "high", (WorkItemTaskType.Triage, AgentRoutingTestData.Route("profile-claude", "opus", "xhigh")));

        await store.SaveAsync(routing, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        loaded!.Default.Should().Be(routing.Default);
        loaded.ByTaskType.Should().BeEquivalentTo(routing.ByTaskType);
    }

    [Fact]
    public async Task SaveAsync_ReplacesTheTable_InTheOneEntry_LastWriteWins()
    {
        var name = Guid.NewGuid().ToString();
        var store = new AgentRoutingStore(new AgentRoutingTestSupport.InMemoryFactory(name));

        await store.SaveAsync(AgentRoutingTestData.Routing("first"), CancellationToken.None);
        await store.SaveAsync(AgentRoutingTestData.Routing("second"), CancellationToken.None);

        (await store.LoadAsync(CancellationToken.None))!.Default!.ProfileId.Should().Be("second");
        await using var db = AgentRoutingTestSupport.NewDb(name);
        (await db.KeyValueStore.CountAsync(kv => kv.Key == RoutingTable.StorageKey)).Should().Be(1);
    }

    [Fact]
    public async Task LoadAsync_KeepsOtherKeyValueRows()
    {
        var name = Guid.NewGuid().ToString();
        await using (var db = AgentRoutingTestSupport.NewDb(name))
        {
            db.KeyValueStore.Add(new KeyValueEntity { Key = "first_run_banner_dismissed", Value = "true" });
            await db.SaveChangesAsync();
        }
        var store = new AgentRoutingStore(new AgentRoutingTestSupport.InMemoryFactory(name));

        await store.SaveAsync(RoutingTable.Empty, CancellationToken.None);

        await using var check = AgentRoutingTestSupport.NewDb(name);
        (await check.KeyValueStore.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deserialize_Blank_IsNull(string? json) => AgentRoutingStore.Deserialize(json).Should().BeNull();
}
