using CodingAgent.Pipeline.Models;
using CodingAgent.Contracts.Ui;

namespace CodingAgent.Web.Ui;

/// <summary>
/// Spec 056: the agent credentials and their pauses. Every user may read them (the Work and Fleet banners); pausing and
/// resuming needs global operator.
/// </summary>
public interface IUiCredentialsClient
{
    Task<UiAgentCredentialsPage> GetAsync(CancellationToken ct = default);

    /// <summary>Pauses <paramref name="credential"/> until <paramref name="until"/> (null: until resumed).</summary>
    Task PauseAsync(string credential, DateTimeOffset? until, string? note, CancellationToken ct = default);

    Task ResumeAsync(string credential, CancellationToken ct = default);

    /// <summary>Replaces the usage thresholds of the Claude subscription (admin).</summary>
    Task SaveUsageThresholdsAsync(CredentialUsageThresholds thresholds, CancellationToken ct = default);
}

public sealed class UiCredentialsClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiCredentialsClient
{
    private const string Credentials = "/api/ui/agent-credentials";

    public Task<UiAgentCredentialsPage> GetAsync(CancellationToken ct = default) =>
        GetAsync(Credentials, UiJsonContext.Default.UiAgentCredentialsPage, ct);

    public Task PauseAsync(string credential, DateTimeOffset? until, string? note, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Post, $"{Credentials}/{Uri.EscapeDataString(credential)}/pause",
            new UiCredentialPauseRequest(until, note), UiJsonContext.Default.UiCredentialPauseRequest, ct);

    public Task ResumeAsync(string credential, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{Credentials}/{Uri.EscapeDataString(credential)}/resume", ct);

    public Task SaveUsageThresholdsAsync(CredentialUsageThresholds thresholds, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Put, $"{Credentials}/usage-thresholds", thresholds,
            UiJsonContext.Default.CredentialUsageThresholds, ct);
}
