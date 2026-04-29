using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups all parameters needed by <see cref="Services.QualityGateOrchestrator.ProceedToQualityGatesAsync"/>
/// into a single context object, reducing the method's parameter count.
/// </summary>
public sealed record QualityGateContext
{
    public required PipelineRun Run { get; init; }
    public required PipelineConfiguration Config { get; init; }
    public required IAgentProvider AgentProvider { get; init; }
    public required IRepositoryProvider RepoProvider { get; init; }
    public IPipelineProvider? PipelineProvider { get; init; }
    public CancellationTokenSource? OrchestratorCts { get; init; }
    public required Action<PipelineStep> TransitionTo { get; init; }
    public required IAgentIssueOperations IssueOps { get; init; }
    public required Func<string, CancellationToken, Task> RemoveAllAgentLabels { get; init; }
    public required Action<PipelineRun> AddRunToHistory { get; init; }
    public required Action<string> OnOutputLine { get; init; }
    public required Action OnChange { get; init; }
    public required Func<PipelineRun, QualityGateReport, bool, CancellationToken, Task> CreatePullRequest { get; init; }

    /// <summary>
    /// The resolved Quality Gate Configurations for this job.
    /// When non-empty, the new multi-QGC ValidateAsync overload is used.
    /// </summary>
    public IReadOnlyList<QualityGateConfiguration> QualityGateConfigs { get; init; } = [];

    /// <summary>
    /// True if any QGCs existed in the configuration store at dispatch time.
    /// Used to distinguish "no QGCs configured" (pre-migration) from "none matched this job".
    /// </summary>
    public bool QgcsConfiguredAtDispatch { get; init; }
}
