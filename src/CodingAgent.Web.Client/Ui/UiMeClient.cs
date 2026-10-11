using CodingAgent.Contracts.Auth;

namespace CodingAgent.Web.Ui;

/// <summary><c>GET /api/ui/me</c>: the user as the Pipeline API sees them, with project names resolved.</summary>
public interface IUiMeClient
{
    Task<UiMeResponse> GetAsync(CancellationToken ct = default);
}

public sealed class UiMeClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiMeClient
{
    public Task<UiMeResponse> GetAsync(CancellationToken ct = default) =>
        GetAsync("/api/ui/me", UiJsonContext.Default.UiMeResponse, ct);
}
