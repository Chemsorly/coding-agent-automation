using System.Text;

namespace KiroWebUI.Pipeline.Models;

public sealed class IssueAnalysisComment
{
    public required string IssueTitle { get; init; }
    public required string PlannedApproach { get; init; }
    public required IReadOnlyList<string> AffectedComponents { get; init; }
    public required string EstimatedComplexity { get; init; }
    public required string ConfidenceAssessment { get; init; }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🤖 Agent Analysis");
        sb.AppendLine();
        sb.AppendLine("### Planned Approach");
        sb.AppendLine(PlannedApproach);
        sb.AppendLine();
        sb.AppendLine("### Affected Components");
        foreach (var component in AffectedComponents)
            sb.AppendLine($"- {component}");
        sb.AppendLine();
        sb.AppendLine("### Estimated Complexity");
        sb.AppendLine(EstimatedComplexity);
        sb.AppendLine();
        sb.AppendLine("### Confidence Assessment");
        sb.AppendLine(ConfidenceAssessment);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*This analysis was posted automatically before implementation began. If the approach looks wrong, comment on this issue to course-correct.*");
        return sb.ToString().TrimEnd();
    }

    public static IssueAnalysisComment FromIssue(IssueDetail issue, ParsedIssue parsed)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var hasRequirements = !string.IsNullOrWhiteSpace(parsed.RequirementsSection);
        var hasCriteria = parsed.AcceptanceCriteria.Count > 0;

        var approach = hasRequirements
            ? $"Implement changes as described in the issue requirements for **{issue.Title}**."
            : $"Implement **{issue.Title}** based on the issue description.";

        var components = new List<string>();
        if (issue.Labels.Count > 0)
            components.AddRange(issue.Labels.Select(l => $"`{l}` (label)"));
        components.Add("Files to be determined during implementation");

        var complexity = hasCriteria
            ? parsed.AcceptanceCriteria.Count switch
            {
                <= 2 => "Low — few acceptance criteria",
                <= 5 => "Medium — moderate acceptance criteria",
                _ => "High — many acceptance criteria"
            }
            : "Medium — no explicit acceptance criteria to gauge scope";

        var confidence = (hasRequirements, hasCriteria) switch
        {
            (true, true) => "High — clear requirements and acceptance criteria provided",
            (true, false) => "Medium — requirements provided but no explicit acceptance criteria",
            (false, true) => "Medium — acceptance criteria provided but requirements section missing",
            _ => "Low — limited structured information in the issue"
        };

        return new IssueAnalysisComment
        {
            IssueTitle = issue.Title,
            PlannedApproach = approach,
            AffectedComponents = components.AsReadOnly(),
            EstimatedComplexity = complexity,
            ConfidenceAssessment = confidence
        };
    }
}
