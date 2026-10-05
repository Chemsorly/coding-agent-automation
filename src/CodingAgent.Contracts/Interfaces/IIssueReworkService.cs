using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Encapsulates conflict-rework label mutation logic: for each conflicted PR whose branch has
/// no active run, extracts linked issues and swaps eligible issue labels to <c>agent:next</c>
/// to trigger a rework dispatch run.
/// </summary>
public interface IIssueReworkService
{
    /// <summary>
    /// For each conflicted PR in <see cref="ConflictReworkRequest.Sorted"/>, swaps the linked issue's
    /// label to <c>agent:next</c> to trigger a rework dispatch run — unless the PR's branch has an
    /// active run or active-run data was unavailable.
    /// </summary>
    /// <param name="request">
    /// The ordered PR candidates, their mergeability, the active-run branches (and whether that data
    /// was available), the providers used to swap labels, and the telemetry tag.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task TriggerConflictReworkAsync(ConflictReworkRequest request, CancellationToken ct);
}
