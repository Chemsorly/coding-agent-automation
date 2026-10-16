using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.Executors;

/// <summary>
/// Reports a consolidation run's progress the way implementation runs report theirs: each step the
/// run enters is sent to the orchestrator, so the run page's step ladder follows the run.
/// </summary>
public sealed class ConsolidationProgress
{
    private readonly Action<PipelineStep> _transitionTo;

    public ConsolidationProgress(PipelineRun run, Action<PipelineStep> transitionTo)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(transitionTo);
        Run = run;
        _transitionTo = transitionTo;
    }

    /// <summary>The agent-side run that the reported steps are recorded on.</summary>
    public PipelineRun Run { get; }

    /// <summary>Reports that the run entered <paramref name="step"/>.</summary>
    public void TransitionTo(PipelineStep step) => _transitionTo(step);

    /// <summary>
    /// Creates the agent-side run of a consolidation job. A consolidation job has no issue; the
    /// orchestrator's run holds the real identifier, so the job ID stands in for it here.
    /// </summary>
    public static PipelineRun CreateRun(ConsolidationJobMessage job) =>
        PipelineRun.CreateForRunType(new PipelineRunCreationParams
        {
            RunId = job.JobId,
            IssueIdentifier = new IssueIdentifier(job.JobId),
            IssueTitle = job.Type.ToString(),
            IssueProviderConfigId = string.Empty,
            RepoProviderConfigId = string.Empty,
            RunType = PipelineRunType.Consolidation,
        });
}
