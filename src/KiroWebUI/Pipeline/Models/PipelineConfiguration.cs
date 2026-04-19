namespace KiroWebUI.Pipeline.Models;

/// <summary>
/// Controls how blacklisted path violations are handled during commits.
/// </summary>
public enum BlacklistMode
{
    /// <summary>Unstage blacklisted files, log a warning, and continue the pipeline.</summary>
    WarnAndExclude,

    /// <summary>Fail the pipeline with a clear error listing the violating files.</summary>
    Fail
}

public sealed class PipelineConfiguration
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan AgentTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public double MinCoverageThreshold { get; init; } = 80.0;
    public bool SecurityScanEnabled { get; init; } = false;
    public string WorkspaceBaseDirectory { get; init; } = "./workspaces";
    public bool SelfReviewEnabled { get; init; } = false;
    public int SelfReviewMaxIterations { get; init; } = 1;
    public string SelfReviewPrompt { get; init; } =
        "Use a sub-agent to review the changes you just made against the original issue requirements. " +
        "The sub-agent should check for: correctness against acceptance criteria, code quality and " +
        "project conventions, unhandled edge cases, and security gaps. Fix any issues the review finds.";
    public bool ExternalCiEnabled { get; init; } = false;
    public TimeSpan ExternalCiTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ExternalCiPollInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// When enabled, the pipeline skips the analysis approval and chat interaction pauses,
    /// proceeding directly to quality gates and PR creation.
    /// </summary>
    public bool AutonomousMode { get; init; } = false;
    public IReadOnlyList<string> BlacklistedPaths { get; init; } = new[] { ".kiro", ".github" };
    public BlacklistMode BlacklistMode { get; init; } = BlacklistMode.WarnAndExclude;
}
