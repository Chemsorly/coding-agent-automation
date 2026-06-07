using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Infrastructure.GitHub;
using Moq;
using Octokit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Property-based tests for GitHubProviderBase.ParseIssueIdentifier.
/// Feature: 018-encapsulation-improvements, Property 3: ParseIssueIdentifier round-trip
/// </summary>
public class ParseIssueIdentifierPropertyTests
{
    /// <summary>
    /// Test helper that exposes the protected ParseIssueIdentifier method for testing.
    /// </summary>
    private sealed class TestableGitHubProvider : GitHubProviderBase
    {
        public TestableGitHubProvider()
            : base(new GitHubConnectionInfo("https://api.github.com", "test-owner", "test-repo"), new Mock<IGitHubClient>().Object)
        {
        }

        public static int ExposedParseIssueIdentifier(string identifier)
            => ParseIssueIdentifier(identifier);
    }

    /// <summary>
    /// Property 3: ParseIssueIdentifier round-trip (positive case).
    /// For any non-negative integer n, ParseIssueIdentifier(n.ToString()) returns n.
    /// **Validates: Requirements 28.1, 28.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ParseIssueIdentifier_RoundTrips_NonNegativeIntegers(NonNegativeInt n)
    {
        var input = n.Get.ToString();

        var result = TestableGitHubProvider.ExposedParseIssueIdentifier(input);

        result.Should().Be(n.Get);
    }

    /// <summary>
    /// Property 3: ParseIssueIdentifier round-trip (negative case).
    /// For any non-integer string, ParseIssueIdentifier throws ArgumentException.
    /// **Validates: Requirements 28.1, 28.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool ParseIssueIdentifier_ThrowsArgumentException_ForNonIntegerStrings(NonEmptyString input)
    {
        // Skip strings that are valid integers — we only want non-integer strings
        if (int.TryParse(input.Get, out _))
            return true;

        try
        {
            TestableGitHubProvider.ExposedParseIssueIdentifier(input.Get);
            return false; // Should have thrown
        }
        catch (ArgumentException)
        {
            return true; // Expected
        }
    }
}
