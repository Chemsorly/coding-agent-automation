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
    /// The file path (relative to workspace) where the prompt input is written
    /// for file-reference-based prompt delivery.
    /// </summary>
    public const string PromptInputFilePath = ".agent/prompt-input.md";

    /// <summary>
    /// The default workspace-relative path for MCP server configuration.
    /// </summary>
    public const string DefaultMcpConfigPath = ".agent/settings/mcp.json";

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
