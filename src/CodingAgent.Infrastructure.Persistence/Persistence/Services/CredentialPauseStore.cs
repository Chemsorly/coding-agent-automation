using System.Text.Json;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// Spec 056 DD5: the pause of each agent credential, one JSON entry per credential in the <c>KeyValueStore</c> table
/// (key <c>agent-credential-pause:&lt;credential&gt;</c>). Every write (pause, resume, expiry) is a read-modify-write
/// under the advisory lock <see cref="LockName"/>, so the rules of <see cref="CredentialPauseRules"/> hold across API
/// replicas. Reads take no lock and no cache. A reader that meets an expired entry treats the credential as available
/// and expires it, which records the resume once. Not part of the config export or import (D12).
/// </summary>
public sealed class CredentialPauseStore
{
    /// <summary>The prefix of every entry's key.</summary>
    public const string KeyPrefix = "agent-credential-pause:";

    /// <summary>The advisory lock every write takes.</summary>
    public const string LockName = "agent-credential-pause";

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IDistributedLockProvider _locks;
    private readonly TimeProvider _time;
    private readonly Serilog.ILogger _log;

    public CredentialPauseStore(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IDistributedLockProvider locks,
        TimeProvider time,
        Serilog.ILogger log)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _dbFactory = dbFactory;
        _locks = locks;
        _time = time;
        _log = log.ForContext<CredentialPauseStore>();
    }

    /// <summary>The key of <paramref name="credential"/>'s entry.</summary>
    public static string KeyOf(string credential) => KeyPrefix + credential;

    /// <summary>
    /// <paramref name="text"/> as a pause stores it (req 9.4, 9.5): secret shapes redacted, line breaks escaped, cut to
    /// <see cref="AgentCredentialPause.MaxDetailLength"/>.
    /// </summary>
    public static string CleanDetail(string? text)
    {
        var clean = LogSanitizer.SanitizeForLog(SecretPatternRedactor.Redact((text ?? string.Empty).Trim()));
        return clean.Length <= AgentCredentialPause.MaxDetailLength ? clean : clean[..AgentCredentialPause.MaxDetailLength];
    }

    /// <summary>The active pauses, one per paused credential. Expired entries are expired on the way.</summary>
    public async Task<IReadOnlyList<AgentCredentialPause>> LoadAllAsync(CancellationToken ct)
    {
        List<KeyValueEntity> rows;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            rows = await db.KeyValueStore.AsNoTracking().Where(kv => kv.Key.StartsWith(KeyPrefix)).ToListAsync(ct);
        }

        var now = _time.GetUtcNow();
        var active = new List<AgentCredentialPause>();
        foreach (var pause in rows.Select(r => Deserialize(r.Value)).OfType<AgentCredentialPause>())
        {
            if (pause.IsActive(now))
                active.Add(pause);
            else
                await ExpireAsync(pause.Credential, ct);
        }
        return active;
    }

    /// <summary>The active pause of <paramref name="credential"/>, or null when it is available.</summary>
    public async Task<AgentCredentialPause?> LoadAsync(string credential, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        AgentCredentialPause? pause;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            pause = Deserialize(await db.KeyValueStore.AsNoTracking()
                .Where(kv => kv.Key == KeyOf(credential)).Select(kv => kv.Value).FirstOrDefaultAsync(ct));
        }

        if (pause is null)
            return null;
        if (pause.IsActive(_time.GetUtcNow()))
            return pause;
        await ExpireAsync(credential, ct);
        return null;
    }

    /// <summary>
    /// Applies <paramref name="incoming"/> by the rules of req 2.5 and returns the stored pause, or null when nothing
    /// changed (an automatic pause over a manual one, or one that would not extend the stored end time). The detail is
    /// cleaned (<see cref="CleanDetail"/>) before it is stored or logged.
    /// </summary>
    public async Task<AgentCredentialPause?> PauseAsync(AgentCredentialPause incoming, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        incoming = incoming with { Detail = CleanDetail(incoming.Detail) };

        await using var handle = await _locks.AcquireAsync(LockName, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.KeyValueStore.FirstOrDefaultAsync(kv => kv.Key == KeyOf(incoming.Credential), ct);
        var existing = Deserialize(row?.Value);
        var now = _time.GetUtcNow();
        var wasActive = existing is not null && existing.IsActive(now);

        var stored = CredentialPauseRules.Apply(existing, incoming, now);
        if (stored is null)
        {
            _log.Debug("Credential {Credential} stays paused ({PauseSource}); the {Source} pause changes nothing",
                incoming.Credential, existing?.Source, incoming.Source);
            return null;
        }

        var json = JsonSerializer.Serialize(stored, PipelineJsonOptions.Default);
        if (row is null)
            db.KeyValueStore.Add(new KeyValueEntity { Key = KeyOf(stored.Credential), Value = json });
        else
            row.Value = json;
        await db.SaveChangesAsync(ct);

        if (!wasActive)
            PipelineTelemetry.RecordAgentCredentialPause(stored.Credential, stored.Reason, stored.Source);
        _log.Information(
            "Credential {Credential} paused: {PauseReason} ({PauseSource}) until {PausedUntil}; {PauseDetail}",
            stored.Credential, stored.Reason, stored.Source, stored.Until?.ToString("O") ?? "resumed", stored.Detail);
        return stored;
    }

    /// <summary>
    /// Resumes <paramref name="credential"/> by hand: deletes its entry and clears the holds it caused. Returns false
    /// when it was not paused (also an expired pause, which this expires).
    /// </summary>
    public Task<bool> ResumeAsync(string credential, string by, CancellationToken ct) =>
        RemoveAsync(credential, CredentialPauseSources.Manual, by, ct);

    /// <summary>
    /// Deletes <paramref name="credential"/>'s entry when its end time has passed, records the resume once
    /// (<c>source=reset</c>, req 2.3) and clears the holds it caused. Returns false when another reader already did.
    /// </summary>
    public Task<bool> ExpireAsync(string credential, CancellationToken ct) =>
        RemoveAsync(credential, CredentialPauseSources.Reset, by: null, ct);

    private async Task<bool> RemoveAsync(string credential, string source, string? by, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        await using var handle = await _locks.AcquireAsync(LockName, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.KeyValueStore.FirstOrDefaultAsync(kv => kv.Key == KeyOf(credential), ct);
        if (row is null)
            return false;

        var existing = Deserialize(row.Value);
        var expired = existing is null || !existing.IsActive(_time.GetUtcNow());
        if (source == CredentialPauseSources.Reset && !expired)
            return false;

        db.KeyValueStore.Remove(row);
        await db.SaveChangesAsync(ct);

        // A manual resume of an entry that had already expired is recorded as the reset it was.
        var recorded = expired ? CredentialPauseSources.Reset : source;
        PipelineTelemetry.RecordAgentCredentialResume(credential, recorded);
        await ClearHoldsAsync(db, credential, ct);
        _log.Information("Credential {Credential} resumed ({PauseSource}) by {User}",
            credential, recorded, LogSanitizer.SanitizeForLog(by ?? "reset"));
        return !expired || source == CredentialPauseSources.Reset;
    }

    /// <summary>
    /// Spec 056 DD7: a resume clears the <c>credential_paused</c> holds of <paramref name="credential"/>'s Pending items,
    /// so they regain their place in the queue.
    /// </summary>
    private static Task<int> ClearHoldsAsync(PipelineDbContext db, string credential, CancellationToken ct) =>
        PendingHoldResets.ClearCredentialHoldsAsync(db, credential, ct);

    private static AgentCredentialPause? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AgentCredentialPause>(json, PipelineJsonOptions.Lenient);
        }
        catch (JsonException)
        {
            // An unreadable entry pauses nothing; the next write replaces it.
            return null;
        }
    }
}
