using System.Text.Json;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// The usage thresholds (<see cref="CredentialUsageThresholds.StorageKey"/>) and, per credential, the last usage reading
/// of each window (<c>agent-credential-usage:&lt;credential&gt;</c>), in the <c>KeyValueStore</c> table. Readings are a
/// read-modify-write under the advisory lock <see cref="LockName"/>, so agents reporting at once do not lose each
/// other's windows. Reads take no lock and no cache.
/// </summary>
public sealed class CredentialUsageStore(IDbContextFactory<PipelineDbContext> dbFactory, IDistributedLockProvider locks, TimeProvider time)
{
    /// <summary>The prefix of every reading entry's key (not <see cref="CredentialPauseStore.KeyPrefix"/>).</summary>
    public const string KeyPrefix = "agent-credential-usage:";

    /// <summary>The advisory lock every reading write takes.</summary>
    public const string LockName = "agent-credential-usage";

    /// <summary>
    /// The advisory lock around a pause decision and around an operator's resume, so a reading checked just before a
    /// resume cannot pause the credential again right after it. Taken before <see cref="LockName"/> and the pause lock.
    /// </summary>
    public const string DecisionLockName = "agent-credential-usage-decision";

    /// <summary>Takes <see cref="DecisionLockName"/>; dispose to release it.</summary>
    public Task<IAsyncDisposable> LockDecisionAsync(CancellationToken ct) => locks.AcquireAsync(DecisionLockName, ct);

    /// <summary>The key of <paramref name="credential"/>'s readings.</summary>
    public static string KeyOf(string credential) => KeyPrefix + credential;

    /// <summary>The thresholds as their stored JSON (config export).</summary>
    public static string SerializeThresholds(CredentialUsageThresholds thresholds) =>
        JsonSerializer.Serialize(thresholds, PipelineJsonOptions.Default);

    /// <summary>The thresholds in <paramref name="json"/>; null for null or blank input. Throws <see cref="JsonException"/> on bad JSON (config import).</summary>
    public static CredentialUsageThresholds? ParseThresholds(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CredentialUsageThresholds>(json, PipelineJsonOptions.Lenient);

    /// <summary>The stored thresholds, or <see cref="CredentialUsageThresholds.Default"/> when none are stored.</summary>
    public async Task<CredentialUsageThresholds> LoadThresholdsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return Deserialize<CredentialUsageThresholds>(await ValueAsync(db, CredentialUsageThresholds.StorageKey, ct))
            ?? CredentialUsageThresholds.Default;
    }

    /// <summary>Replaces the stored thresholds (last write wins; the lock keeps two first saves from both inserting).</summary>
    public async Task SaveThresholdsAsync(CredentialUsageThresholds thresholds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        await using var handle = await locks.AcquireAsync(LockName, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await StageAsync(db, CredentialUsageThresholds.StorageKey, thresholds, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The last readings of <paramref name="credential"/>; <see cref="CredentialUsageState.Empty"/> without any.</summary>
    public async Task<CredentialUsageState> LoadAsync(string credential, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return Deserialize<CredentialUsageState>(await ValueAsync(db, KeyOf(credential), ct)) ?? CredentialUsageState.Empty;
    }

    /// <summary>Stores <paramref name="observations"/> as <paramref name="credential"/>'s last readings; returns the new state.</summary>
    public Task<CredentialUsageState> RecordAsync(
        string credential, IReadOnlyList<AgentRateLimitObservation> observations, CancellationToken ct) =>
        UpdateAsync(credential, (state, now) => CredentialUsageRules.Record(state, observations, now), ct);

    /// <summary>An operator resumed <paramref name="credential"/>: its windows over their thresholds are let through until they reset.</summary>
    public async Task ResumeAsync(string credential, CancellationToken ct)
    {
        var thresholds = await LoadThresholdsAsync(ct);
        await UpdateAsync(credential, (state, now) => CredentialUsageRules.Resume(state, thresholds, now), ct);
    }

    private async Task<CredentialUsageState> UpdateAsync(
        string credential, Func<CredentialUsageState, DateTimeOffset, CredentialUsageState> update, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        await using var handle = await locks.AcquireAsync(LockName, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = Deserialize<CredentialUsageState>(await ValueAsync(db, KeyOf(credential), ct)) ?? CredentialUsageState.Empty;
        var updated = update(state, time.GetUtcNow());
        await StageAsync(db, KeyOf(credential), updated, ct);
        await db.SaveChangesAsync(ct);
        return updated;
    }

    private static Task<string?> ValueAsync(PipelineDbContext db, string key, CancellationToken ct) =>
        db.KeyValueStore.AsNoTracking().Where(kv => kv.Key == key).Select(kv => kv.Value).FirstOrDefaultAsync(ct);

    private static async Task StageAsync<T>(PipelineDbContext db, string key, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, PipelineJsonOptions.Default);
        var row = await db.KeyValueStore.FirstOrDefaultAsync(kv => kv.Key == key, ct);
        if (row is null)
            db.KeyValueStore.Add(new KeyValueEntity { Key = key, Value = json });
        else
            row.Value = json;
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, PipelineJsonOptions.Lenient);
        }
        catch (JsonException)
        {
            // An unreadable entry counts as none; the next write replaces it.
            return null;
        }
    }
}
