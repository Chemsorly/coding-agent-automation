using System.Text;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Builds prompts for the agent from issue details and parsed issue data.
/// This is used by the orchestrator — the agent provider receives pre-built prompts.
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// The file path (relative to workspace) where the agent writes its analysis.
    /// </summary>
    public const string AnalysisFilePath = ".kiro/analysis.md";

    /// <summary>
    /// Constructs an analysis-only prompt. The agent examines the codebase in context of the
    /// issue and writes its recommendation to .kiro/analysis.md without making any other changes.
    /// The configurable analysis instructions are prepended, followed by pipeline mechanics.
    /// </summary>
    public static string BuildAnalysisPrompt(string analysisInstructions, IssueDetail issue, ParsedIssue parsed,
        IReadOnlyList<IssueComment>? comments = null)
    {
        ArgumentNullException.ThrowIfNull(analysisInstructions);
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

        // Configurable instructions
        sb.AppendLine(analysisInstructions);
        sb.AppendLine();

        // Pipeline mechanics (non-configurable)
        sb.AppendLine("Do NOT implement any changes. Only analyze and recommend.");
        sb.AppendLine();
        sb.AppendLine($"Write your analysis to the file `{AnalysisFilePath}` in the workspace. Do NOT print the analysis to stdout — only write it to that file.");
        sb.AppendLine();
        sb.AppendLine("Use sub-agents to cover more ground and provide a thorough analysis. For example, delegate parallel investigations to explore different parts of the codebase — one sub-agent could examine the data layer while another looks at the UI components, or one traces the call chain while another checks for test coverage gaps. This produces a more complete picture than a single-threaded read-through.");
        sb.AppendLine();

        sb.AppendLine($"# Issue: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine(issue.Description);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(parsed.RequirementsSection))
        {
            sb.AppendLine("## Requirements");
            sb.AppendLine(parsed.RequirementsSection);
            sb.AppendLine();
        }

        if (parsed.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("## Acceptance Criteria");
            foreach (var criterion in parsed.AcceptanceCriteria)
            {
                sb.AppendLine($"- {criterion}");
            }
            sb.AppendLine();
        }

        AppendComments(sb, comments);

        sb.AppendLine($"Analyze the workspace now and write your recommendation to `{AnalysisFilePath}`.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Constructs a prompt containing action instructions, the issue title, description,
    /// and all acceptance criteria. The prompt explicitly instructs the agent to implement
    /// the changes in the workspace, not just analyze them.
    /// The configurable implementation instructions are prepended, followed by pipeline mechanics.
    /// </summary>
    public static string BuildPrompt(string implementationInstructions, IssueDetail issue, ParsedIssue parsed,
        IReadOnlyList<IssueComment>? comments = null)
    {
        ArgumentNullException.ThrowIfNull(implementationInstructions);
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

        // Configurable instructions
        sb.AppendLine(implementationInstructions);
        sb.AppendLine();

        // Pipeline mechanics (non-configurable)
        sb.AppendLine("Do NOT run git write commands (git add, git commit, git push, git checkout, git reset, etc.). The pipeline handles all version control operations. Read-only git commands (git log, git diff, git status, git show) are fine.");
        sb.AppendLine($"The analysis for this issue is at `{AnalysisFilePath}` — read it before implementing.");
        sb.AppendLine();

        sb.AppendLine($"# Issue: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine(issue.Description);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(parsed.RequirementsSection))
        {
            sb.AppendLine("## Requirements");
            sb.AppendLine(parsed.RequirementsSection);
            sb.AppendLine();
        }

        if (parsed.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("## Acceptance Criteria");
            foreach (var criterion in parsed.AcceptanceCriteria)
            {
                sb.AppendLine($"- {criterion}");
            }
            sb.AppendLine();
        }

        AppendComments(sb, comments);

        sb.AppendLine("Implement these changes now.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Constructs a code review prompt that includes the original issue context so the
    /// reviewing agent does not rely solely on conversation history for requirements.
    /// The configurable review instructions are prepended, followed by the full issue
    /// details (title, description, requirements, acceptance criteria, and comments).
    /// </summary>
    public static string BuildReviewPrompt(string reviewInstructions, IssueDetail issue,
        ParsedIssue parsed, IReadOnlyList<IssueComment>? comments = null)
    {
        ArgumentNullException.ThrowIfNull(reviewInstructions);
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

        sb.AppendLine(reviewInstructions);
        sb.AppendLine();
        sb.AppendLine("Do NOT run git write commands (git add, git commit, git push, git checkout, git reset, etc.). The pipeline handles all version control operations. Read-only git commands are fine.");
        sb.AppendLine();
        sb.AppendLine("Below is the original issue for reference. Review the changes against these requirements.");
        sb.AppendLine();

        sb.AppendLine($"# Issue: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine(issue.Description);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(parsed.RequirementsSection))
        {
            sb.AppendLine("## Requirements");
            sb.AppendLine(parsed.RequirementsSection);
            sb.AppendLine();
        }

        if (parsed.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("## Acceptance Criteria");
            foreach (var criterion in parsed.AcceptanceCriteria)
            {
                sb.AppendLine($"- {criterion}");
            }
            sb.AppendLine();
        }

        AppendComments(sb, comments);

        return sb.ToString().TrimEnd();
    }

    /// <summary>Markers identifying bot-generated comments that should be excluded from context.</summary>
    internal static readonly string[] ExcludedCommentMarkers = ["## 🤖 Agent Analysis"];

    private static void AppendComments(StringBuilder sb, IReadOnlyList<IssueComment>? comments)
    {
        if (comments == null || comments.Count == 0)
            return;

        var filtered = comments
            .Where(c => !ExcludedCommentMarkers.Any(marker => c.Body.Contains(marker)))
            .TakeLast(10)
            .ToList();

        if (filtered.Count == 0)
            return;

        sb.AppendLine("## Comments");
        sb.AppendLine("The following comments were left on the issue and may contain clarifications, updated requirements, or stakeholder feedback.");
        sb.AppendLine();
        foreach (var comment in filtered)
        {
            sb.AppendLine($"**@{comment.Author}** ({comment.CreatedAt:yyyy-MM-dd}):");
            sb.AppendLine(comment.Body);
            sb.AppendLine();
        }
    }
}
