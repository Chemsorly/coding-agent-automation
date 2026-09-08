namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Describes what the pipeline actually did with the branch when a run started.
/// Orthogonal to <c>InitiatedBy</c>, which records who/what triggered the dispatch.
/// </summary>
/// <remarks>
/// Set by <c>DetectReworkStep</c> during agent execution, after the repository is
/// queried for existing agent pull requests on the issue.
/// </remarks>
public enum RunMode
{
    /// <summary>
    /// No prior agent PR existed — a fresh branch was created.
    /// </summary>
    New = 0,

    /// <summary>
    /// Only draft agent PR(s) existed — drafts were closed and a fresh branch was created.
    /// Indicates a previous attempt was abandoned or failed before producing a reviewable PR.
    /// </summary>
    Retry = 1,

    /// <summary>
    /// A non-draft open agent PR existed — the existing branch was checked out and rebased.
    /// The agent is continuing work on a PR that was already under review.
    /// </summary>
    Rework = 2
}
