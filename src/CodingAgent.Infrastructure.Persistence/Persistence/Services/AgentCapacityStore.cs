using System.Text.Json;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// Spec 057 DD5: the UI agent limits, stored as one JSON entry (<see cref="AgentCapacityLimits.StorageKey"/>) in the
/// <c>KeyValueStore</c> table, next to the routing table (<see cref="AgentRoutingStore"/>). Only the Capacity page and a
/// config import write it; saves are last-write-wins. Every read goes to the database (no cache), so a save applies at
/// the next dispatch on every API replica.
/// </summary>
public sealed class AgentCapacityStore(IDbContextFactory<PipelineDbContext> dbFactory)
{
    /// <summary>The stored limits, or <see cref="AgentCapacityLimits.Empty"/> when none are stored yet.</summary>
    public async Task<AgentCapacityLimits> LoadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await LoadAsync(db, ct);
    }

    /// <summary>Replaces the stored limits.</summary>
    public async Task SaveAsync(AgentCapacityLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(limits);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await StageAsync(db, limits, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The limits stored in <paramref name="db"/>, or <see cref="AgentCapacityLimits.Empty"/> when none are stored.</summary>
    public static async Task<AgentCapacityLimits> LoadAsync(PipelineDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entity = await db.KeyValueStore.AsNoTracking()
            .FirstOrDefaultAsync(kv => kv.Key == AgentCapacityLimits.StorageKey, ct);
        return Deserialize(entity?.Value) ?? AgentCapacityLimits.Empty;
    }

    /// <summary>
    /// Adds or updates the entry on <paramref name="db"/> without saving, for callers that write inside their own
    /// transaction (a config import).
    /// </summary>
    public static async Task StageAsync(PipelineDbContext db, AgentCapacityLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(limits);
        var json = Serialize(limits);
        var entity = await db.KeyValueStore.FirstOrDefaultAsync(kv => kv.Key == AgentCapacityLimits.StorageKey, ct);
        if (entity is null)
            db.KeyValueStore.Add(new KeyValueEntity { Key = AgentCapacityLimits.StorageKey, Value = json });
        else
            entity.Value = json;
    }

    public static string Serialize(AgentCapacityLimits limits) => JsonSerializer.Serialize(limits, PipelineJsonOptions.Default);

    /// <summary>The limits in <paramref name="json"/>; null for null or blank input.</summary>
    public static AgentCapacityLimits? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AgentCapacityLimits>(json, PipelineJsonOptions.Lenient);
}
