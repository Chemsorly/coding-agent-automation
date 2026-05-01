using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property 9: Issue description parsing round-trip
/// Feature: automated-dev-pipeline, Property 9: Issue description parsing round-trip
/// Validates: Requirements 12.5
/// </summary>
public class IssueDescriptionParserPropertyTests
{
    private readonly IssueDescriptionParser _parser = new();

    /// <summary>
    /// Property 9: For random markdown strings, Parse(Format(Parse(description))) ≡ Parse(description).
    /// **Validates: Requirements 12.5**
    /// </summary>
    [Property]
    public void ParseFormatParse_RoundTrip_ProducesEquivalentParsedIssue(byte scenarioSeed)
    {
        // Generate a variety of markdown descriptions
        var description = GenerateMarkdownDescription(scenarioSeed);

        var firstParse = _parser.Parse(description);
        var formatted = _parser.Format(firstParse);
        var secondParse = _parser.Parse(formatted);

        Assert.Equal(firstParse.RequirementsSection, secondParse.RequirementsSection);
        Assert.Equal(firstParse.AcceptanceCriteria.Count, secondParse.AcceptanceCriteria.Count);
        for (var i = 0; i < firstParse.AcceptanceCriteria.Count; i++)
        {
            Assert.Equal(firstParse.AcceptanceCriteria[i], secondParse.AcceptanceCriteria[i]);
        }
    }

    /// <summary>
    /// Property 9 with structured headings: descriptions with ## Requirements and ## Acceptance Criteria.
    /// **Validates: Requirements 12.5**
    /// </summary>
    [Property]
    public void ParseFormatParse_WithStructuredHeadings_RoundTrips(
        NonEmptyString requirements,
        byte criteriaCount)
    {
        var count = criteriaCount % 6; // 0-5 criteria
        var criteria = Enumerable.Range(1, count)
            .Select(i => $"- [ ] Criterion {i} for {requirements.Get.Replace("\n", " ").Replace("\r", " ")}")
            .ToList();

        var description = $"## Requirements\n{requirements.Get.Replace("\n", " ").Replace("\r", " ")}\n\n## Acceptance Criteria\n{string.Join("\n", criteria)}";

        var firstParse = _parser.Parse(description);
        var formatted = _parser.Format(firstParse);
        var secondParse = _parser.Parse(formatted);

        Assert.Equal(firstParse.RequirementsSection, secondParse.RequirementsSection);
        Assert.Equal(firstParse.AcceptanceCriteria.Count, secondParse.AcceptanceCriteria.Count);
        for (var i = 0; i < firstParse.AcceptanceCriteria.Count; i++)
        {
            Assert.Equal(firstParse.AcceptanceCriteria[i], secondParse.AcceptanceCriteria[i]);
        }
    }

    /// <summary>
    /// Property 9 with checkbox-only descriptions.
    /// **Validates: Requirements 12.5**
    /// </summary>
    [Property]
    public void ParseFormatParse_WithCheckboxOnly_RoundTrips(
        NonEmptyString item1,
        NonEmptyString item2)
    {
        // Sanitize: remove newlines from items to keep them single-line
        var clean1 = item1.Get.Replace("\n", " ").Replace("\r", " ").Trim();
        var clean2 = item2.Get.Replace("\n", " ").Replace("\r", " ").Trim();

        if (string.IsNullOrWhiteSpace(clean1) || string.IsNullOrWhiteSpace(clean2))
            return; // Skip degenerate cases

        var description = $"- [ ] {clean1}\n- [x] {clean2}";

        var firstParse = _parser.Parse(description);
        var formatted = _parser.Format(firstParse);
        var secondParse = _parser.Parse(formatted);

        Assert.Equal(firstParse.RequirementsSection, secondParse.RequirementsSection);
        Assert.Equal(firstParse.AcceptanceCriteria.Count, secondParse.AcceptanceCriteria.Count);
        for (var i = 0; i < firstParse.AcceptanceCriteria.Count; i++)
        {
            Assert.Equal(firstParse.AcceptanceCriteria[i], secondParse.AcceptanceCriteria[i]);
        }
    }

    /// <summary>
    /// Generates a markdown description based on a seed byte to cover various scenarios.
    /// </summary>
    private static string GenerateMarkdownDescription(byte seed)
    {
        var scenario = seed % 8;
        return scenario switch
        {
            0 => "## Requirements\nImplement user login\n\n## Acceptance Criteria\n- [ ] Users can log in\n- [x] Password is validated",
            1 => "## Description\nAdd a new feature for data export",
            2 => "- [ ] First task\n- [ ] Second task\n- [x] Third task",
            3 => "Just a plain text description with no structure at all.",
            4 => "",
            5 => "## Requirements\nBuild the API endpoint\n\n## Acceptance Criteria\n- [ ] Returns 200 on success",
            6 => "## Acceptance Criteria\n- [ ] Widget renders correctly\n- [ ] Widget handles empty state",
            7 => "Some intro text\n\n## Requirements\nCore requirement here\n\n## Acceptance Criteria\n- [ ] Criterion A\n- [ ] Criterion B\n- [x] Criterion C",
            _ => "Fallback description"
        };
    }
}
