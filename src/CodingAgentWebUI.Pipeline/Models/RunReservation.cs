namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents a reserved run slot — an ID has been allocated and the dedup guard is active,
/// but no fully-constructed <see cref="PipelineRun"/> has been registered yet.
/// Returned by <see cref="Interfaces.IDispatchRunCreator.ReserveRunIdAsync"/> and consumed
/// by dispatch methods that construct the final run with full metadata before calling
/// <see cref="Services.PipelineRunLifecycleService.RegisterReservedRun"/>.
/// </summary>
public sealed record RunReservation(
    string RunId,
    string RepositoryName,
    string ModelName,
    DateTimeOffset StartedAt);
