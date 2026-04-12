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
    /// Constructs a prompt containing the issue title, description, and all acceptance criteria.
    /// </summary>
    public static string BuildPrompt(IssueDetail issue, ParsedIssue parsed)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

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
        }

        return sb.ToString().TrimEnd();
    }
}
