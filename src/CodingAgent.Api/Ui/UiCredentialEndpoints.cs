using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 056 req 2.4, design "API Endpoints": the agent credentials and their pauses. Every UI user may read them (the
/// banners of Work and Fleet); pausing and resuming needs global operate (D11).
/// </summary>
internal static class UiCredentialEndpoints
{
    /// <summary>The longest pause note an operator may give (req 9.5).</summary>
    internal const int MaxNoteLength = 200;

    public static RouteGroupBuilder MapUiCredentialEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/agent-credentials", GetCredentials);
        group.MapPost("/agent-credentials/{credential}/pause", Pause).RequireAuthorization(UiPolicies.GlobalOperate);
        group.MapPost("/agent-credentials/{credential}/resume", Resume).RequireAuthorization(UiPolicies.GlobalOperate);
        group.MapPut("/agent-credentials/usage-thresholds", SaveUsageThresholds).RequireAuthorization(UiPolicies.Admin);
        return group;
    }

    /// <summary>
    /// GET /api/ui/agent-credentials — every credential a profile uses, plus any paused one, with its pause, the items
    /// held for it, the profiles that use it and its last usage readings; plus the usage thresholds.
    /// </summary>
    internal static async Task<IResult> GetCredentials(
        CredentialPauseStore pauses, IAgentProfileStore profiles, JobTemplateStore templates,
        IDbContextFactory<PipelineDbContext> dbFactory, CredentialUsageStore usage, CancellationToken ct) =>
        TypedResults.Ok(await AgentCredentialViews.ReadPageAsync(pauses, profiles, templates, dbFactory, ct, usage));

    /// <summary>
    /// PUT /api/ui/agent-credentials/usage-thresholds — replaces the usage thresholds of the Claude subscription (admin).
    /// 400 with one line per problem; each changed value is logged with the user.
    /// </summary>
    internal static async Task<IResult> SaveUsageThresholds(
        [FromBody] CredentialUsageThresholds? thresholds, CredentialUsageStore usage, Dispatch.CredentialUsageMonitor monitor,
        IUserAccess access, CancellationToken ct)
    {
        if (thresholds is null)
            return TypedResults.BadRequest("The usage thresholds are missing.");
        var errors = thresholds.Validate();
        if (errors.Count > 0)
            return TypedResults.BadRequest(string.Join("\n", errors));

        var old = await usage.LoadThresholdsAsync(ct);
        await usage.SaveThresholdsAsync(thresholds, ct);
        foreach (var window in CredentialUsageWindows.All.Where(w => old.PercentOf(w) != thresholds.PercentOf(w)))
        {
            Serilog.Log.Information("Usage threshold of the {Window} changed from {Old} % to {New} % by {User}",
                CredentialUsageWindows.Text(window), old.PercentOf(window), thresholds.PercentOf(window),
                CodingAgent.Infrastructure.Common.LogSanitizer.SanitizeForLog(UserName(access)));
        }

        // The new thresholds apply to the last readings at once: a pause they no longer justify ends, a window now over
        // its threshold pauses.
        await monitor.ReevaluateAsync(UserName(access), ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// POST /api/ui/agent-credentials/{credential}/pause — pauses it by hand (replaces any pause). 404 for an unknown
    /// credential; 400 for an end time in the past or a note over 200 characters.
    /// </summary>
    internal static async Task<IResult> Pause(
        string credential, [FromBody] UiCredentialPauseRequest? request, CredentialPauseStore pauses, IUserAccess access,
        TimeProvider time, CancellationToken ct)
    {
        if (!AgentCredentials.IsKnown(credential))
            return UnknownCredential(credential);

        var now = time.GetUtcNow();
        if (request?.Until is { } until && until <= now)
            return TypedResults.BadRequest("The end time of the pause is in the past.");
        if ((request?.Note?.Length ?? 0) > MaxNoteLength)
            return TypedResults.BadRequest($"The note is longer than {MaxNoteLength} characters.");

        await pauses.PauseAsync(new AgentCredentialPause(
            credential,
            CredentialPauseReasons.Manual,
            CredentialPauseSources.Manual,
            now,
            request?.Until,
            request?.Note ?? string.Empty,
            PausedBy: UserName(access)), ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// POST /api/ui/agent-credentials/{credential}/resume — 204 also when it was not paused; 404 when unknown. The windows
    /// of the Claude subscription at or over their usage threshold are let through until they reset, so the resume
    /// sticks.
    /// </summary>
    internal static async Task<IResult> Resume(
        string credential, Dispatch.CredentialUsageMonitor monitor, IUserAccess access, CancellationToken ct)
    {
        if (!AgentCredentials.IsKnown(credential))
            return UnknownCredential(credential);

        await monitor.ResumeAsync(credential, UserName(access), ct);
        return TypedResults.NoContent();
    }

    private static NotFound<string> UnknownCredential(string credential) =>
        TypedResults.NotFound($"Unknown agent credential '{CodingAgent.Infrastructure.Common.LogSanitizer.SanitizeForLog(credential)}'.");

    private static string UserName(IUserAccess access) => access.Username ?? access.Subject ?? "unknown";
}

/// <summary>Spec 056: the views of the credentials and their pauses, shared by the UI and the Scheduler endpoint.</summary>
internal static class AgentCredentialViews
{
    /// <summary>The credentials page: every credential in use plus any paused one, in the order of <see cref="AgentCredentials.All"/>.</summary>
    internal static async Task<UiAgentCredentialsPage> ReadPageAsync(
        CredentialPauseStore pauses, IAgentProfileStore profileStore, JobTemplateStore templates,
        IDbContextFactory<PipelineDbContext>? dbFactory, CancellationToken ct, CredentialUsageStore? usage = null)
    {
        var profiles = await profileStore.LoadAgentProfilesAsync(ct);
        var (heldByCredential, heldByReason) = await HeldCountsAsync(dbFactory, ct);
        var tools = templates.ToolsByKey();
        var active = (await pauses.LoadAllAsync(ct)).ToDictionary(p => p.Credential, StringComparer.Ordinal);
        var inUse = AgentCredentials.InUse(profiles, tools);
        var thresholds = usage is null ? null : await usage.LoadThresholdsAsync(ct);
        var readings = usage is null ? null : await usage.LoadAsync(AgentCredentials.ClaudeSubscription, ct);

        var rows = AgentCredentials.All
            .Where(c => inUse.Contains(c) || active.ContainsKey(c))
            .Select(c => ToRow(c, active.GetValueOrDefault(c), profiles, tools, heldByCredential.GetValueOrDefault(c))
                with { Usage = c == AgentCredentials.ClaudeSubscription ? UsageRows(readings, thresholds) : null })
            .ToList();
        return new UiAgentCredentialsPage(rows, heldByReason, thresholds);
    }

    private static IReadOnlyList<UiCredentialUsage>? UsageRows(CredentialUsageState? state, CredentialUsageThresholds? thresholds) =>
        state is null || thresholds is null
            ? null
            : state.Windows
                // A reading whose window has reset since says nothing about now.
                .Where(w => w.Value.ResetsAt is not { } resets || resets > DateTimeOffset.UtcNow)
                .OrderBy(w => CredentialUsageWindows.Order(w.Key)).ThenBy(w => w.Key, StringComparer.Ordinal)
                .Select(w => new UiCredentialUsage(w.Key, CredentialUsageWindows.Text(w.Key), w.Value.Utilization,
                    w.Value.ResetsAt, w.Value.SeenAt, thresholds.PercentOf(w.Key)))
                .ToList();

    /// <summary>The paused state of every credential in use plus any paused one (<c>GET /api/agents/credentials</c>).</summary>
    internal static async Task<IReadOnlyList<AgentCredentialStatus>> ReadStatusAsync(
        CredentialPauseStore pauses, IAgentProfileStore profileStore, JobTemplateStore templates, CancellationToken ct)
    {
        var page = await ReadPageAsync(pauses, profileStore, templates, dbFactory: null, ct);
        return page.Credentials.Select(c => new AgentCredentialStatus(c.Credential, c.Paused)).ToList();
    }

    /// <summary>
    /// The Pending items held for a paused credential, per credential, and the Pending items with any stored hold, per
    /// hold reason (design "API Endpoints"). Empty without a database.
    /// </summary>
    private static async Task<(Dictionary<string, int> ByCredential, Dictionary<string, int> ByReason)> HeldCountsAsync(
        IDbContextFactory<PipelineDbContext>? dbFactory, CancellationToken ct)
    {
        if (dbFactory is null)
            return (new Dictionary<string, int>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var held = await db.WorkItems.AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending && w.HoldReason != null)
            .GroupBy(w => new { w.HoldReason, w.Credential })
            .Select(g => new { g.Key.HoldReason, g.Key.Credential, Count = g.Count() })
            .ToListAsync(ct);

        var byCredential = held
            .Where(h => h.HoldReason == HoldReasons.CredentialPaused && h.Credential != null)
            .GroupBy(h => h.Credential!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(h => h.Count), StringComparer.Ordinal);
        var byReason = held
            .GroupBy(h => h.HoldReason!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(h => h.Count), StringComparer.Ordinal);
        return (byCredential, byReason);
    }

    private static UiAgentCredential ToRow(
        string credential, AgentCredentialPause? pause, IReadOnlyList<AgentProfile> profiles,
        IReadOnlyDictionary<string, string> tools, int heldItems)
    {
        var names = profiles
            .Where(p => AgentCredentials.For(p, tools) == credential)
            .Select(p => p.DisplayName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new UiAgentCredential(
            credential,
            AgentCredentials.DisplayName(credential),
            pause is not null,
            pause?.Reason,
            pause?.Source,
            pause?.Since,
            pause?.Until,
            pause?.Detail,
            pause?.PausedBy,
            pause is null ? null : HoldTexts.CredentialPaused(pause),
            heldItems,
            names);
    }
}
