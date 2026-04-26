using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.IntegrationTests.Helpers;

/// <summary>
/// Factory for creating PipelineConfiguration instances in tests with all properties
/// explicitly set. This prevents tests from silently breaking when production defaults change.
/// </summary>
public static class TestPipelineConfig
{
    /// <summary>
    /// Creates a default PipelineConfiguration with code review disabled.
    /// All properties are explicitly set to prevent default-change regressions.
    /// </summary>
    public static PipelineConfiguration Default(string? workspaceBaseDirectory = null) => new()
    {
        MaxRetries = 3,
        MaxAnalysisRetries = 1,
        IssuePageSize = 25,
        AgentTimeout = TimeSpan.FromMinutes(30),
        MinCoverageThreshold = 50.0,
        SecurityScanEnabled = false,
        WorkspaceBaseDirectory = workspaceBaseDirectory ?? Path.GetTempPath(),
        CodeReview = new CodeReviewConfiguration
        {
            Enabled = false,
            MaxIterations = 2,
            Prompt = PipelineConfiguration.DefaultCodeReviewPrompt,
            FixPrompt = null,
            Agents = null,
        },
        ExternalCiEnabled = false,
        ExternalCiTimeout = TimeSpan.FromMinutes(15),
        ExternalCiPollInterval = TimeSpan.FromSeconds(30),
        StallWarningInterval = TimeSpan.FromMinutes(2),
        StallPollInterval = TimeSpan.FromSeconds(30),
        BlacklistedPaths = new[] { ".kiro", ".github" },
        BlacklistMode = BlacklistMode.WarnAndExclude,
        CleanupSuccessfulWorkspaces = true,
        FailedWorkspaceRetentionDays = 7,
        ClosedLoopPollInterval = TimeSpan.FromSeconds(60),
        ClosedLoopMaxRunsPerCycle = 0,
        ClosedLoopMaxConsecutivePollFailures = 5,
        ClosedLoopMaxBackoffInterval = TimeSpan.FromMinutes(15),
        // TODO: [REF-01] Add ClosedLoopMaxPagesToFetch = 10 — omitted from explicit property list (review finding #1)
    };

    /// <summary>
    /// Creates a PipelineConfiguration with code review enabled.
    /// All properties are explicitly set to prevent default-change regressions.
    /// </summary>
    public static PipelineConfiguration WithCodeReview(string? workspaceBaseDirectory = null) => new()
    {
        MaxRetries = 3,
        MaxAnalysisRetries = 1,
        IssuePageSize = 25,
        AgentTimeout = TimeSpan.FromMinutes(30),
        MinCoverageThreshold = 50.0,
        SecurityScanEnabled = false,
        WorkspaceBaseDirectory = workspaceBaseDirectory ?? Path.GetTempPath(),
        CodeReview = new CodeReviewConfiguration
        {
            Enabled = true,
            MaxIterations = 2,
            Prompt = PipelineConfiguration.DefaultCodeReviewPrompt,
            FixPrompt = null,
            Agents = null,
        },
        ExternalCiEnabled = false,
        ExternalCiTimeout = TimeSpan.FromMinutes(15),
        ExternalCiPollInterval = TimeSpan.FromSeconds(30),
        StallWarningInterval = TimeSpan.FromMinutes(2),
        StallPollInterval = TimeSpan.FromSeconds(30),
        BlacklistedPaths = new[] { ".kiro", ".github" },
        BlacklistMode = BlacklistMode.WarnAndExclude,
        CleanupSuccessfulWorkspaces = true,
        FailedWorkspaceRetentionDays = 7,
        ClosedLoopPollInterval = TimeSpan.FromSeconds(60),
        ClosedLoopMaxRunsPerCycle = 0,
        ClosedLoopMaxConsecutivePollFailures = 5,
        ClosedLoopMaxBackoffInterval = TimeSpan.FromMinutes(15),
        // TODO: [REF-01] Add ClosedLoopMaxPagesToFetch = 10 — omitted from explicit property list (review finding #1)
    };
}
