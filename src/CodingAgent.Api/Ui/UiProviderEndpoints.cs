using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.GitLab;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.1c: provider configs and the provider operations of Settings, admin only. The web host
/// no longer holds provider credentials: reads return them masked (D4), and every operation takes the
/// form as a draft whose masked values the API fills from the stored config with the same ID
/// (<see cref="UiSecretMask.Restore(ProviderConfig, ProviderConfig?)"/>), so the GitHub and GitLab
/// calls run here with the real credentials.
/// </summary>
internal static class UiProviderEndpoints
{
    public static RouteGroupBuilder MapUiProviderEndpoints(this RouteGroupBuilder settings)
    {
        settings.MapGet("/provider-configs", GetProviderConfigs);
        settings.MapPut("/provider-configs", SaveProviderConfig);
        settings.MapDelete("/provider-configs/{id}", ConfigEndpoints.DeleteProviderConfig);

        settings.MapPost("/provider-configs/check", Check);
        settings.MapPost("/provider-configs/actions-access", CheckActionsAccess);
        settings.MapPost("/provider-configs/repositories", ListRepositories);
        settings.MapPost("/provider-configs/initialize", Initialize);
        settings.MapGet("/provider-configs/{id}/agent-labels", HasAgentLabels);
        settings.MapPost("/provider-configs/{id}/agent-labels", EnsureAgentLabels);

        settings.MapGet("/models", GetModels);
        return settings;
    }

    internal static async Task<IResult> GetProviderConfigs(ProviderKind kind, IProviderConfigStore store, CancellationToken ct) =>
        TypedResults.Ok((await store.LoadProviderConfigsAsync(kind, ct)).Select(UiSecretMask.Redact).ToList());

    /// <summary>
    /// PUT …/provider-configs — saves the draft. With <paramref name="copyFrom"/>, a new config takes
    /// its masked credentials from that stored provider (Settings creates the related issue,
    /// repository and pipeline providers of a repository this way); the URL rule applies to it too.
    /// </summary>
    internal static async Task<IResult> SaveProviderConfig(
        [FromBody] ProviderConfig draft, IProviderConfigStore store, JobTemplateStore templates, IAgentProfileStore profileStore, CancellationToken ct,
        string? copyFrom = null, ProviderKind? copyFromKind = null)
    {
        var stored = await store.GetProviderConfigByIdAsync(draft.Id, draft.Kind, ct);
        if (stored is null && copyFrom is not null && copyFromKind is { } sourceKind)
            stored = await store.GetProviderConfigByIdAsync(copyFrom, sourceKind, ct);

        var (config, error) = UiSecretMask.Restore(draft, stored);
        return config is null ? TypedResults.BadRequest(error) : await ConfigEndpoints.SaveProviderConfig(config, store, templates, profileStore, ct);
    }

    /// <summary>POST …/check — the credentials (and, when given, the repository or project) work.</summary>
    internal static async Task<IResult> Check(
        [FromBody] ProviderConfig draft, IProviderConfigStore store,
        GitHubValidationService gitHub, GitLabValidationService gitLab, CancellationToken ct)
    {
        var (config, error) = await ResolveAsync(draft, store, ct);
        if (config is null)
            return TypedResults.BadRequest(error);

        switch (config.ProviderType)
        {
            case ProviderTypes.GitHub:
                if (!TryGetInstallationId(config, out var installationId))
                    return TypedResults.Ok(new UiProviderCheck(false, InstallationIdMessage));
                var (success, message) = await gitHub.ValidateAppCredentialsAsync(
                    Setting(config, ProviderSettingKeys.ApiUrl), Setting(config, ProviderSettingKeys.ClientId), installationId,
                    Setting(config, ProviderSettingKeys.PrivateKeyBase64), ct,
                    config.Settings.GetValueOrDefault(ProviderSettingKeys.Owner), config.Settings.GetValueOrDefault(ProviderSettingKeys.Repo));
                return TypedResults.Ok(new UiProviderCheck(success, message));

            case ProviderTypes.GitLab:
                var apiUrl = config.Settings.GetValueOrDefault(ProviderSettingKeys.ApiUrl) is { Length: > 0 } url ? url : ProviderSettingKeys.DefaultGitLabApiUrl;
                var result = await gitLab.ValidateAsync(
                    apiUrl, Setting(config, ProviderSettingKeys.AccessToken), Setting(config, ProviderSettingKeys.ProjectId), ct);
                return TypedResults.Ok(result.Success
                    ? new UiProviderCheck(true, $"Connected to {result.ProjectPath} (access: {result.AccessLevel})")
                    : new UiProviderCheck(false, result.ErrorMessage ?? "GitLab rejected the credentials."));

            default:
                return TypedResults.BadRequest($"There is no credential check for {config.ProviderType} providers.");
        }
    }

