using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// Client for the Pipeline API's triage endpoints (<c>/api/triages</c>). Operator tier: the caller checks the
/// signed-in user's role on the triage's project first.
/// </summary>
public interface IPipelineApiTriageClient
{
    Task<TriageListPage> ListAsync(TriageListQuery query, CancellationToken ct = default);

    /// <summary>The triage, or null when it does not exist.</summary>
    Task<TriageDetail?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The id of the triage a run belongs to, or null.</summary>
    Task<Guid?> FindByRunAsync(string runId, CancellationToken ct = default);

    Task<IReadOnlyList<TriageListItem>> FindSimilarAsync(string projectId, string title, CancellationToken ct = default);

    /// <exception cref="TriageApiException">When the API refuses the request.</exception>
    Task<TriageDetail> CreateAsync(CreateTriageRequest request, CancellationToken ct = default);

    /// <exception cref="TriageApiException">When the API refuses the request.</exception>
    Task<TriageDetail> RerunAsync(Guid id, RerunTriageRequest request, CancellationToken ct = default);

    /// <exception cref="TriageApiException">When the API refuses the request.</exception>
    Task<TriageDetail> UpdateDraftAsync(Guid id, string draftId, UpdateTriageDraftRequest request, CancellationToken ct = default);

    /// <exception cref="TriageApiException">When the API refuses the request.</exception>
    Task<TriageDetail> ResetDraftAsync(Guid id, string draftId, CancellationToken ct = default);

    /// <exception cref="TriageApiException">When the API refuses the request.</exception>
    Task<CreateTriageIssuesResult> CreateIssuesAsync(Guid id, CreateTriageIssuesRequest request, CancellationToken ct = default);

    /// <exception cref="TriageApiException">When the API refuses the request.</exception>
    Task<TriageDetail> DismissAsync(Guid id, DismissTriageRequest request, CancellationToken ct = default);
}

/// <summary>A triage request the API refused, with its reason for the user.</summary>
public sealed class TriageApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
