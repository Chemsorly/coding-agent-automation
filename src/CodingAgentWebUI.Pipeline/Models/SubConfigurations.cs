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
    public int MaxInfrastructureRetries
    {
        get => _maxInfrastructureRetries;
        init => _maxInfrastructureRetries = value is >= 0 and <= 10
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxInfrastructureRetries), value, "Value must be between 0 and 10.");
    }
    private readonly int _maxInfrastructureRetries = 5;
}

/// <summary>
/// Closed-loop polling settings for autonomous issue processing.
/// </summary>
public sealed record ClosedLoopConfiguration
{
    /// <summary>
    /// When true, the pipeline loop starts automatically on application startup.
    /// Set to true when user starts the loop, false when user stops it.
    /// </summary>
    public bool AutoStart { get; init; }

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

    /// <summary>
    /// Cooldown duration before the circuit breaker auto-resumes polling.
    /// After this period the loop resets failure counters and retries. Default: 5 minutes.
    /// Must be at least 1 second.
    /// </summary>
    public TimeSpan ClosedLoopCircuitBreakerCooldown
    {
        get => _closedLoopCircuitBreakerCooldown;
        init => _closedLoopCircuitBreakerCooldown = value >= TimeSpan.FromSeconds(1)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ClosedLoopCircuitBreakerCooldown), value, "Value must be at least 1 second.");
    }
    private readonly TimeSpan _closedLoopCircuitBreakerCooldown = PipelineConstants.DefaultClosedLoopCircuitBreakerCooldown;

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
    public TimeSpan AgentBusyProgressTimeout { get; init; } = PipelineConstants.DefaultAgentBusyProgressTimeout;
    public int OutputBufferCapacity { get; init; } = PipelineConstants.DefaultOutputBufferCapacity;
    public int OutputLinesCapacity { get; init; } = PipelineConstants.DefaultOutputLinesCapacity;
    public int ChatHistoryCapacity { get; init; } = PipelineConstants.DefaultChatHistoryCapacity;
    public int QualityGateHistoryCapacity { get; init; } = PipelineConstants.DefaultQualityGateHistoryCapacity;
    public int RetryErrorsCapacity { get; init; } = PipelineConstants.DefaultRetryErrorsCapacity;
    public int BrainPushMaxRetries { get; init; } = 3;
    public bool BrainReadOnly { get; init; } = false;
    public int HeartbeatSweepIntervalSeconds { get; init; } = PipelineConstants.DefaultHeartbeatSweepIntervalSeconds;
    public int HeartbeatTimeoutSeconds { get; init; } = PipelineConstants.DefaultHeartbeatTimeoutSeconds;
}

/// <summary>
/// Commit blacklist and enforcement settings.
/// </summary>
public sealed record CommitConfiguration
{
    public IReadOnlyList<string> BlacklistedPaths { get; init; } = new[] { ".agent", ".brain" };
}


