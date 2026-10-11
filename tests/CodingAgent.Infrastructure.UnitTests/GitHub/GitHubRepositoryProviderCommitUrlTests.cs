using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Pipeline;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// Unit tests for <see cref="GitHubRepositoryProvider.GetCommitWebUrl"/>.
/// Verifies the URL construction for standard GitHub and GitHub Enterprise API URLs,
/// and that an empty SHA returns null. No network calls are made.
/// </summary>
public class GitHubRepositoryProviderCommitUrlTests
{
    [Theory]
    [InlineData("https://api.github.com", "https://github.com/owner/repo/commit/abc123")]
    [InlineData("https://github.example.com/api/v3", "https://github.example.com/owner/repo/commit/abc123")]
    [InlineData("https://github.example.com/api/v3/", "https://github.example.com/owner/repo/commit/abc123")]
    public async Task GetCommitWebUrl_ReturnsExpectedUrl(string apiUrl, string expectedUrl)
    {
        await using var provider = new GitHubRepositoryProvider(
            new GitHubConnectionInfo(apiUrl, "owner", "repo"), "token", "main");

        var result = provider.GetCommitWebUrl("abc123");

        result.Should().Be(expectedUrl);
    }

    [Fact]
    public async Task GetCommitWebUrl_EmptySha_ReturnsNull()
    {
        await using var provider = new GitHubRepositoryProvider(
            new GitHubConnectionInfo("https://api.github.com", "owner", "repo"), "token", "main");

        var result = provider.GetCommitWebUrl(string.Empty);

        result.Should().BeNull();
    }

    // TODO: Missing test: GetCommitWebUrl with an empty ApiUrl ("") should return null. The
    // implementation returns null when WebBaseUrl is empty, but this branch is untested. Add a fact:
    // new GitHubConnectionInfo("", "owner", "repo") → GetCommitWebUrl("abc123") returns null.
}
