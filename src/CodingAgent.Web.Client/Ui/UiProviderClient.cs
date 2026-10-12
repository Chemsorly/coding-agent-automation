using System.Net.Http.Json;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Ui;

/// <summary>
/// Provider configs and provider operations in Settings, admin only (spec 051 2.1c). Credentials come
/// back as "****". Operations take the form as a draft: the API fills masked values from the stored
/// config with the same ID, as long as the API URL is unchanged, and calls GitHub or GitLab itself.
/// A refusal throws <see cref="InvalidOperationException"/> with the API's reason.
/// </summary>
public interface IUiProviderClient
{
    Task<IReadOnlyList<ProviderConfig>> GetProviderConfigsAsync(ProviderKind kind, CancellationToken ct = default);

    /// <summary>Saves the config; "****" keeps a stored credential.</summary>
    Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct = default);

    /// <summary>Saves a new config whose "****" credentials are those of the stored provider <paramref name="sourceId"/>.</summary>
    Task CopyProviderConfigAsync(ProviderConfig config, string sourceId, ProviderKind sourceKind, CancellationToken ct = default);

    Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct = default);

    /// <summary>Whether the credentials work, and the repository or project when the draft names one.</summary>
    Task<UiProviderCheck> CheckAsync(ProviderConfig draft, CancellationToken ct = default);

    /// <summary>Whether the GitHub App can read the repository's workflow runs.</summary>
    Task<UiProviderCheck> CheckActionsAccessAsync(ProviderConfig draft, CancellationToken ct = default);

    /// <summary>The repositories the GitHub App installation can access; throws when the credentials fail.</summary>
    Task<IReadOnlyList<UiRepository>> ListRepositoriesAsync(ProviderConfig draft, CancellationToken ct = default);

    /// <summary>Checks access and creates the agent labels; throws when access fails.</summary>
    Task<UiProviderCheck> InitializeAsync(ProviderConfig draft, CancellationToken ct = default);

    Task<bool> HasAgentLabelsAsync(string issueProviderId, CancellationToken ct = default);

    Task<bool> EnsureAgentLabelsAsync(string issueProviderId, CancellationToken ct = default);

    /// <summary>The models the agent CLI offers, or the reason they could not be fetched.</summary>
    Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> GetModelsAsync(CancellationToken ct = default);
}

public sealed class UiProviderClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiProviderClient
{
    private const string Providers = "/api/ui/settings/provider-configs";

    public Task<IReadOnlyList<ProviderConfig>> GetProviderConfigsAsync(ProviderKind kind, CancellationToken ct = default) =>
        GetAsync($"{Providers}?kind={kind}", UiJsonContext.Default.IReadOnlyListProviderConfig, ct);

    public Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Put, Providers, config, UiJsonContext.Default.ProviderConfig, ct);

    public Task CopyProviderConfigAsync(ProviderConfig config, string sourceId, ProviderKind sourceKind, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Put, $"{Providers}?copyFrom={Uri.EscapeDataString(sourceId)}&copyFromKind={sourceKind}",
            config, UiJsonContext.Default.ProviderConfig, ct);

    public Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"{Providers}/{Uri.EscapeDataString(id)}?kind={kind}", ct);

    public Task<UiProviderCheck> CheckAsync(ProviderConfig draft, CancellationToken ct = default) =>
        PostDraftAsync("check", draft, UiJsonContext.Default.UiProviderCheck, ct);

    public Task<UiProviderCheck> CheckActionsAccessAsync(ProviderConfig draft, CancellationToken ct = default) =>
        PostDraftAsync("actions-access", draft, UiJsonContext.Default.UiProviderCheck, ct);

    public Task<IReadOnlyList<UiRepository>> ListRepositoriesAsync(ProviderConfig draft, CancellationToken ct = default) =>
        PostDraftAsync("repositories", draft, UiJsonContext.Default.IReadOnlyListUiRepository, ct);

    public Task<UiProviderCheck> InitializeAsync(ProviderConfig draft, CancellationToken ct = default) =>
        PostDraftAsync("initialize", draft, UiJsonContext.Default.UiProviderCheck, ct);

    public Task<bool> HasAgentLabelsAsync(string issueProviderId, CancellationToken ct = default) =>
        SendForResultAsync(HttpMethod.Get, $"{Providers}/{Uri.EscapeDataString(issueProviderId)}/agent-labels", null, UiJsonContext.Default.Boolean, ct);

    public Task<bool> EnsureAgentLabelsAsync(string issueProviderId, CancellationToken ct = default) =>
        SendForResultAsync(HttpMethod.Post, $"{Providers}/{Uri.EscapeDataString(issueProviderId)}/agent-labels", null, UiJsonContext.Default.Boolean, ct);

    public async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> GetModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/ui/settings/models");
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return ([], await ReadReasonAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync(UiJsonContext.Default.IReadOnlyListAgentModelInfo, ct) ?? [], null);
    }

    private Task<T> PostDraftAsync<T>(string operation, ProviderConfig draft, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> result, CancellationToken ct) =>
        SendForResultAsync(HttpMethod.Post, $"{Providers}/{operation}", JsonContent.Create(draft, UiJsonContext.Default.ProviderConfig), result, ct);
}
