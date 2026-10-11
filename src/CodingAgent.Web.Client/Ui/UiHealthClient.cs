using CodingAgent.Contracts.Ui;

namespace CodingAgent.Web.Ui;

/// <summary><c>GET /api/ui/health</c>: database and Redis as the Pipeline API sees them.</summary>
public interface IUiHealthClient
{
    Task<UiHealthResponse> GetAsync(CancellationToken ct = default);
}

public sealed class UiHealthClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiHealthClient
{
    public Task<UiHealthResponse> GetAsync(CancellationToken ct = default) =>
        GetAsync("/api/ui/health", UiJsonContext.Default.UiHealthResponse, ct);
}
