using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

public class ConventionalCommitTypesTests
{
    private static string ScanIssueBody(string metadataLine) =>
        $"## Problem\n\nSomething is off.\n\n{metadataLine}\n\n---\n*{RefactoringScanIssues.Footer}.*";

    [Theory]
    [InlineData(RefactoringCategories.Duplication, ConventionalCommitTypes.Refactor)]
    [InlineData(RefactoringCategories.StructuralDrift, ConventionalCommitTypes.Refactor)]
    [InlineData(RefactoringCategories.Complexity, ConventionalCommitTypes.Refactor)]
    [InlineData(RefactoringCategories.OverEngineering, ConventionalCommitTypes.Refactor)]
    [InlineData(RefactoringCategories.DeadCode, ConventionalCommitTypes.Refactor)]
    [InlineData(RefactoringCategories.NamingInconsistency, ConventionalCommitTypes.Refactor)]
    [InlineData(RefactoringCategories.PrimitiveObsession, ConventionalCommitTypes.Refactor)]
    [InlineData(RefactoringCategories.Bug, ConventionalCommitTypes.Fix)]
    [InlineData(RefactoringCategories.TestGap, ConventionalCommitTypes.Test)]
    [InlineData(RefactoringCategories.StaleDocumentation, ConventionalCommitTypes.Docs)]
    [InlineData(RefactoringCategories.Todo, ConventionalCommitTypes.Chore)]
    public void WhenScanIssueHasCategory_ShouldMapItToCommitType(string category, string expected)
    {
        var body = ScanIssueBody($"**Category:** {category} | **Effort:** small | **Risk:** low");

        ConventionalCommitTypes.Resolve(body, []).Should().Be(expected);
    }

    [Fact]
    public void EveryRefactoringCategory_ShouldHaveACommitTypeOtherThanFeature()
    {
        foreach (var category in RefactoringCategories.All)
        {
            var body = ScanIssueBody($"**Category:** {category}");

            ConventionalCommitTypes.Resolve(body, []).Should().NotBe(ConventionalCommitTypes.Feature,
                because: $"scan category '{category}' needs a mapping");
        }
    }

    [Fact]
    public void WhenCategoryCaseDiffers_ShouldStillMap()
    {
        var body = ScanIssueBody("**Category:** Duplication | **Effort:** small");

        ConventionalCommitTypes.Resolve(body, []).Should().Be(ConventionalCommitTypes.Refactor);
    }

    [Fact]
    public void WhenCategoryLineIsMissingFooter_ShouldIgnoreCategory()
    {
        var body = "## Problem\n\nPasted from a scan.\n\n**Category:** duplication | **Effort:** small";

        ConventionalCommitTypes.Resolve(body, []).Should().Be(ConventionalCommitTypes.Feature);
    }

    [Fact]
    public void WhenCategoryOnlyAppearsInsideProse_ShouldIgnoreCategory()
    {
        var body = ScanIssueBody("The field **Category:** duplication is wrong.");

        ConventionalCommitTypes.Resolve(body, []).Should().Be(ConventionalCommitTypes.Feature);
    }

    [Fact]
    public void WhenScanIssueCategoryIsUnknown_ShouldFallBackToLabels()
    {
        var body = ScanIssueBody("**Category:** something-new | **Effort:** small");

        ConventionalCommitTypes.Resolve(body, ["bug"]).Should().Be(ConventionalCommitTypes.Fix);
    }

    [Fact]
    public void WhenScanCategoryAndLabelDisagree_ShouldPreferCategory()
    {
        var body = ScanIssueBody("**Category:** test-gap");

        ConventionalCommitTypes.Resolve(body, ["bug"]).Should().Be(ConventionalCommitTypes.Test);
    }

    [Theory]
    [InlineData("bug")]
    [InlineData("Bug")]
    public void WhenIssueHasBugLabel_ShouldBeFix(string label)
    {
        ConventionalCommitTypes.Resolve("A human wrote this.", ["agent:next", label])
            .Should().Be(ConventionalCommitTypes.Fix);
    }

    [Fact]
    public void WhenIssueHasOtherLabels_ShouldBeFeature()
    {
        ConventionalCommitTypes.Resolve("A human wrote this.", ["enhancement", "agent:generated"])
            .Should().Be(ConventionalCommitTypes.Feature);
    }

    [Fact]
    public void WhenDescriptionIsNull_ShouldUseLabels()
    {
        ConventionalCommitTypes.Resolve(null, []).Should().Be(ConventionalCommitTypes.Feature);
        ConventionalCommitTypes.Resolve(null, ["bug"]).Should().Be(ConventionalCommitTypes.Fix);
    }
}
