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
    /// Constructs a prompt containing action instructions, the issue title, description,
    /// and all acceptance criteria. The prompt explicitly instructs the agent to implement
    /// the changes in the workspace, not just analyze them.
    /// </summary>
    public static string BuildPrompt(IssueDetail issue, ParsedIssue parsed)
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

        sb.AppendLine("Implement these changes now.");

        return sb.ToString().TrimEnd();
    }
}
