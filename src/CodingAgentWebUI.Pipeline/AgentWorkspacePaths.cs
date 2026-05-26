namespace CodingAgentWebUI.Pipeline;

/// <summary>
/// Centralized constants for workspace-relative paths used by the pipeline
/// to store agent metadata, analysis output, and quality gate results.
/// </summary>
public static class AgentWorkspacePaths
{
    /// <summary>
    /// The root metadata directory inside target workspaces.
    /// </summary>
    public const string MetadataDirectory = ".agent";

    /// <summary>
    /// The file path (relative to workspace) where the agent writes its analysis.
    /// </summary>
    public const string AnalysisFilePath = ".agent/analysis.md";

    /// <summary>
    /// The file path (relative to workspace) where the agent writes its structured assessment.
    /// </summary>
    public const string AnalysisAssessmentFilePath = ".agent/analysis-assessment.json";

    /// <summary>
    /// The file path (relative to workspace) where the pipeline writes consolidated
    /// review findings for the fix agent to read.
    /// </summary>
    public const string ReviewFindingsFilePath = ".agent/review-findings.md";

    /// <summary>
    /// The directory (relative to workspace) where quality gate output files are written.
    /// Each gate writes its stdout/stderr here; the agent discovers files by listing the directory.
    /// </summary>
    public const string QualityGatesOutputDirectory = ".agent/quality-gates";

    /// <summary>
    /// The file path (relative to workspace) where the pipeline writes issue context
    /// (description + comments) for the agent to read on demand.
    /// </summary>
    public const string IssueContextFilePath = ".agent/issue-context.md";

    /// <summary>
    /// The file path (relative to workspace) where the pipeline writes brain context
    /// for the agent to read on demand.
    /// </summary>
    public const string BrainContextFilePath = ".agent/brain-context.md";

    /// <summary>
    /// The file path (relative to workspace) where refactoring proposals are written.
    /// </summary>
    public const string RefactoringProposalsFilePath = ".agent/refactoring-proposals.json";

    /// <summary>
    /// The file path (relative to workspace) where the analysis review agent writes its feedback.
    /// </summary>
    public const string AnalysisReviewFilePath = ".agent/analysis-review.md";

    /// <summary>
    /// The file path (relative to workspace) where the pipeline writes the prompt input
    /// for file-reference-based prompt delivery.
    /// </summary>
    public const string PromptInputFilePath = ".agent/prompt-input.md";

    /// <summary>
    /// The default workspace-relative path for MCP server configuration.
    /// </summary>
    public const string DefaultMcpConfigPath = ".agent/settings/mcp.json";

    /// <summary>
    /// The file path (relative to workspace) where the pipeline writes the diff stat
    /// (file list with line counts) for review agents to triage changes efficiently.
    /// </summary>
    public const string DiffStatFilePath = ".agent/diff-stat.txt";

    /// <summary>
    /// The file path (relative to workspace) where the pipeline writes the full git diff
    /// for review agents to read selectively instead of running git diff themselves.
    /// </summary>
    public const string FullDiffFilePath = ".agent/full-diff.txt";

    /// <summary>
    /// The file path (relative to workspace) where the refactoring review agent writes its findings.
    /// </summary>
    public const string RefactoringReviewFilePath = ".agent/refactoring-review.md";

    /// <summary>
    /// The file path (relative to workspace) where the brain consolidation review agent writes its findings.
    /// </summary>
    public const string BrainConsolidationReviewFilePath = ".agent/brain-consolidation-review.md";

    /// <summary>
    /// The file path (relative to workspace) where the harness suggestions review agent writes its findings.
    /// </summary>
    public const string HarnessSuggestionsReviewFilePath = ".agent/harness-suggestions-review.md";

    /// <summary>
    /// The file path (relative to workspace) where the brain consolidation agent writes its diff summary.
    /// </summary>
    public const string BrainConsolidationDiffFilePath = ".agent/brain-consolidation-diff.md";

    /// <summary>
    /// The file path (relative to workspace) where the harness suggestion agent writes its output JSON.
    /// </summary>
    public const string HarnessSuggestionsOutputFilePath = ".agent/harness-suggestions-output.json";

    /// <summary>
    /// Directory for open issue context files (deduplication).
    /// </summary>
    public const string OpenIssuesDirectory = ".agent/open-issues";

    /// <summary>
    /// Directory for sub-issue JSON output files.
    /// </summary>
    public const string SubIssuesDirectory = ".agent/sub-issues";

    /// <summary>
    /// File path for the decomposition plan output.
    /// </summary>
    public const string DecompositionPlanFilePath = ".agent/decomposition-plan.md";

    /// <summary>
    /// File path for the decomposition adversarial review findings.
    /// </summary>
    public const string DecompositionReviewFilePath = ".agent/decomposition-review.md";

    /// <summary>
    /// Returns a per-agent findings file path to prevent sub-agent overwrite conflicts.
    /// Each review agent writes to its own isolated file.
    /// </summary>
    public static string GetReviewFindingsFilePath(string agentName)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        var sanitized = agentName.ToLowerInvariant()
            .Replace(' ', '-').Replace('/', '-').Replace('\\', '-');
        return $".agent/review-findings-{sanitized}.md";
    }
}
