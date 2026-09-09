namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Transient HTTP response DTO for <c>GET /api/agents</c>.
///
/// <para>
/// Flattens all <see cref="AgentEntry"/> fields into a single record and adds enrichment
/// fields populated from the active <c>PipelineRun</c> (when available). This allows
/// the Fleet UI to display issue, run, and PR links without additional per-row API calls.
/// </para>
///
/// <para>
/// This type is <b>not</b> stored in Redis and must not carry <c>[MessagePackObject]</c>
/// or <c>[Key]</c> attributes. It is serialized exclusively via <c>PipelineJsonOptions.Default</c>
/// (camelCase, enum-as-string) for the <c>/api/agents</c> HTTP response.
/// </para>
/// </summary>
public sealed record AgentEntryDto
{
    // ── AgentEntry fields (flattened) ─────────────────────────────────────────

    public required AgentId AgentId { get; init; }
    public required string ConnectionId { get; init; }
    public required string Hostname { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
    public AgentStatus Status { get; init; }
    public string? ActiveJobId { get; init; }
    public string? ActiveChatSessionId { get; init; }
    public required DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastHeartbeatAt { get; init; }
    public DateTimeOffset? LastJobCompletedAt { get; init; }
    public DateTimeOffset? DisconnectedAt { get; init; }
    public bool Disabled { get; init; }
    public DateTimeOffset? OrphanRestoredAt { get; init; }
    public DateTimeOffset? BusySince { get; init; }

    // ── Enrichment fields from the active PipelineRun ─────────────────────────

    /// <summary>
    /// Full composite issue identifier (e.g. <c>owner/repo#123</c>), or null when the agent
    /// is idle or the active run is not found. Follows the same format as
    /// <c>PipelineRun.IssueIdentifier</c>.
    /// </summary>
    public string? ActiveIssueIdentifier { get; init; }

    /// <summary>Issue title from the active run, or null when not available.</summary>
    public string? ActiveIssueTitle { get; init; }

    /// <summary>
    /// Web URL of the issue on the provider (e.g. GitHub issue URL), or null when not available.
    /// <para>
    /// TODO: Validate that this starts with <c>https://</c> before rendering as <c>href</c>.
    /// Blazor HTML-encodes attribute values but does NOT strip <c>javascript:</c> URI schemes,
    /// so a malicious/corrupted record could produce a clickable XSS link.
    /// </para>
    /// </summary>
    public string? ActiveIssueUrl { get; init; }

    /// <summary>
    /// Run ID of the active pipeline run, for constructing <c>/runs/{id}</c> navigation links.
    /// Null when the agent is idle or the run is not found.
    /// </summary>
    public string? ActiveRunId { get; init; }

    /// <summary>
    /// Pull request URL when a PR has been created for the active run, or null when no PR exists yet.
    /// <para>
    /// TODO: Validate that this starts with <c>https://</c> before rendering as <c>href</c>.
    /// Same <c>javascript:</c> URI injection risk as <see cref="ActiveIssueUrl"/>.
    /// </para>
    /// </summary>
    public string? ActivePullRequestUrl { get; init; }
}
