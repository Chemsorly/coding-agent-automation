namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Retry and timeout settings for agent execution.
/// </summary>
public sealed record RetryConfiguration
{
    public int MaxRetries { get; init; } = 3;
    public int MaxAnalysisRetries { get; init; } = 2;
    public TimeSpan AgentTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan StallWarningInterval { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan StallPollInterval { get; init; } = TimeSpan.FromSeconds(30);
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
    public bool ExternalCiEnabled { get; init; } = false;
    public TimeSpan ExternalCiTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ExternalCiPollInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Closed-loop polling settings for autonomous issue processing.
/// </summary>
public sealed record ClosedLoopConfiguration
{
    public TimeSpan ClosedLoopPollInterval { get; init; } = TimeSpan.FromSeconds(60);
    public int ClosedLoopMaxRunsPerCycle { get; init; } = 0;

    public int ClosedLoopMaxConsecutivePollFailures
    {
        get => _closedLoopMaxConsecutivePollFailures;
        init => _closedLoopMaxConsecutivePollFailures = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ClosedLoopMaxConsecutivePollFailures), value, "Value must be at least 1.");
    }
    private readonly int _closedLoopMaxConsecutivePollFailures = 5;

    public TimeSpan ClosedLoopMaxBackoffInterval { get; init; } = TimeSpan.FromMinutes(15);

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
    public TimeSpan AgentDisconnectGracePeriod { get; init; } = TimeSpan.FromMinutes(5);
    public int OutputBufferCapacity { get; init; } = PipelineConstants.DefaultOutputBufferCapacity;
    public int BrainPushMaxRetries { get; init; } = 3;
    public bool BrainReadOnly { get; init; } = false;
}

/// <summary>
/// Commit blacklist and enforcement settings.
/// </summary>
public sealed record CommitConfiguration
{
    public IReadOnlyList<string> BlacklistedPaths { get; init; } = new[] { ".kiro", ".github", ".brain" };
    public BlacklistMode BlacklistMode { get; init; } = BlacklistMode.WarnAndExclude;
}
