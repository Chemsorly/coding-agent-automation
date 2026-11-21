using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

public class RefactoringScanIssuesTests
{
    private static IssueSummary Issue(string description, bool isAuthorVerified) => new()
    {
        Identifier = "7",
        Title = "Extract retry logic",
        Labels = ["agent:generated"],
        Description = description,
        IsAuthorVerified = isAuthorVerified,
    };

    private static string BodyWithFooter => $"## Problem\n\nSomething is off.\n\n---\n*{RefactoringScanIssues.Footer}.*";

    [Fact]
    public void IsScanIssue_VerifiedAuthorWithFooter_IsTrue() =>
        RefactoringScanIssues.IsScanIssue(Issue(BodyWithFooter, isAuthorVerified: true)).Should().BeTrue();

    [Fact]
    public void IsScanIssue_UnverifiedAuthorWithFooter_IsFalse() =>
        RefactoringScanIssues.IsScanIssue(Issue(BodyWithFooter, isAuthorVerified: false))
            .Should().BeFalse("an author outside the repository can copy the footer into any issue");

    [Fact]
    public void IsScanIssue_VerifiedAuthorWithoutFooter_IsFalse() =>
        RefactoringScanIssues.IsScanIssue(Issue("## Problem\n\nNo footer here.", isAuthorVerified: true)).Should().BeFalse();
}
