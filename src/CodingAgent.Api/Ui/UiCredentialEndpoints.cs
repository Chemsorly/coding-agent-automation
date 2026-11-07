using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;

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
        return group;
    }

    /// <summary>
    /// GET /api/ui/agent-credentials — every credential a profile uses, plus any paused one, with its pause, the items
    /// held for it and the profiles that use it.
    /// </summary>
    internal static async Task<IResult> GetCredentials(
        CredentialPauseStore pauses, IAgentProfileStore profiles, JobTemplateStore templates, CancellationToken ct) =>
        TypedResults.Ok(await AgentCredentialViews.ReadPageAsync(pauses, profiles, templates, ct));

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

    /// <summary>POST /api/ui/agent-credentials/{credential}/resume — 204 also when it was not paused; 404 when unknown.</summary>
    internal static async Task<IResult> Resume(
        string credential, CredentialPauseStore pauses, IUserAccess access, CancellationToken ct)
    {
        if (!AgentCredentials.IsKnown(credential))
            return UnknownCredential(credential);

        await pauses.ResumeAsync(credential, UserName(access), ct);
        return TypedResults.NoContent();
    }

    private static IResult UnknownCredential(string credential) =>
        TypedResults.NotFound($"Unknown agent credential '{CodingAgent.Infrastructure.Common.LogSanitizer.SanitizeForLog(credential)}'.");

    private static string UserName(IUserAccess access) => access.Username ?? access.Subject ?? "unknown";
}

/// <summary>Spec 056: the views of the credentials and their pauses, shared by the UI and the Scheduler endpoint.</summary>
internal static class AgentCredentialViews
{
    /// <summary>The credentials page: every credential in use plus any paused one, in the order of <see cref="AgentCredentials.All"/>.</summary>
    internal static async Task<UiAgentCredentialsPage> ReadPageAsync(
        CredentialPauseStore pauses, IAgentProfileStore profileStore, JobTemplateStore templates, CancellationToken ct)
    {
        var profiles = await profileStore.LoadAgentProfilesAsync(ct);
        var tools = templates.ToolsByKey();
        var active = (await pauses.LoadAllAsync(ct)).ToDictionary(p => p.Credential, StringComparer.Ordinal);
        var inUse = AgentCredentials.InUse(profiles, tools);

        var rows = AgentCredentials.All
            .Where(c => inUse.Contains(c) || active.ContainsKey(c))
            .Select(c => ToRow(c, active.GetValueOrDefault(c), profiles, tools))
            .ToList();
        return new UiAgentCredentialsPage(rows, new Dictionary<string, int>(StringComparer.Ordinal));
    }

    /// <summary>The paused state of every credential in use plus any paused one (<c>GET /api/agents/credentials</c>).</summary>
    internal static async Task<IReadOnlyList<AgentCredentialStatus>> ReadStatusAsync(
        CredentialPauseStore pauses, IAgentProfileStore profileStore, JobTemplateStore templates, CancellationToken ct)
    {
        var page = await ReadPageAsync(pauses, profileStore, templates, ct);
        return page.Credentials.Select(c => new AgentCredentialStatus(c.Credential, c.Paused)).ToList();
    }

    private static UiAgentCredential ToRow(
        string credential, AgentCredentialPause? pause, IReadOnlyList<AgentProfile> profiles,
        IReadOnlyDictionary<string, string> tools)
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
            HeldItems: 0,
            names);
    }
}
