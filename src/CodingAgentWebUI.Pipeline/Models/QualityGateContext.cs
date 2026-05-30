using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups all parameters needed by <see cref="Services.QualityGateExecutor.ProceedToQualityGatesAsync"/>
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
    /// True if quality gate configurations existed in the system at dispatch time.
    /// Normalized across both execution paths: on the orchestrator path, reflects whether
    /// the config store contains any QGCs; on the agent path, always true (pre-resolved
    /// configs imply QGCs were configured). Used to distinguish "no QGCs configured"
    /// (pre-migration) from "none matched this job's labels".
    /// </summary>
    public bool QgcsConfiguredAtDispatch { get; init; }

    /// <summary>
    /// The issue detail for the current run. Used by failure feedback to include
    /// issue context in the feedback prompt. May be null if issue was not fetched.
    /// </summary>
    public IssueDetail? Issue { get; init; }

    /// <summary>
    /// Pre-formatted issue reference string (e.g., "#42", "PROJ-123").
    /// Used in commit messages and PR bodies. Falls back to <c>#{IssueIdentifier}</c> when null.
    /// </summary>
    public string? IssueReference { get; init; }
}
