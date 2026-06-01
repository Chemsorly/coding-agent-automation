using MessagePack;

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

/// <summary>
/// Configuration for quarantining known-flaky tests to prevent them from consuming retry budget.
/// </summary>
[MessagePackObject]
public sealed record TestQuarantineConfiguration
{
    [Key(0)]
    public bool Enabled { get; init; } = false;
    [Key(1)]
    public IReadOnlyList<QuarantinedTest> QuarantinedTests { get; init; } = [];
    [Key(2)]
    public int MaxQuarantinedFailuresPerRun { get; init; } = 5;
}

/// <summary>
/// A single quarantined test entry.
/// </summary>
[MessagePackObject]
public sealed record QuarantinedTest
{
    [Key(0)]
    public required string TestName { get; init; }
    [Key(1)]
    public required string Reason { get; init; }
    [Key(2)]
    public required DateTime QuarantinedAt { get; init; }
    [Key(3)]
    public DateTime? ExpiresAt { get; init; }
    [Key(4)]
    public IReadOnlyList<string>? AssociatedSourceFiles { get; init; }
}
