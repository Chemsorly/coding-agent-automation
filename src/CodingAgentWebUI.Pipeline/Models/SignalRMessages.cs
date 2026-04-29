using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Agent → Orchestrator: Registration payload sent when an agent connects to the hub.
/// </summary>
[MessagePackObject]
public sealed record AgentRegistrationMessage
{
    [Key(0)]
    public required string AgentId { get; init; }

    [Key(1)]
    public required string Hostname { get; init; }

    [Key(2)]
    public required string AgentType { get; init; }

    [Key(3)]
    public required IReadOnlyList<string> Labels { get; init; }
}

/// <summary>
/// Agent → Orchestrator: Periodic heartbeat confirming the agent is alive.
/// </summary>
[MessagePackObject]
public sealed record HeartbeatMessage
{
    [Key(0)]
    public required string AgentId { get; init; }

    [Key(1)]
    public required DateTimeOffset Timestamp { get; init; }

    [Key(2)]
    public PipelineStep? CurrentStep { get; init; }

    [Key(3)]
    public long MemoryUsageMb { get; init; }
}

/// <summary>
/// Orchestrator → Agent: Job assignment containing all data needed to execute a pipeline run.
/// </summary>
[MessagePackObject]
public sealed record JobAssignmentMessage
{
    [Key(0)]
    public required string JobId { get; init; }

    [Key(1)]
    public required string IssueIdentifier { get; init; }

    [Key(2)]
    public required IssueDetail IssueDetail { get; init; }

    [Key(3)]
    public required ParsedIssue ParsedIssue { get; init; }

    /// <summary>Pre-fetched issue comments, capped at 50.</summary>
    [Key(4)]
    public required IReadOnlyList<IssueComment> IssueComments { get; init; }

    [Key(5)]
    public string? ExistingAnalysis { get; init; }

    [Key(6)]
    public bool ForceRefreshAnalysis { get; init; }

    [Key(7)]
    public LinkedPullRequest? LinkedPullRequest { get; init; }

    [Key(8)]
    public required string RepoProviderConfigId { get; init; }

    [Key(9)]
    public required string AgentProviderConfigId { get; init; }

    [Key(10)]
    public string? BrainProviderConfigId { get; init; }

    [Key(11)]
    public string? PipelineProviderConfigId { get; init; }

    /// <summary>Serialized provider configs (repository, agent, brain, pipeline — NO issue provider).</summary>
    [Key(12)]
    public required IReadOnlyList<ProviderConfig> ProviderConfigs { get; init; }

    [Key(13)]
    public required PipelineConfiguration PipelineConfiguration { get; init; }

    [Key(14)]
    public required string InitiatedBy { get; init; }

    [Key(15)]
    public string? ResolvedProfileId { get; init; }

    [Key(16)]
    public required IReadOnlyList<QualityGateConfiguration> QualityGateConfigs { get; init; }

    [Key(17)]
    public IReadOnlyList<McpServerConfig> McpServers { get; init; } = [];
}

/// <summary>
/// Agent → Orchestrator: Completion payload with full results of a pipeline run.
/// </summary>
[MessagePackObject]
public sealed record JobCompletionPayload
{
    [Key(0)]
    public required PipelineStep FinalStep { get; init; }

    [Key(1)]
    public string? FailureReason { get; init; }

    [Key(2)]
    public string? PullRequestUrl { get; init; }

    [Key(3)]
    public string? PullRequestNumber { get; init; }

    [Key(4)]
    public bool IsDraftPr { get; init; }

    [Key(5)]
    public int RetryCount { get; init; }

    [Key(6)]
    public required DateTimeOffset CompletedAt { get; init; }

    [Key(7)]
    public int FilesChangedCount { get; init; }

    [Key(8)]
    public int LinesAdded { get; init; }

    [Key(9)]
    public int LinesRemoved { get; init; }

    [Key(10)]
    public bool BrainUpdatesPushed { get; init; }

    [Key(11)]
    public string? AnalysisRecommendation { get; init; }

    [Key(12)]
    public bool IsRework { get; init; }

    [Key(13)]
    public IReadOnlyList<string> AnalysisConcerns { get; init; } = [];

    [Key(14)]
    public IReadOnlyList<string> AnalysisBlockingIssues { get; init; } = [];

    [Key(15)]
    public IReadOnlyList<string> BlacklistedFilesDetected { get; init; } = [];

    [Key(16)]
    public IReadOnlyList<string> CodeReviewAgentsRun { get; init; } = [];

    [Key(17)]
    public int CodeReviewCriticalCount { get; init; }

    [Key(18)]
    public int CodeReviewWarningCount { get; init; }

    [Key(19)]
    public int CodeReviewSuggestionCount { get; init; }
}

/// <summary>
/// Type of comment to post on the GitHub issue via the orchestrator.
/// </summary>
public enum CommentType
{
    Analysis,
    GateRejection,
    GateWontDo
}

/// <summary>
/// Agent → Orchestrator: Typed comment payload for issue comment posting.
/// Uses typed fields instead of byte[] to avoid double-serialization and enable log inspection.
/// </summary>
[MessagePackObject]
public sealed record CommentPayload
{
    [Key(0)]
    public string? AnalysisMarkdown { get; init; }

    [Key(1)]
    public string? AssessmentJson { get; init; }
}

/// <summary>
/// Orchestrator → Agent: Response to a token refresh request with a new short-lived token.
/// </summary>
[MessagePackObject]
public sealed record TokenRefreshResponse
{
    [Key(0)]
    public required string Token { get; init; }

    [Key(1)]
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Orchestrator → Agent: Chat prompt assignment for interactive MCP validation/debugging.
/// Lighter than <see cref="JobAssignmentMessage"/> — no issue, no pipeline steps.
/// </summary>
[MessagePackObject]
public sealed record ChatPromptMessage
{
    /// <summary>Unique session ID for correlating responses back to this chat.</summary>
    [Key(0)]
    public required string SessionId { get; init; }

    /// <summary>The user's prompt text.</summary>
    [Key(1)]
    public required string Prompt { get; init; }

    /// <summary>Whether to use --resume (multi-turn continuation).</summary>
    [Key(2)]
    public bool UseResume { get; init; }

    /// <summary>MCP servers to write to the workspace before execution.</summary>
    [Key(3)]
    public IReadOnlyList<McpServerConfig> McpServers { get; init; } = [];

    /// <summary>
    /// Absolute path where MCP config should be written.
    /// Resolved from the agent provider config's "mcpConfigPath" setting.
    /// Examples: "/home/ubuntu/.kiro/settings/mcp.json" (Kiro CLI global),
    /// "/home/ubuntu/.claude.json" (Claude CLI).
    /// </summary>
    [Key(4)]
    public string McpConfigPath { get; init; } = "/home/ubuntu/.kiro/settings/mcp.json";
}

/// <summary>
/// Agent → Orchestrator: Streamed chat response lines during interactive chat.
/// </summary>
[MessagePackObject]
public sealed record ChatResponseMessage
{
    [Key(0)]
    public required string SessionId { get; init; }

    [Key(1)]
    public required IReadOnlyList<string> Lines { get; init; }
}

/// <summary>
/// Agent → Orchestrator: Signals that the chat prompt execution has completed.
/// </summary>
[MessagePackObject]
public sealed record ChatCompletedMessage
{
    [Key(0)]
    public required string SessionId { get; init; }

    [Key(1)]
    public required int ExitCode { get; init; }

    [Key(2)]
    public string? Error { get; init; }
}
