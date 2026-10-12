using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Ui;

/// <summary>
/// What the pages browse in the providers (spec 051 2.3b). The API calls the providers with the
/// stored credentials. Lists without a project need global read; a scoped user's pages always pass one.
/// </summary>
public interface IUiBrowseClient
{
    /// <summary>The open issues of the enabled templates' issue providers, each with its dispatch readiness.</summary>
    Task<BacklogResult> GetBacklogAsync(string? projectId, CancellationToken ct = default);
}

public sealed class UiBrowseClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiBrowseClient
{
    public Task<BacklogResult> GetBacklogAsync(string? projectId, CancellationToken ct = default) =>
        GetAsync(string.IsNullOrEmpty(projectId) ? "/api/ui/backlog" : $"/api/ui/backlog?projectId={Uri.EscapeDataString(projectId)}",
            UiJsonContext.Default.BacklogResult, ct);
}
