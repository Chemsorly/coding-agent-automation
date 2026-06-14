namespace CodingAgentWebUI.Pipeline.Models;

// Evaluated: splitting formatting constants (MaxBranchNameLength, MaxCommentLength) into a
// separate FormattingConstants class is not warranted — the file contains only 5 domain
// constants at 25 lines. Revisit if the file grows significantly.

/// <summary>
/// Centralized domain constants for the pipeline. Avoids scattering magic numbers
/// across production and test code.
/// </summary>
public static class PipelineConstants
{
    /// <summary>Default page size for paginated API calls (issue listing).</summary>
    public const int DefaultPageSize = 100;

    /// <summary>Maximum pages to fetch for issue/PR comments (300 comments at 100/page).</summary>
    public const int MaxCommentPages = 3;

    /// <summary>Maximum pages to fetch for timeline events (200 events at 100/page).</summary>
    public const int MaxTimelineEventPages = 2;

    /// <summary>Maximum pages to fetch for PR reviews (100 reviews at 100/page).</summary>
    public const int MaxReviewPages = 1;

    /// <summary>Maximum pages to fetch for PR discussion/review comments (200 at 100/page).</summary>
    public const int MaxPrCommentPages = 2;

    /// <summary>Maximum total length of a generated branch name.</summary>
    public const int MaxBranchNameLength = 100;

    /// <summary>Maximum character length for a comment body in the PR description before truncation.</summary>
    public const int MaxCommentLength = 2000;

    /// <summary>Minimum length in bytes for analysis.md to be considered valid.</summary>
    public const int MinAnalysisLength = 100;

    /// <summary>Default capacity (line count) for the output ring buffer.</summary>
    public const int DefaultOutputBufferCapacity = 10_000;

    /// <summary>Default capacity for PipelineRun.OutputLines bounded queue.</summary>
    public const int DefaultOutputLinesCapacity = 5_000;

    /// <summary>Default capacity for PipelineRun.ChatHistory bounded queue.</summary>
    public const int DefaultChatHistoryCapacity = 200;

    /// <summary>Default capacity for PipelineRun.QualityGateHistory bounded queue.</summary>
    public const int DefaultQualityGateHistoryCapacity = 50;

    /// <summary>Default capacity for PipelineRun.RetryErrors bounded queue.</summary>
    public const int DefaultRetryErrorsCapacity = 100;

    /// <summary>Number of output lines to include in chat history summaries and log messages.</summary>
    public const int OutputTailLineCount = 10;

    /// <summary>Fallback text when agent produces no output.</summary>
    public const string NoOutputFallback = "(no output)";

    /// <summary>Branch name prefix for auto-generated feature branches.</summary>
    public const string BranchPrefix = "feature/auto-";

    /// <summary>Default commit message suffix for pipeline-generated commits.</summary>
    public const string AutomatedCommitSuffix = "Automated implementation via pipeline";

    /// <summary>Base directory for pipeline configuration files.</summary>
    public const string ConfigBaseDirectory = "config/pipeline";

    /// <summary>Directory for pipeline run history files.</summary>
    public const string RunsDirectory = "config/pipeline/runs";

    /// <summary>Directory for consolidation run files.</summary>
    public const string ConsolidationRunsDirectory = "config/pipeline/consolidation-runs";

    /// <summary>Path for harness suggestions file.</summary>
    public const string HarnessSuggestionsPath = "config/pipeline/harness-suggestions.json";

    /// <summary>
    /// Git restriction instruction appended to prompts (full version with read-only examples).
    /// </summary>
    public const string GitRestrictionFull =
        "Do NOT run git write commands (git add, git commit, git push, git checkout, git reset, etc.). " +
        "The pipeline handles all version control operations — it automatically stages and commits ALL new and modified files (including untracked files). " +
        "Read-only git commands (git log, git diff, git status, git show) are fine.";

    /// <summary>
    /// Git restriction instruction appended to prompts (short version for cleanup/retry).
    /// </summary>
    public const string GitRestrictionShort =
        "Do NOT run git write commands (git add, git commit, git push, etc.). " +
        "The pipeline handles version control automatically.";

    // ── TimeSpan defaults for sub-configurations ────────────────────────

    /// <summary>Default agent execution timeout (30 minutes).</summary>
    public static readonly TimeSpan DefaultAgentTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Default interval between stall warning checks (2 minutes).</summary>
    public static readonly TimeSpan DefaultStallWarningInterval = TimeSpan.FromMinutes(2);

    /// <summary>Default poll interval for stall detection (30 seconds).</summary>
    public static readonly TimeSpan DefaultStallPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Default timeout for external CI checks (15 minutes).</summary>
    public static readonly TimeSpan DefaultExternalCiTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Default poll interval for external CI status (30 seconds).</summary>
    public static readonly TimeSpan DefaultExternalCiPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Default poll interval for closed-loop issue polling (60 seconds).</summary>
    public static readonly TimeSpan DefaultClosedLoopPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>Default maximum backoff interval for closed-loop polling (15 minutes).</summary>
    public static readonly TimeSpan DefaultClosedLoopMaxBackoffInterval = TimeSpan.FromMinutes(15);

    /// <summary>Default grace period before marking a disconnected agent as lost (5 minutes).</summary>
    public static readonly TimeSpan DefaultAgentDisconnectGracePeriod = TimeSpan.FromMinutes(5);

    /// <summary>Default progress timeout for busy agents before marking them as stuck (60 minutes).</summary>
    public static readonly TimeSpan DefaultAgentBusyProgressTimeout = TimeSpan.FromMinutes(60);
}
