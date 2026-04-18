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
    /// </summary>
    public static string BuildAnalysisPrompt(IssueDetail issue, ParsedIssue parsed,
        IReadOnlyList<IssueComment>? comments = null)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

        sb.AppendLine("Analyze the codebase in context of the following issue. Examine the relevant source files and provide a concise recommendation.");
        sb.AppendLine("Do NOT implement any changes. Only analyze and recommend.");
        sb.AppendLine();
        sb.AppendLine("Use sub-agents to cover more ground and provide a thorough analysis. For example, delegate parallel investigations to explore different parts of the codebase — one sub-agent could examine the data layer while another looks at the UI components, or one traces the call chain while another checks for test coverage gaps. This produces a more complete picture than a single-threaded read-through.");
        sb.AppendLine();
        sb.AppendLine($"Write your analysis to the file `{AnalysisFilePath}` in the workspace. Do NOT print the analysis to stdout — only write it to that file.");
        sb.AppendLine();
        sb.AppendLine("The file should contain your analysis in this structure:");
        sb.AppendLine("1. **Planned Approach** — What files need to change and how. Be specific about the strategy.");
        sb.AppendLine("2. **Affected Components** — Which files, classes, or modules will be touched.");
        sb.AppendLine("3. **Estimated Complexity** — Low / Medium / High with a brief justification.");
        sb.AppendLine("4. **Risks & Considerations** — Anything that could go wrong or needs special attention.");
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
    /// </summary>
    public static string BuildPrompt(IssueDetail issue, ParsedIssue parsed,
        IReadOnlyList<IssueComment>? comments = null)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

        // Action instructions — concise, high-impact
        sb.AppendLine("Implement the following issue. Write the code — do not just analyze or plan. Keep your analysis brief and focus on making changes.");
        sb.AppendLine("If a file write is rejected, retry it immediately — it will succeed on the second attempt.");
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
