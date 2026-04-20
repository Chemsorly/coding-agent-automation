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
    public const string DefaultSelfReviewPrompt =
        "Use a sub-agent to review the changes you just made against the original issue requirements. " +
        "The sub-agent should check for: correctness against acceptance criteria, code quality and " +
        "project conventions, unhandled edge cases, and security gaps. Fix any issues the review finds.";

    public int MaxRetries { get; init; } = 3;
    public int IssuePageSize { get; init; } = 25;
    public TimeSpan AgentTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public double MinCoverageThreshold { get; init; } = 80.0;
    public bool SecurityScanEnabled { get; init; } = false;
    public string WorkspaceBaseDirectory { get; init; } = "./workspaces";
    public bool SelfReviewEnabled { get; init; } = false;
    public int SelfReviewMaxIterations { get; init; } = 1;
    public string SelfReviewPrompt { get; init; } = DefaultSelfReviewPrompt;
    public bool ExternalCiEnabled { get; init; } = false;
    public TimeSpan ExternalCiTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ExternalCiPollInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// How long the agent can be silent (no output) before the stall monitor logs a warning.
    /// The warning resets after each occurrence so it fires again after another interval of silence.
    /// </summary>
    public TimeSpan StallWarningInterval { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>
    /// How often the stall monitor polls <see cref="IAgentProvider.GetHealthStatus"/>.
    /// Default is 30 seconds. Tests can set a shorter interval for faster execution.
    /// </summary>
    public TimeSpan StallPollInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// When enabled, the pipeline skips the analysis approval and chat interaction pauses,
    /// proceeding directly to quality gates and PR creation.
    /// </summary>
    public bool AutonomousMode { get; init; } = false;
    public IReadOnlyList<string> BlacklistedPaths { get; init; } = new[] { ".kiro", ".github" };
    public BlacklistMode BlacklistMode { get; init; } = BlacklistMode.WarnAndExclude;
}
