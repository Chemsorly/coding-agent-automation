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

    /// <summary>Per-agent findings accumulated across all review iterations.</summary>
    // TODO: [ARC-10] Should be ConcurrentDictionary for thread safety — written by orchestration, read by UI concurrently
    public Dictionary<string, string> CodeReviewAgentFindings { get; } = new();

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
    // TODO: [ARC-10] Setter allows mutable List<string> assignment behind IReadOnlyList interface
    public IReadOnlyList<string> BlacklistedFilesDetected { get; set; } = Array.Empty<string>();

    /// <summary>Model configured for the agent provider used in this run (e.g. "auto", "claude-sonnet-4.6").</summary>
    public string? ModelName { get; set; }

    /// <summary>Names of review agents that were executed during this run.</summary>
    public IReadOnlyList<string> CodeReviewAgentsRun { get; set; } = Array.Empty<string>();

    /// <summary>Brain repository provider config ID, or null if no brain repo selected.</summary>
    public string? BrainProviderConfigId { get; init; }

    /// <summary>Whether brain context was successfully loaded during pre-run sync.</summary>
    public bool BrainContextLoaded { get; set; }

    /// <summary>Number of knowledge files available in the .brain/ directory after sync.</summary>
    public int BrainKnowledgeFileCount { get; set; }

    /// <summary>Whether post-run brain updates were pushed successfully.</summary>
    public bool BrainUpdatesPushed { get; set; }

    /// <summary>Number of brain files committed during post-run sync.</summary>
    public int BrainFilesCommitted { get; set; }

    /// <summary>Brain update validation result from BrainUpdateService.</summary>
    public BrainValidationResult? BrainValidation { get; set; }

    /// <summary>How this run was initiated: "manual" or "loop".</summary>
    public string InitiatedBy { get; init; } = "manual";

    /// <summary>Creates a <see cref="PipelineRunSummary"/> from this run's current state.</summary>
    // TODO: [ARC-10] FinalStep = CurrentStep without terminal state guard — edge case if called before TransitionTo completes
    public PipelineRunSummary ToSummary() => new()
    {
        RunId = RunId,
        IssueIdentifier = IssueIdentifier,
        IssueTitle = IssueTitle,
        FinalStep = CurrentStep,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        RetryCount = RetryCount,
        PullRequestUrl = PullRequestUrl,
        ModelName = ModelName,
        BrainRepoUsed = BrainProviderConfigId != null,
        BrainUpdatesPushed = BrainUpdatesPushed,
        InitiatedBy = InitiatedBy
    };
}
