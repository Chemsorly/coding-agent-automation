using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Tests.Helpers;

/// <summary>
/// Factory for creating PipelineConfiguration instances in tests with all properties
/// explicitly set. This prevents tests from silently breaking when production defaults change.
/// 
/// Use <see cref="NonAutonomous"/> for tests that expect the pipeline to pause at
/// WaitingForAnalysisApproval (most tests). Use <see cref="Autonomous"/> for tests
/// that expect the pipeline to run to completion without pausing.
/// </summary>
public static class TestPipelineConfig
{
    /// <summary>
    /// Creates a PipelineConfiguration for tests that expect non-autonomous pipeline flow
    /// (pauses at WaitingForAnalysisApproval and WaitingForChat).
    /// All properties are explicitly set to prevent default-change regressions.
    /// </summary>
    public static PipelineConfiguration NonAutonomous(string? workspaceBaseDirectory = null) => new()
    {
        MaxRetries = 3,
        IssuePageSize = 25,
        AgentTimeout = TimeSpan.FromMinutes(30),
        MinCoverageThreshold = 80.0,
        SecurityScanEnabled = false,
        WorkspaceBaseDirectory = workspaceBaseDirectory ?? Path.GetTempPath(),
        CodeReview = new CodeReviewConfiguration
        {
            Enabled = false,
            MaxIterations = 1,
            Prompt = PipelineConfiguration.DefaultCodeReviewPrompt,
            FixPrompt = null,
        },
        ExternalCiEnabled = false,
        ExternalCiTimeout = TimeSpan.FromMinutes(15),
        ExternalCiPollInterval = TimeSpan.FromSeconds(30),
        StallWarningInterval = TimeSpan.FromMinutes(2),
        StallPollInterval = TimeSpan.FromSeconds(30),
        AutonomousMode = false,
        BlacklistedPaths = new[] { ".kiro", ".github" },
        BlacklistMode = BlacklistMode.WarnAndExclude,
        CleanupSuccessfulWorkspaces = true,
        FailedWorkspaceRetentionDays = 7
    };

    /// <summary>
    /// Creates a PipelineConfiguration for tests that expect autonomous pipeline flow
    /// (runs to completion without pausing).
    /// All properties are explicitly set to prevent default-change regressions.
    /// </summary>
    public static PipelineConfiguration Autonomous(string? workspaceBaseDirectory = null) => new()
    {
        MaxRetries = 3,
        IssuePageSize = 25,
        AgentTimeout = TimeSpan.FromMinutes(30),
        MinCoverageThreshold = 80.0,
        SecurityScanEnabled = false,
        WorkspaceBaseDirectory = workspaceBaseDirectory ?? Path.GetTempPath(),
        CodeReview = new CodeReviewConfiguration
        {
            Enabled = true,
            MaxIterations = 1,
            Prompt = PipelineConfiguration.DefaultCodeReviewPrompt,
            FixPrompt = null,
        },
        ExternalCiEnabled = false,
        ExternalCiTimeout = TimeSpan.FromMinutes(15),
        ExternalCiPollInterval = TimeSpan.FromSeconds(30),
        StallWarningInterval = TimeSpan.FromMinutes(2),
        StallPollInterval = TimeSpan.FromSeconds(30),
        AutonomousMode = true,
        BlacklistedPaths = new[] { ".kiro", ".github" },
        BlacklistMode = BlacklistMode.WarnAndExclude,
        CleanupSuccessfulWorkspaces = true,
        FailedWorkspaceRetentionDays = 7
    };
}
