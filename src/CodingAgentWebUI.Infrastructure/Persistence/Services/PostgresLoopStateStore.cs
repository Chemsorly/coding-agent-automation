using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Database-backed implementation of <see cref="ILoopStateStore"/>.
/// Stores loop state as a JSONB row in the KeyValueStore table.
/// </summary>
public sealed class PostgresLoopStateStore : ILoopStateStore
{
    private const string Key = "loop-state";
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    public PostgresLoopStateStore(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<LoopState?> ReadAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KeyValueStore.AsNoTracking()
            .FirstOrDefaultAsync(kv => kv.Key == Key, ct);

        if (entity?.Value is null)
            return null;

        return JsonSerializer.Deserialize<LoopState>(entity.Value, PipelineJsonOptions.Default);
    }

    public async Task WriteAsync(LoopState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(state, PipelineJsonOptions.Default);

        var entity = await db.KeyValueStore.FindAsync([Key], ct);
        if (entity is not null)
        {
            entity.Value = json;
        }
        else
        {
            db.KeyValueStore.Add(new KeyValueEntity { Key = Key, Value = json });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KeyValueStore.FindAsync([Key], ct);
        if (entity is not null)
        {
            db.KeyValueStore.Remove(entity);
            await db.SaveChangesAsync(ct);
        }
    }
}
