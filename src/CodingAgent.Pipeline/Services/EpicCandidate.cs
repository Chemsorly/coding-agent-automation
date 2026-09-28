using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// An epic waiting for decomposition, with the tracker it lives in. The run is bound to that tracker;
/// when it is its project's epic tracker, the epic is a project epic.
/// </summary>
internal readonly record struct EpicCandidate(IssueSummary Issue, PipelineRunType Phase, string IssueProviderId)
    : IHasCreatedAt
{
    public DateTime? CreatedAt => Issue.CreatedAt;
}
