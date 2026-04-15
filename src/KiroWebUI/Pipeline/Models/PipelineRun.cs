using System.Collections.Concurrent;

namespace KiroWebUI.Pipeline.Models;

public sealed class PipelineRun
{
    public required string RunId { get; init; }
    public required string IssueIdentifier { get; init; }
    public required string IssueTitle { get; set; }
    public required string IssueProviderConfigId { get; init; }
    public required string RepoProviderConfigId { get; init; }
    public PipelineStep CurrentStep { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public string? WorkspacePath { get; set; }
    public string? BranchName { get; set; }
    public string? FailureReason { get; set; }
    public string? PullRequestUrl { get; set; }
    public int RetryCount { get; set; }

    /// <summary>Agent-generated analysis content, populated during AnalyzingCode step.</summary>
    public string? AnalysisContent { get; set; }

    /// <summary>Thread-safe collections — mutated by orchestration service while UI reads via OnChange.</summary>
    public ConcurrentBag<string> RetryErrors { get; init; } = new();
    public ConcurrentQueue<ChatEntry> ChatHistory { get; init; } = new();
    public QualityGateReport? LatestQualityReport { get; set; }
    public ConcurrentQueue<string> OutputLines { get; init; } = new();
}
