using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Implements <see cref="IDispatchRunCreator"/>: the dispatch path's dedup and active-run queries over
/// <see cref="PipelineRunLifecycleService"/>. Its run creation, which had no production caller and read the agent
/// provider config, was deleted with spec 053 (req 4.11): the API creates the run when it creates the work item.
/// </summary>
public class DispatchRunCreationService : IDispatchRunCreator
{
    private readonly PipelineRunLifecycleService _lifecycle;

    public DispatchRunCreationService(PipelineRunLifecycleService lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        _lifecycle = lifecycle;
    }

    /// <inheritdoc />
    public IReadOnlyList<PipelineRun> GetAllActiveRuns() => _lifecycle.GetAllActiveRuns();

    /// <inheritdoc />
    public bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId) =>
        _lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderConfigId);
}
