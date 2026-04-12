using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using KiroWebUI.Pipeline.Providers;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Property-based tests for GitHubRepositoryProvider helper methods.
/// </summary>
public class GitHubRepositoryProviderPropertyTests
{
    /// <summary>
    /// Property 1: Branch name generation produces valid slug format.
    /// For any issue number (positive integer) and any issue title (non-empty string),
    /// the generated branch name matches the pattern feature/auto-{issueNumber}-{slug}
    /// where slug contains only lowercase alphanumeric characters and hyphens,
    /// does not start or end with a hyphen, and does not contain consecutive hyphens.
    /// **Validates: Requirements 2.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public void BranchName_AlwaysProducesValidSlugFormat(PositiveInt issueNum, NonEmptyString title)
    {
        var number = issueNum.Get.ToString();
        var branchName = GitHubRepositoryProvider.GenerateBranchName(number, title.Get);

        // Must start with feature/auto-{number}
        branchName.Should().StartWith($"feature/auto-{number}");

        // Extract slug part (after the prefix)
        var prefix = $"feature/auto-{number}";
        if (branchName.Length > prefix.Length)
        {
            branchName[prefix.Length].Should().Be('-');
            var slug = branchName[(prefix.Length + 1)..];

            // Slug should only contain lowercase alphanumeric and hyphens
            slug.Should().MatchRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$");

            // No consecutive hyphens
            slug.Should().NotContain("--");
        }
    }

    /// <summary>
    /// Property 5: PR title follows conventional commit format.
    /// For any issue title (non-empty string) and any issue number (positive integer),
    /// the generated PR title equals feat: {issueTitle} (#{issueNumber}).
    /// **Validates: Requirements 6.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public void PrTitle_FollowsConventionalCommitFormat(NonEmptyString title, PositiveInt issueNum)
    {
        var number = issueNum.Get.ToString();
        var prTitle = GitHubRepositoryProvider.GeneratePrTitle(title.Get, number);

        prTitle.Should().Be($"feat: {title.Get} (#{number})");
    }

    /// <summary>
    /// Property 6: PR body contains all required sections.
    /// For any issue number, test result counts, coverage percentage, and implementation summary,
    /// the generated PR body contains all required sections and Closes #{issueNumber}.
    /// **Validates: Requirements 6.3, 6.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public void PrBody_ContainsAllRequiredSections(
        PositiveInt issueNum,
        NonNegativeInt passed,
        NonNegativeInt failed,
        NonNegativeInt skipped,
        NonEmptyString summary)
    {
        var number = issueNum.Get.ToString();
        var body = GitHubRepositoryProvider.GeneratePrBody(
            number,
            passed.Get,
            failed.Get,
            skipped.Get,
            coveragePercent: 85.5,
            implementationSummary: summary.Get);

        body.Should().Contain("## Implementation Summary");
        body.Should().Contain(summary.Get);
        body.Should().Contain("## Test Results");
        body.Should().Contain($"Passed: {passed.Get}");
        body.Should().Contain($"Failed: {failed.Get}");
        body.Should().Contain($"Skipped: {skipped.Get}");
        body.Should().Contain("## Coverage");
        body.Should().Contain("85.5%");
        body.Should().Contain($"Closes #{number}");
    }
}
