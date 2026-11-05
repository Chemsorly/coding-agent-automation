using AwesomeAssertions;
using CodingAgent.Api.IntegrationTests.AgentRouting;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.IntegrationTests.AgentCapacity;

/// <summary>Spec 057 DD5, req 3.6: <see cref="AgentCapacityStore"/> keeps the UI limits in one key-value entry.</summary>
public sealed class AgentCapacityStoreTests
{
    private static AgentCapacityLimits Limits(int global, string key, int value) => new()
    {
        MaxConcurrentAgents = global,
        MaxConcurrentByJobTemplate = new Dictionary<string, int>(StringComparer.Ordinal) { [key] = value },
    };

    [Fact]
    public async Task LoadAsync_NoEntry_ReturnsEmpty()
    {
        var store = new AgentCapacityStore(new AgentRoutingTestSupport.InMemoryFactory(Guid.NewGuid().ToString()));

        (await store.LoadAsync(CancellationToken.None)).Should().BeSameAs(AgentCapacityLimits.Empty);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsTheLimits()
    {
        var store = new AgentCapacityStore(new AgentRoutingTestSupport.InMemoryFactory(Guid.NewGuid().ToString()));

        await store.SaveAsync(Limits(4, "dotnet,kiro", 3), CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        loaded.MaxConcurrentAgents.Should().Be(4);
        loaded.MaxConcurrentByJobTemplate.Should().Equal(new Dictionary<string, int> { ["dotnet,kiro"] = 3 });
    }

    [Fact]
    public async Task SaveAsync_Twice_KeepsOneEntry_LastWriteWins()
    {
        var name = Guid.NewGuid().ToString();
        var store = new AgentCapacityStore(new AgentRoutingTestSupport.InMemoryFactory(name));

        await store.SaveAsync(Limits(4, "dotnet,kiro", 3), CancellationToken.None);
        await store.SaveAsync(Limits(2, "claude", 1), CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);
        loaded.MaxConcurrentAgents.Should().Be(2);
        loaded.MaxConcurrentByJobTemplate.Should().Equal(new Dictionary<string, int> { ["claude"] = 1 });
        await using var db = AgentRoutingTestSupport.InMemoryFactory.Create(name);
        (await db.KeyValueStore.CountAsync(kv => kv.Key == AgentCapacityLimits.StorageKey)).Should().Be(1);
    }
}
