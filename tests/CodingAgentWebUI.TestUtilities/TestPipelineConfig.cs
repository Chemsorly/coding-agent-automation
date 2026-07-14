using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.TestUtilities;

/// <summary>
/// Factory for creating PipelineConfiguration instances in tests with all properties
/// explicitly set. This prevents tests from silently breaking when production defaults change.
/// 
/// Use <see cref="Default"/> for most tests. Use <see cref="WithCodeReview"/> for tests
/// that need code review enabled. Use <see cref="DefaultProject"/> for tests that need
/// a project with well-known Default ID.
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
        AnalysisReviewEnabled = false,
        RefactoringReviewEnabled = false,
        BrainConsolidationReviewEnabled = false,
        HarnessSuggestionsReviewEnabled = false,
        CodeReview = new CodeReviewConfiguration
        {
            MaxIterations = 2,
            FixPrompt = null,
        },
        ExternalCiTimeout = TimeSpan.FromMinutes(15),
        ExternalCiPollInterval = TimeSpan.FromSeconds(30),
        MaxInfrastructureRetries = 2,
        StallWarningInterval = TimeSpan.FromMinutes(2),
        StallPollInterval = TimeSpan.FromSeconds(30),
        BlacklistedPaths = new[] { ".agent", ".github" },
        FailedWorkspaceRetentionDays = 7,
        ClosedLoopPollInterval = TimeSpan.FromSeconds(60),
        ClosedLoopMaxRunsPerCycle = 0,
        ClosedLoopMaxConsecutivePollFailures = 5,
        ClosedLoopMaxBackoffInterval = TimeSpan.FromMinutes(15),
        ClosedLoopMaxPagesToFetch = 10,
        MaxRefactoringProposals = 3,
        HotspotAnalysisLookback = TimeSpan.FromDays(90),
        MaxDecompositionSubIssues = 10,
        MaxConcurrentDecompositions = 2,
        DecompositionTimeout = TimeSpan.FromMinutes(15),
        MaxOpenIssuesForContext = 50,
        RefactoringOutcomeLookback = TimeSpan.FromDays(90),
        OutputLinesCapacity = 5_000,
        ChatHistoryCapacity = 200,
        QualityGateHistoryCapacity = 50,
        RetryErrorsCapacity = 100,
        OrphanedLabelSweepIntervalMinutes = 30,
        AnalysisCommitThreshold = 30,
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
        AnalysisReviewEnabled = false,
        RefactoringReviewEnabled = false,
        BrainConsolidationReviewEnabled = false,
        HarnessSuggestionsReviewEnabled = false,
        CodeReview = new CodeReviewConfiguration
        {
            MaxIterations = 2,
            FixPrompt = null,
        },
        ExternalCiTimeout = TimeSpan.FromMinutes(15),
        ExternalCiPollInterval = TimeSpan.FromSeconds(30),
        MaxInfrastructureRetries = 2,
        StallWarningInterval = TimeSpan.FromMinutes(2),
        StallPollInterval = TimeSpan.FromSeconds(30),
        BlacklistedPaths = new[] { ".agent", ".github" },
        FailedWorkspaceRetentionDays = 7,
        ClosedLoopPollInterval = TimeSpan.FromSeconds(60),
        ClosedLoopMaxRunsPerCycle = 0,
        ClosedLoopMaxConsecutivePollFailures = 5,
        ClosedLoopMaxBackoffInterval = TimeSpan.FromMinutes(15),
        ClosedLoopMaxPagesToFetch = 10,
        MaxRefactoringProposals = 3,
        HotspotAnalysisLookback = TimeSpan.FromDays(90),
        MaxDecompositionSubIssues = 10,
        MaxConcurrentDecompositions = 2,
        DecompositionTimeout = TimeSpan.FromMinutes(15),
        MaxOpenIssuesForContext = 50,
        RefactoringOutcomeLookback = TimeSpan.FromDays(90),
        OutputLinesCapacity = 5_000,
        ChatHistoryCapacity = 200,
        QualityGateHistoryCapacity = 50,
        RetryErrorsCapacity = 100,
        OrphanedLabelSweepIntervalMinutes = 30,
        AnalysisCommitThreshold = 30,
    };

    /// <summary>
    /// Creates a <see cref="PipelineProject"/> with the well-known Default project ID.
    /// All overrides are null (inherit from global). Includes no templates by default.
    /// Use <c>with { TemplateIds = [...] }</c> to add template ownership.
    /// </summary>
    public static PipelineProject DefaultProject(params string[] templateIds) => new()
    {
        Id = WellKnownIds.DefaultProjectId,
        Name = "Default",
        Enabled = true,
        TemplateIds = templateIds,
    };

    /// <summary>
    /// Creates a <see cref="PipelineProject"/> with a generated ID and the given name.
    /// All overrides are null (inherit from global). Use <c>with { ... }</c> to set
    /// behavioral overrides for project-level settings tests.
    /// </summary>
    public static PipelineProject WithProject(
        string name = "TestProject",
        params string[] templateIds) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = name,
        Enabled = true,
        TemplateIds = templateIds,
    };
}
