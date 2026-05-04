using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Helpers;

/// <summary>
/// Factory for creating PipelineConfiguration instances in tests with all properties
/// explicitly set. This prevents tests from silently breaking when production defaults change.
/// 
/// Use <see cref="Default"/> for most tests. Use <see cref="WithCodeReview"/> for tests
/// that need code review enabled.
/// </summary>
public static class TestPipelineConfig
{
    /// <summary>
    /// Creates a default PipelineConfiguration with code review disabled.
    /// All properties are explicitly set to prevent default-change regressions.
    /// </summary>
    public static PipelineConfiguration Default(string? workspaceBaseDirectory = null) => new()
    {
        Retry = new RetryConfiguration
        {
            MaxRetries = 3,
            MaxAnalysisRetries = 1,
            AgentTimeout = TimeSpan.FromMinutes(30),
            StallWarningInterval = TimeSpan.FromMinutes(2),
            StallPollInterval = TimeSpan.FromSeconds(30),
        },
        IssuePageSize = 25,
        Workspace = new WorkspaceConfiguration
        {
            WorkspaceBaseDirectory = workspaceBaseDirectory ?? Path.GetTempPath(),
            FailedWorkspaceRetentionDays = 7,
        },
        CodeReview = new CodeReviewConfiguration
        {
            Enabled = false,
            MaxIterations = 2,
            Prompt = PipelineConfiguration.DefaultCodeReviewPrompt,
            FixPrompt = null,
        },
        ExternalCi = new ExternalCiConfiguration
        {
            Enabled = false,
            Timeout = TimeSpan.FromMinutes(15),
            PollInterval = TimeSpan.FromSeconds(30),
        },
        Commit = new CommitConfiguration
        {
            BlacklistedPaths = new[] { ".kiro", ".github" },
            BlacklistMode = BlacklistMode.WarnAndExclude,
        },
        ClosedLoop = new ClosedLoopConfiguration
        {
            PollInterval = TimeSpan.FromSeconds(60),
            MaxRunsPerCycle = 0,
            MaxConsecutivePollFailures = 5,
            MaxBackoffInterval = TimeSpan.FromMinutes(15),
            MaxPagesToFetch = 10,
        },
    };

    /// <summary>
    /// Creates a PipelineConfiguration with code review enabled.
    /// All properties are explicitly set to prevent default-change regressions.
    /// </summary>
    public static PipelineConfiguration WithCodeReview(string? workspaceBaseDirectory = null) => new()
    {
        Retry = new RetryConfiguration
        {
            MaxRetries = 3,
            MaxAnalysisRetries = 1,
            AgentTimeout = TimeSpan.FromMinutes(30),
            StallWarningInterval = TimeSpan.FromMinutes(2),
            StallPollInterval = TimeSpan.FromSeconds(30),
        },
        IssuePageSize = 25,
        Workspace = new WorkspaceConfiguration
        {
            WorkspaceBaseDirectory = workspaceBaseDirectory ?? Path.GetTempPath(),
            FailedWorkspaceRetentionDays = 7,
        },
        CodeReview = new CodeReviewConfiguration
        {
            Enabled = true,
            MaxIterations = 2,
            Prompt = PipelineConfiguration.DefaultCodeReviewPrompt,
            FixPrompt = null,
        },
        ExternalCi = new ExternalCiConfiguration
        {
            Enabled = false,
            Timeout = TimeSpan.FromMinutes(15),
            PollInterval = TimeSpan.FromSeconds(30),
        },
        Commit = new CommitConfiguration
        {
            BlacklistedPaths = new[] { ".kiro", ".github" },
            BlacklistMode = BlacklistMode.WarnAndExclude,
        },
        ClosedLoop = new ClosedLoopConfiguration
        {
            PollInterval = TimeSpan.FromSeconds(60),
            MaxRunsPerCycle = 0,
            MaxConsecutivePollFailures = 5,
            MaxBackoffInterval = TimeSpan.FromMinutes(15),
            MaxPagesToFetch = 10,
        },
    };
}
