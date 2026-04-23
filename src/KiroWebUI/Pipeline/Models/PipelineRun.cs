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

    /// <summary>Number of code review iterations completed during the ReviewingCode step.</summary>
    public int CodeReviewIterationsCompleted { get; set; }

    /// <summary>Current code review iteration in progress (1-based), 0 when not reviewing.</summary>
    public int CodeReviewIterationInProgress { get; set; }

    /// <summary>Total code review iterations configured for this run.</summary>
    public int CodeReviewIterationsTotal { get; set; }

    /// <summary>Number of [CRITICAL] findings detected across all review iterations. Use Interlocked for thread-safe updates.</summary>
    public int CodeReviewCriticalCount;

    /// <summary>Number of [WARNING] findings detected across all review iterations. Use Interlocked for thread-safe updates.</summary>
    public int CodeReviewWarningCount;

    /// <summary>Number of [SUGGESTION] findings detected across all review iterations. Use Interlocked for thread-safe updates.</summary>
    public int CodeReviewSuggestionCount;

    /// <summary>Raw findings text from the review step, for inclusion in PR descriptions.</summary>
    public string? CodeReviewRawFindings { get; set; }

    /// <summary>Thread-safe collections — mutated by orchestration service while UI reads via OnChange.</summary>
    public ConcurrentBag<string> RetryErrors { get; init; } = new();
    public ConcurrentQueue<ChatEntry> ChatHistory { get; init; } = new();
    public QualityGateReport? LatestQualityReport { get; set; }
    public ConcurrentQueue<string> OutputLines { get; init; } = new();

    /// <summary>Issue labels, populated when the issue is fetched.</summary>
    public IReadOnlyList<string> IssueLabels { get; set; } = Array.Empty<string>();

    /// <summary>History of quality gate reports across retry attempts.</summary>
    public ConcurrentQueue<QualityGateReport> QualityGateHistory { get; init; } = new();

    /// <summary>Whether existing analysis was reused (skipped agent analysis).</summary>
    public bool AnalysisSkipped { get; set; }

    /// <summary>Whether the PR is a draft (quality gates failed after max retries).</summary>
    public bool IsDraftPr { get; set; }

    /// <summary>Repository display name (owner/repo).</summary>
    public string? RepositoryName { get; set; }

    /// <summary>Number of files changed during code generation, updated after agent execution.</summary>
    public int FilesChangedCount { get; set; }

    /// <summary>Lines added during code generation.</summary>
    public int LinesAdded { get; set; }

    /// <summary>Lines removed during code generation.</summary>
    public int LinesRemoved { get; set; }

    /// <summary>PR number extracted from the PR URL (e.g. "47").</summary>
    public string? PullRequestNumber { get; set; }

    /// <summary>Files excluded from the commit due to blacklist rules (from GIT-04).</summary>
    public IReadOnlyList<string> BlacklistedFilesDetected { get; set; } = Array.Empty<string>();

    /// <summary>Model configured for the agent provider used in this run (e.g. "auto", "claude-sonnet-4.6").</summary>
    public string? ModelName { get; set; }

    /// <summary>Names of review agents that were executed during this run.</summary>
    public IReadOnlyList<string> CodeReviewAgentsRun { get; set; } = Array.Empty<string>();
}
