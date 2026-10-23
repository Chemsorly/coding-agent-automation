using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.Executors;

/// <summary>
/// Reports a consolidation run's progress the way implementation runs report theirs: each step the
/// run enters is sent to the orchestrator, so the run page's step ladder follows the run.
/// </summary>
public sealed class ConsolidationProgress
{
    private readonly Action<PipelineStep> _transitionTo;
    private readonly Lock _usageLock = new();

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

    /// <summary>Adds one agent call's tokens and cost to the run. Thread-safe: the scan's detectors run in parallel.</summary>
    public void AccumulateTokenUsage(AgentResult? result, string phase)
    {
        // TODO [WARNING]: `phase` has no ArgumentNullException.ThrowIfNull guard, deviating from the ThrowIfNull
        // pattern used by this class's constructor. A null phase is forwarded to Run.AccumulateTokenUsage(…, null).
        lock (_usageLock) Run.AccumulateTokenUsage(result, phase);
    }

    /// <summary>Adds tokens without a cost (review and refinement report only tokens). Thread-safe.</summary>
    public void AccumulateTokenUsage(TokenUsage? usage, string phase)
    {
        // TODO [WARNING]: `phase` has no ArgumentNullException.ThrowIfNull guard, deviating from the ThrowIfNull
        // pattern used by this class's constructor. A null phase is forwarded to Run.AccumulateTokenUsage(…, null).
        lock (_usageLock) Run.AccumulateTokenUsage(usage, phase);
    }

    /// <summary>Copies the run's token totals into the result reported to the orchestrator.</summary>
    public void CopyUsageTo(ConsolidationJobResult result)
    {
        // TODO [WARNING]: `result` has no ArgumentNullException.ThrowIfNull guard, deviating from the ThrowIfNull
        // pattern used by this class's constructor. A null argument surfaces as NullReferenceException inside
        // the lock body rather than as ArgumentNullException at method entry, obscuring the fault origin.
        lock (_usageLock)
        {
            result.TotalTokens = Run.TotalTokens;
            result.TotalCost = Run.TotalCost;
            result.CacheReadTokens = Run.CacheReadTokens;
            result.CacheWriteTokens = Run.CacheWriteTokens;
            result.PhaseBreakdown = LocalPipelineExecutor.BuildPhaseBreakdownPayload(Run.Metrics.PhaseBreakdown);
        }
    }

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
