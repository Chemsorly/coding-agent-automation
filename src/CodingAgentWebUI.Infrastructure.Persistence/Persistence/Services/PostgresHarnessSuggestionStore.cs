using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Database-backed implementation of <see cref="IHarnessSuggestionStore"/>.
/// Stores suggestions as a JSONB row in the KeyValueStore table.
/// </summary>
public sealed class PostgresHarnessSuggestionStore : IHarnessSuggestionStore
{
    private const string Key = "harness-suggestions";
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    public PostgresHarnessSuggestionStore(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<HarnessSuggestions?> GetAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KeyValueStore.AsNoTracking()
            .FirstOrDefaultAsync(kv => kv.Key == Key, ct);

        if (entity?.Value is null)
            return null;

        return JsonSerializer.Deserialize<HarnessSuggestions>(entity.Value, PipelineJsonOptions.Default);
    }

    public async Task SaveAsync(HarnessSuggestions suggestions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(suggestions);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(suggestions, PipelineJsonOptions.Default);

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
}
