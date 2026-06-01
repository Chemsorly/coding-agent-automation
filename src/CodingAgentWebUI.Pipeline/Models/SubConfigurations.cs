namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Retry and timeout settings for agent execution.
/// </summary>
public sealed record RetryConfiguration
{
    public int MaxRetries { get; init; } = 3;
    public int MaxAnalysisRetries { get; init; } = 2;
    public TimeSpan AgentTimeout { get; init; } = PipelineConstants.DefaultAgentTimeout;
    public TimeSpan StallWarningInterval { get; init; } = PipelineConstants.DefaultStallWarningInterval;
    public TimeSpan StallPollInterval { get; init; } = PipelineConstants.DefaultStallPollInterval;
}

/// <summary>
/// Workspace directory and retention settings.
/// </summary>
public sealed record WorkspaceConfiguration
{
    public string WorkspaceBaseDirectory { get; init; } = "./workspaces";
    public int FailedWorkspaceRetentionDays { get; init; } = 7;
}

/// <summary>
/// External CI integration settings.
/// </summary>
public sealed record ExternalCiConfiguration
{
    public TimeSpan ExternalCiTimeout { get; init; } = PipelineConstants.DefaultExternalCiTimeout;
    public TimeSpan ExternalCiPollInterval { get; init; } = PipelineConstants.DefaultExternalCiPollInterval;
    public int MaxInfrastructureRetries { get; init; } = 2; // TODO: Add range validation (e.g., 0–10) consistent with other config properties
}

/// <summary>
/// Closed-loop polling settings for autonomous issue processing.
/// </summary>
public sealed record ClosedLoopConfiguration
{
    public TimeSpan ClosedLoopPollInterval { get; init; } = PipelineConstants.DefaultClosedLoopPollInterval;
    public int ClosedLoopMaxRunsPerCycle { get; init; } = 0;

    public int ClosedLoopMaxConsecutivePollFailures
    {
        get => _closedLoopMaxConsecutivePollFailures;
        init => _closedLoopMaxConsecutivePollFailures = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ClosedLoopMaxConsecutivePollFailures), value, "Value must be at least 1.");
    }
    private readonly int _closedLoopMaxConsecutivePollFailures = 5;

    public TimeSpan ClosedLoopMaxBackoffInterval { get; init; } = PipelineConstants.DefaultClosedLoopMaxBackoffInterval;

    public int ClosedLoopMaxPagesToFetch
    {
        get => _closedLoopMaxPagesToFetch;
        init => _closedLoopMaxPagesToFetch = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ClosedLoopMaxPagesToFetch), value, "Value must be at least 1.");
    }
    private readonly int _closedLoopMaxPagesToFetch = 10;
}

/// <summary>
/// Multi-agent orchestration settings.
/// </summary>
public sealed record AgentConfiguration
{
    public string? DefaultRequiredAgentLabels { get; init; }
    public TimeSpan AgentDisconnectGracePeriod { get; init; } = PipelineConstants.DefaultAgentDisconnectGracePeriod;
    public int OutputBufferCapacity { get; init; } = PipelineConstants.DefaultOutputBufferCapacity;
    public int BrainPushMaxRetries { get; init; } = 3;
    public bool BrainReadOnly { get; init; } = false;
}

/// <summary>
/// Commit blacklist and enforcement settings.
/// </summary>
public sealed record CommitConfiguration
{
    public IReadOnlyList<string> BlacklistedPaths { get; init; } = new[] { ".agent", ".github", ".brain" };
    public BlacklistMode BlacklistMode { get; init; } = BlacklistMode.WarnAndExclude;
}
