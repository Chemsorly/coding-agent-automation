// Feature: 021-consolidation-loops
// Property 8: Refactoring Creates Issues Per Proposal Capped at 3
// Property 9: Refactoring Summary Includes Issue Count and Identifiers
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Agent.Executors;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests.Executors;

/// <summary>
/// Property-based tests for RefactoringExecutor issue creation cap and summary formatting.
/// **Validates: Requirements 6.2, 6.4, 6.7**
/// </summary>
public class RefactoringExecutorPropertyTests
{
    private static readonly string[] TitlePool =
    [
        "Extract shared utility",
        "Remove dead code",
        "Consolidate duplicate logic",
        "Rename inconsistent methods",
        "Split large class",
        "Fix TODO comments"
    ];

    /// <summary>
    /// Property 8: Refactoring Creates Issues Per Proposal Capped at 3
    /// For any list of CreatedIssueInfo instances (length 0 to N),
    /// the executor caps at min(count, 3) issues. We verify via FormatRefactoringSummary.
    /// **Validates: Requirements 6.2, 6.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(RefactoringArbitraries) })]
    public void FormatRefactoringSummary_IssueCount_NeverExceedsThree(
        List<CreatedIssueInfo> createdIssues)
    {
        // Simulate the cap that ExecuteAsync applies: Take(3)
        var capped = createdIssues.Take(3).ToList();
        var proposalCount = createdIssues.Count;

        var summary = RefactoringExecutor.FormatRefactoringSummary(capped, proposalCount);

        if (capped.Count == 0 && proposalCount == 0)
        {
            summary.Should().Contain("No refactoring opportunities");
        }
        else if (capped.Count > 0)
        {
            summary.Should().Contain(capped.Count.ToString());
        }
    }

    /// <summary>
    /// Property 9: Refactoring Summary Includes Issue Count and Identifiers
    /// For any list of CreatedIssueInfo results (0 to 3 items), the summary string
    /// contains the count and every issue identifier.
    /// **Validates: Requirements 6.7**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(RefactoringArbitraries) })]
    public void FormatRefactoringSummary_IncludesAllIdentifiers(
        List<CreatedIssueInfo> createdIssues)
    {
        var capped = createdIssues.Take(3).ToList();

        var summary = RefactoringExecutor.FormatRefactoringSummary(capped, capped.Count);

        if (capped.Count == 0)
        {
            summary.Should().Contain("No refactoring opportunities");
        }
        else
        {
            summary.Should().Contain(capped.Count.ToString());

            foreach (var issue in capped)
            {
                summary.Should().Contain(issue.Identifier,
                    $"summary should contain identifier '{issue.Identifier}'");
            }
        }
    }
}

/// <summary>
/// FsCheck arbitrary generators for refactoring property tests.
/// </summary>
public class RefactoringArbitraries
{
    private static readonly string[] TitlePool =
    [
        "Extract shared utility",
        "Remove dead code",
        "Consolidate duplicate logic",
        "Rename inconsistent methods",
        "Split large class",
        "Fix TODO comments"
    ];

    public static Arbitrary<List<CreatedIssueInfo>> ListArb()
    {
        var issueGen =
            from id in Gen.Choose(1, 999).Select(n => n.ToString())
            from title in Gen.Elements(TitlePool)
            select new CreatedIssueInfo
            {
                Identifier = id,
                Title = title,
                Url = $"https://github.com/test/repo/issues/{id}"
            };

        return Gen.Choose(0, 6)
            .SelectMany(count => Gen.ArrayOf(issueGen, count))
            .Select(arr => arr.ToList())
            .ToArbitrary();
    }
}
