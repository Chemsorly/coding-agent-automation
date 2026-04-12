using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Property-based tests for prompt construction.
/// </summary>
public class PromptConstructionPropertyTests
{
    /// <summary>
    /// Property 2: Prompt construction includes all issue fields.
    /// For any valid IssueDetail (with non-empty title, non-empty description,
    /// and zero or more acceptance criteria), the prompt contains the issue title,
    /// the issue description, and every individual acceptance criterion string.
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public void Prompt_ContainsAllIssueFields(NonEmptyString title, NonEmptyString description, byte criteriaCount)
    {
        // Generate a reasonable number of criteria (0-5)
        var count = criteriaCount % 6;
        var criteriaOptions = new[] { "Must compile", "Tests pass", "Coverage above 80%", "No security issues", "Handles edge cases" };
        var criteria = criteriaOptions.Take(count).ToList();

        var issue = new IssueDetail
        {
            Identifier = "1",
            Title = title.Get,
            Description = description.Get,
            Labels = Array.Empty<string>(),
            AcceptanceCriteria = criteria.AsReadOnly()
        };

        var parsed = new ParsedIssue
        {
            RequirementsSection = issue.Description,
            AcceptanceCriteria = issue.AcceptanceCriteria
        };

        var prompt = PromptBuilder.BuildPrompt(issue, parsed);

        prompt.Should().Contain(issue.Title);
        prompt.Should().Contain(issue.Description);

        foreach (var criterion in issue.AcceptanceCriteria)
        {
            prompt.Should().Contain(criterion);
        }
    }
}
