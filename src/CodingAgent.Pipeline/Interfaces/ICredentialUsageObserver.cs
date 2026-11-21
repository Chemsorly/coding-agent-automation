using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Receives the subscription usage an agent call reported while its run goes on (hub event
/// <see cref="PipelineRunEventKind.RateLimitReading"/>). Stores the readings of the run's credential and pauses the
/// credential until the window resets when a window reaches its usage threshold; the run itself is not touched.
/// </summary>
public interface ICredentialUsageObserver
{
    /// <summary>Handles <paramref name="readings"/> of work item <paramref name="workItemId"/>'s agent call.</summary>
    Task ObserveAsync(Guid workItemId, IReadOnlyList<AgentRateLimitObservation> readings, CancellationToken ct);

    /// <summary>
    /// Handles <paramref name="readings"/> of an agent call billed to <paramref name="credential"/> that belongs to no
    /// work item (a chat prompt).
    /// </summary>
    Task ObserveCredentialAsync(string credential, IReadOnlyList<AgentRateLimitObservation> readings, CancellationToken ct);
}