    /// <summary>POST …/actions-access — the GitHub App can read the repository's workflow runs.</summary>
    internal static async Task<IResult> CheckActionsAccess(
        [FromBody] ProviderConfig draft, IProviderConfigStore store, GitHubValidationService gitHub, CancellationToken ct)
    {
        var (config, error) = await ResolveAsync(draft, store, ct);
        if (config is null)
            return TypedResults.BadRequest(error);
        if (!TryGetInstallationId(config, out var installationId))
            return TypedResults.Ok(new UiProviderCheck(false, InstallationIdMessage));

        var (success, message) = await gitHub.ValidateActionsAccessAsync(
            Setting(config, ProviderSettingKeys.ApiUrl), Setting(config, ProviderSettingKeys.ClientId), installationId,
            Setting(config, ProviderSettingKeys.PrivateKeyBase64),
            Setting(config, ProviderSettingKeys.Owner), Setting(config, ProviderSettingKeys.Repo), ct);
        return TypedResults.Ok(new UiProviderCheck(success, message));
    }

    /// <summary>POST …/repositories — the repositories a GitHub App installation can access; 400 with the reason when the credentials fail.</summary>
    internal static async Task<IResult> ListRepositories(
        [FromBody] ProviderConfig draft, IProviderConfigStore store, GitHubValidationService gitHub, CancellationToken ct)
    {
        var (config, error) = await ResolveAsync(draft, store, ct);
        if (config is null)
            return TypedResults.BadRequest(error);
        if (!TryGetInstallationId(config, out var installationId))
            return TypedResults.BadRequest(InstallationIdMessage);

        var apiUrl = Setting(config, ProviderSettingKeys.ApiUrl);
        var clientId = Setting(config, ProviderSettingKeys.ClientId);
        var privateKey = Setting(config, ProviderSettingKeys.PrivateKeyBase64);
        var (success, message) = await gitHub.ValidateAppCredentialsAsync(apiUrl, clientId, installationId, privateKey, ct);
        if (!success)
            return TypedResults.BadRequest(message);

        var repositories = await gitHub.ListRepositoriesWithAppAsync(apiUrl, clientId, installationId, privateKey, ct);
        return TypedResults.Ok(repositories.Select(r => new UiRepository(r.FullName, r.Owner, r.Name)).ToList());
    }

    /// <summary>POST …/initialize — checks access and creates the agent labels; 400 with the reason when access fails.</summary>
    internal static async Task<IResult> Initialize(
        [FromBody] ProviderConfig draft, IProviderConfigStore store, IProviderFactory providers, CancellationToken ct)
    {
        var (config, error) = await ResolveAsync(draft, store, ct);
        if (config is null)
            return TypedResults.BadRequest(error);

        return await RunIssueProviderAsync(config, providers, async provider =>
        {
            var labelsCreated = await provider.InitializeAsync(ct);
            return new UiProviderCheck(labelsCreated, labelsCreated
                ? "Repository initialized successfully."
                : "Credentials validated but some labels could not be created.");
        });
    }

    /// <summary>GET …/{id}/agent-labels — whether the stored issue provider's repository has the agent labels.</summary>
    internal static async Task<IResult> HasAgentLabels(string id, IProviderConfigStore store, IProviderFactory providers, CancellationToken ct) =>
        await store.GetProviderConfigByIdAsync(id, ProviderKind.Issue, ct) is { } config
            ? await RunIssueProviderAsync(config, providers, async provider => await provider.HasAgentLabelsAsync(ct))
            : TypedResults.NotFound();

    /// <summary>POST …/{id}/agent-labels — creates the agent labels; true when all of them exist afterwards.</summary>
    internal static async Task<IResult> EnsureAgentLabels(string id, IProviderConfigStore store, IProviderFactory providers, CancellationToken ct) =>
        await store.GetProviderConfigByIdAsync(id, ProviderKind.Issue, ct) is { } config
            ? await RunIssueProviderAsync(config, providers, async provider => await provider.EnsureAgentLabelsAsync(ct))
            : TypedResults.NotFound();

    /// <summary>GET …/models — the models the agent CLI offers; 502 with the reason as a JSON string when the fetch fails.</summary>
    internal static async Task<IResult> GetModels(ModelFetchJobService fetchService, CancellationToken ct)
    {
        var (models, error) = await fetchService.FetchModelsAsync(JobTemplateProviderType.Kiro, ct);
        return error is null ? TypedResults.Ok(models) : TypedResults.Json(error, statusCode: StatusCodes.Status502BadGateway);
    }

    private const string InstallationIdMessage = "Installation ID must be a valid number.";

    private static async Task<(ProviderConfig? Config, string? Error)> ResolveAsync(ProviderConfig draft, IProviderConfigStore store, CancellationToken ct)
    {
        var stored = await store.GetProviderConfigByIdAsync(draft.Id, draft.Kind, ct);
        return UiSecretMask.Restore(draft, stored);
    }

    /// <summary>Runs <paramref name="operation"/> on a provider built from <paramref name="config"/>; a provider failure is a 400 with its message.</summary>
    private static async Task<IResult> RunIssueProviderAsync<T>(ProviderConfig config, IProviderFactory providers, Func<IIssueProvider, Task<T>> operation)
    {
        try
        {
            await using var provider = providers.CreateIssueProvider(config);
            return TypedResults.Ok(await operation(provider));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static bool TryGetInstallationId(ProviderConfig config, out long installationId) =>
        long.TryParse(config.Settings.GetValueOrDefault(ProviderSettingKeys.InstallationId), out installationId);

    private static string Setting(ProviderConfig config, string key) => config.Settings.GetValueOrDefault(key) ?? "";
}
