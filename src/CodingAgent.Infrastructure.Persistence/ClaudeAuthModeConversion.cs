using System.Text.Json;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure;

/// <summary>
/// Spec 056 DD16 (D8): Claude Code's auth mode <c>auto</c> is gone. Every Claude Code profile whose <c>authMode</c> is not
/// <c>apiKey</c> or <c>subscription</c> (the former <c>auto</c>, empty, missing or unknown) is set to <c>subscription</c>,
/// and each change is logged. A profile is a Claude Code profile when its job template's tool is <c>claude</c>, or, when
/// its job template is not in the Helm values, when it has an <c>authMode</c> setting (only Claude Code has one).
/// Idempotent: a second run changes nothing. Runs at startup with the other configuration moves, and inside a config
/// import's transaction.
/// </summary>
public static class ClaudeAuthModeConversion
{
    /// <summary>Takes <c>MigrationLockKey</c> and runs <see cref="RunAsync"/>. The startup entry point.</summary>
    /// <returns>How many profiles changed.</returns>
    public static async Task<int> RunWithLockAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IDistributedLockProvider lockProvider,
        IReadOnlyDictionary<string, string> toolsByKey,
        Serilog.ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(lockProvider);

        await using var lockHandle = await lockProvider.AcquireAsync(DatabaseStartupService.MigrationLockKey, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await RunAsync(db, toolsByKey, logger, ct);
    }

    /// <summary>
    /// Converts the profiles of <paramref name="db"/> and saves them, inside the caller's transaction when there is one.
    /// A profile whose stored JSON cannot be read is left as it is.
    /// </summary>
    /// <returns>How many profiles changed.</returns>
    public static async Task<int> RunAsync(
        PipelineDbContext db,
        IReadOnlyDictionary<string, string> toolsByKey,
        Serilog.ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(toolsByKey);
        ArgumentNullException.ThrowIfNull(logger);

        var changed = 0;
        foreach (var entity in await db.AgentProfiles.ToListAsync(ct))
        {
            if (Read(entity.Configuration) is not { } profile || !IsClaudeCode(profile, toolsByKey))
                continue;

            var settings = profile.Settings ?? new Dictionary<string, string>();
            var current = settings.GetValueOrDefault(ProviderSettingKeys.AuthMode);
            var target = ClaudeCodeAuthModes.Normalize(current); // a known value in any case keeps its meaning
            if (string.Equals(current, target, StringComparison.Ordinal))
                continue;

            var updated = new Dictionary<string, string>(settings, StringComparer.Ordinal)
            {
                [ProviderSettingKeys.AuthMode] = target,
            };
            entity.Configuration = JsonSerializer.Serialize(profile with { Settings = updated }, PipelineJsonOptions.Default);
            changed++;
            logger.Information(
                "Claude auth mode conversion: agent profile {AgentProfile} ({AgentProfileId}) authMode {OldAuthMode} → {NewAuthMode}",
                profile.DisplayName, profile.Id, string.IsNullOrEmpty(current) ? "(none)" : current, target);
        }

        if (changed > 0)
            await db.SaveChangesAsync(ct);
        return changed;
    }

    private static bool IsClaudeCode(AgentProfile profile, IReadOnlyDictionary<string, string> toolsByKey) =>
        toolsByKey.TryGetValue(profile.JobTemplateKey(), out var tool)
            ? ReferenceEquals(AgentToolCatalog.Find(tool), AgentToolCatalog.ClaudeCode)
            : profile.Settings?.ContainsKey(ProviderSettingKeys.AuthMode) == true;

    private static AgentProfile? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AgentProfile>(json, PipelineJsonOptions.Lenient);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
