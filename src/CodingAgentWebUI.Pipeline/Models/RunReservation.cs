namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Lightweight reservation token returned by <see cref="Interfaces.IDispatchRunCreator.ReserveRunIdAsync"/>.
/// Contains the allocated RunId and essential metadata resolved during reservation (repository name,
/// model name) so that dispatch consumers can construct a fully-populated <see cref="PipelineRun"/>
/// without re-resolving provider configs.
/// </summary>
public sealed record RunReservation(
    string RunId,
    string RepositoryName,
    string ModelName,
    DateTimeOffset StartedAt);
