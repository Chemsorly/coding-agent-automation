using System.Text.Json;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// Spec 053 DD8: the global routing table, stored as one JSON entry (<see cref="AgentRouting.StorageKey"/>) in the
/// <c>KeyValueStore</c> table, apart from the pipeline configuration. Only the Routing endpoints, a config import and
/// the configuration move write it; saves are last-write-wins like every other settings page. It reads the table
/// through <see cref="IDbContextFactory{TContext}"/> (not the scoped <c>IKeyValueStore</c>), so singletons can use it.
/// The API's configuration store is uncached, so there is nothing to invalidate across replicas.
/// </summary>
public sealed class AgentRoutingStore(IDbContextFactory<PipelineDbContext> dbFactory)
{
    /// <summary>The stored routing table, or null when none is stored yet.</summary>
    public async Task<AgentRouting?> LoadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await LoadAsync(db, ct);
    }

    /// <summary>Replaces the stored routing table.</summary>
    public async Task SaveAsync(AgentRouting routing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(routing);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await StageAsync(db, routing, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The routing table stored in <paramref name="db"/>, or null when none is stored yet.</summary>
    public static async Task<AgentRouting?> LoadAsync(PipelineDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entity = await db.KeyValueStore.AsNoTracking()
            .FirstOrDefaultAsync(kv => kv.Key == AgentRouting.StorageKey, ct);
        return Deserialize(entity?.Value);
    }

    /// <summary>
    /// Adds or updates the routing entry on <paramref name="db"/> without saving, for callers that write inside their
    /// own transaction (the configuration move, a config import).
    /// </summary>
    public static async Task StageAsync(PipelineDbContext db, AgentRouting routing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(routing);
        var json = Serialize(routing);
        var entity = await db.KeyValueStore.FirstOrDefaultAsync(kv => kv.Key == AgentRouting.StorageKey, ct);
        if (entity is null)
            db.KeyValueStore.Add(new KeyValueEntity { Key = AgentRouting.StorageKey, Value = json });
        else
            entity.Value = json;
    }

    public static string Serialize(AgentRouting routing) => JsonSerializer.Serialize(routing, PipelineJsonOptions.Default);

    /// <summary>The table in <paramref name="json"/>; null for null or blank input.</summary>
    public static AgentRouting? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AgentRouting>(json, PipelineJsonOptions.Lenient);
}
