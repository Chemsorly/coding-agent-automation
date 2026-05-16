using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.IntegrationTests.Helpers;

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
        WorkspaceBaseDirectory = workspaceBaseDirectory ?? Path.GetTempPath(),
        CodeReview = new CodeReviewConfiguration
        {
            MaxIterations = 2,
            FixPrompt = null,
        },
        ExternalCiTimeout = TimeSpan.FromMinutes(15),
        ExternalCiPollInterval = TimeSpan.FromSeconds(30),
        StallWarningInterval = TimeSpan.FromMinutes(2),
        StallPollInterval = TimeSpan.FromSeconds(30),
        BlacklistedPaths = new[] { ".agent", ".github" },
        BlacklistMode = BlacklistMode.WarnAndExclude,
        FailedWorkspaceRetentionDays = 7,
        ClosedLoopPollInterval = TimeSpan.FromSeconds(60),
        ClosedLoopMaxRunsPerCycle = 0,
        ClosedLoopMaxConsecutivePollFailures = 5,
        ClosedLoopMaxBackoffInterval = TimeSpan.FromMinutes(15),
        ClosedLoopMaxPagesToFetch = 10,
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
        WorkspaceBaseDirectory = workspaceBaseDirectory ?? Path.GetTempPath(),
        CodeReview = new CodeReviewConfiguration
        {
            MaxIterations = 2,
            FixPrompt = null,
        },
        ExternalCiTimeout = TimeSpan.FromMinutes(15),
        ExternalCiPollInterval = TimeSpan.FromSeconds(30),
        StallWarningInterval = TimeSpan.FromMinutes(2),
        StallPollInterval = TimeSpan.FromSeconds(30),
        BlacklistedPaths = new[] { ".agent", ".github" },
        BlacklistMode = BlacklistMode.WarnAndExclude,
        FailedWorkspaceRetentionDays = 7,
        ClosedLoopPollInterval = TimeSpan.FromSeconds(60),
        ClosedLoopMaxRunsPerCycle = 0,
        ClosedLoopMaxConsecutivePollFailures = 5,
        ClosedLoopMaxBackoffInterval = TimeSpan.FromMinutes(15),
        ClosedLoopMaxPagesToFetch = 10,
    };
}
