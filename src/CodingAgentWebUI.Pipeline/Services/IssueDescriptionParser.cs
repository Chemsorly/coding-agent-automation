using System.Text;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Parses structured requirements and acceptance criteria from markdown issue descriptions,
/// and formats parsed issues into structured prompt sections for agent consumption.
/// </summary>
public partial class IssueDescriptionParser
{
    // Matches markdown headings like ## Requirements, ## Description
    [GeneratedRegex(@"^##\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    // Matches checkbox list items: - [ ] item or - [x] item
    [GeneratedRegex(@"^-\s+\[[ xX]\]\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex CheckboxPattern();

    // Matches numbered list items: 1. item, 2. item, etc.
    [GeneratedRegex(@"^\d+\.\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex NumberedListPattern();

    private static readonly string[] RequirementHeadings =
        ["requirements", "description"];

    private static readonly string[] AcceptanceCriteriaHeadings =
        ["acceptance criteria", "acceptance_criteria"];

    /// <summary>
    /// Parses an issue description to extract a requirements section and acceptance criteria.
    /// </summary>
    public ParsedIssue Parse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return new ParsedIssue
            {
                RequirementsSection = string.Empty,
                AcceptanceCriteria = Array.Empty<string>()
            };
        }

        var sections = ExtractSections(description);

        var requirementsSection = FindSection(sections, RequirementHeadings);
        var acceptanceCriteriaSection = FindSection(sections, AcceptanceCriteriaHeadings);

        // Extract list items from the acceptance criteria section (checkbox or numbered)
        var acceptanceCriteria = ExtractListItems(acceptanceCriteriaSection);

        // If no items found in the AC section, try extracting checkboxes from the whole description
        if (acceptanceCriteria.Count == 0 && string.IsNullOrWhiteSpace(acceptanceCriteriaSection))
        {
            acceptanceCriteria = ExtractCheckboxItems(description);
        }

        // Determine requirements text
        string requirements;
        if (!string.IsNullOrWhiteSpace(requirementsSection))
        {
            requirements = requirementsSection.Trim();
        }
        else if (sections.Count == 0)
        {
            // No recognizable structure — treat entire description as requirements
            // but strip out any checkbox items that were extracted as acceptance criteria
            requirements = acceptanceCriteria.Count > 0
                ? StripCheckboxLines(description).Trim()
                : description.Trim();
        }
        else
        {
            // Had sections but none matched requirements headings
            requirements = string.Empty;
        }

        return new ParsedIssue
        {
            RequirementsSection = requirements,
            AcceptanceCriteria = acceptanceCriteria
        };
    }

    /// <summary>
    /// Formats a parsed issue into a structured prompt section suitable for agent consumption.
    /// </summary>
    public string Format(ParsedIssue parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

        sb.AppendLine("## Requirements");
        sb.AppendLine(parsed.RequirementsSection);
        sb.AppendLine();
        sb.AppendLine("## Acceptance Criteria");

        if (parsed.AcceptanceCriteria.Count > 0)
        {
            for (var i = 0; i < parsed.AcceptanceCriteria.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {parsed.AcceptanceCriteria[i]}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Splits the description into sections keyed by heading name (lowercased).
    /// </summary>
    private static Dictionary<string, string> ExtractSections(string description)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headingMatches = HeadingPattern().Matches(description);

        if (headingMatches.Count == 0)
            return sections;

        for (var i = 0; i < headingMatches.Count; i++)
        {
            var heading = headingMatches[i].Groups[1].Value.Trim();
            var startIndex = headingMatches[i].Index + headingMatches[i].Length;
            var endIndex = i + 1 < headingMatches.Count
                ? headingMatches[i + 1].Index
                : description.Length;

            var content = description[startIndex..endIndex].Trim();
            sections[heading.ToLowerInvariant()] = content;
        }

        return sections;
    }

    private static string FindSection(Dictionary<string, string> sections, string[] headingNames)
    {
        foreach (var name in headingNames)
        {
            if (sections.TryGetValue(name, out var content))
                return content;
        }

        return string.Empty;
    }

    private static List<string> ExtractCheckboxItems(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var matches = CheckboxPattern().Matches(text);
        return matches.Select(m => m.Groups[1].Value.Trim()).ToList();
    }

    /// <summary>
    /// Extracts list items from text, supporting both checkbox (- [ ] item) and numbered (1. item) formats.
    /// </summary>
    private static List<string> ExtractListItems(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // Try checkbox items first
        var checkboxItems = ExtractCheckboxItems(text);
        if (checkboxItems.Count > 0)
            return checkboxItems;

        // Fall back to numbered list items
        var matches = NumberedListPattern().Matches(text);
        return matches.Select(m => m.Groups[1].Value.Trim()).ToList();
    }

    private static string StripCheckboxLines(string text)
    {
        var lines = text.Split('\n');
        var filtered = lines.Where(line => !CheckboxPattern().IsMatch(line));
        return string.Join('\n', filtered);
    }
}
