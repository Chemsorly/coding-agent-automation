using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Octokit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Property-based tests for GitHubRepositoryProvider helper methods.
/// </summary>
public class GitHubRepositoryProviderPropertyTests
{
    /// <summary>
    /// Feature: provider-interface-gaps, Property 4: RepositoryFullName format
    /// For any non-null owner and repo strings, RepositoryFullName equals $"{owner}/{repo}".
    /// **Validates: Requirements 2.5**
    /// </summary>
    // Feature: provider-interface-gaps, Property 4: RepositoryFullName format
    [Property]
    public void RepositoryFullName_Equals_Owner_Slash_Repo(NonEmptyString owner, NonEmptyString repo)
    {
        // Arrange — use the internal test constructor with a mock IGitHubClient
        var mockClient = new Mock<IGitHubClient>();
        var provider = new GitHubRepositoryProvider(
            new GitHubConnectionInfo("https://api.github.com", owner.Get, repo.Get),
            gitHubClient: mockClient.Object,
            token: "test-token",
            baseBranch: "main");

        // Act
        var fullName = provider.RepositoryFullName;

        // Assert — must be exactly "{owner}/{repo}"
        fullName.Should().Be($"{owner.Get}/{repo.Get}");
    }

    /// <summary>
    /// Property 1: Branch name generation produces valid slug format.
    /// For any issue number (positive integer) and any issue title (non-empty string),
    /// the generated branch name matches the pattern feature/auto-{issueNumber}-{slug}
    /// where slug contains only lowercase alphanumeric characters and hyphens,
    /// does not start or end with a hyphen, and does not contain consecutive hyphens.
    /// **Validates: Requirements 2.4**
    /// </summary>
    [Property]
    public void BranchName_AlwaysProducesValidSlugFormat(PositiveInt issueNum, NonEmptyString title)
    {
        var number = issueNum.Get.ToString();
        var branchName = PipelineFormatting.GenerateBranchName(number, title.Get);

        // Must start with feature/auto-{number}
        branchName.Should().StartWith($"feature/auto-{number}");

        // Must not exceed max length
        branchName.Length.Should().BeLessThanOrEqualTo(100);

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
    [Property]
    public void PrTitle_FollowsConventionalCommitFormat(NonEmptyString title, PositiveInt issueNum)
    {
        var number = issueNum.Get.ToString();
        var prTitle = PipelineFormatting.GeneratePrTitle(title.Get, number);

        prTitle.Should().Be($"feat: {title.Get} (#{number})");
    }

    /// <summary>
    /// Property 6: PR body contains all required sections including file changes and issue context.
    /// **Validates: Requirements 6.3, 6.4**
    /// </summary>
    [Property]
    public void PrBody_ContainsAllRequiredSections(
        PositiveInt issueNum,
        NonNegativeInt passed,
        NonNegativeInt failed,
        NonNegativeInt skipped,
        NonEmptyString title)
    {
        var number = issueNum.Get.ToString();
        var fileChanges = new List<FileChangeSummary>
        {
            new("Added", "src/Test.cs"),
            new("Modified", "src/Other.cs")
        };

        var body = PipelineFormatting.GeneratePrBody(
            number,
            passed.Get,
            failed.Get,
            skipped.Get,
            coveragePercent: 85.5,
            fileChanges: fileChanges,
            issueTitle: title.Get);

        body.Should().Contain("## Issue Context");
        body.Should().Contain(title.Get);
        body.Should().Contain($"(#{number})");
        body.Should().Contain("## Files Changed");
        body.Should().Contain("src/Test.cs");
        body.Should().Contain("## Test Results");
        body.Should().Contain($"Passed: {passed.Get}");
        body.Should().Contain($"Failed: {failed.Get}");
        body.Should().Contain($"Skipped: {skipped.Get}");
        body.Should().Contain("## Coverage");
        body.Should().Contain("85.5%");
        body.Should().Contain($"Closes #{number}");
    }
}
