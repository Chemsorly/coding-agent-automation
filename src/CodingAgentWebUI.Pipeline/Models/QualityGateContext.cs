using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups all parameters needed by <see cref="Services.QualityGateOrchestrator.ProceedToQualityGatesAsync"/>
/// into a single context object.
/// </summary>
public sealed record QualityGateContext : PipelineContextBase
{
    public required IRepositoryProvider RepoProvider { get; init; }
    public IPipelineProvider? PipelineProvider { get; init; }

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
