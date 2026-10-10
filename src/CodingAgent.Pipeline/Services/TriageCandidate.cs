using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// An <c>agent:triage</c> issue waiting for a triage run, with the tracker it lives in. The run is bound to that
/// tracker and reads every enabled repository of its project, wherever the issue was reported.
/// </summary>
internal readonly record struct TriageCandidate(IssueSummary Issue, string IssueProviderId) : IHasCreatedAt
{
    public DateTime? CreatedAt => Issue.CreatedAt;
}
