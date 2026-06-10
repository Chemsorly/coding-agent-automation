using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Unit tests for <see cref="GitHubProviderBase.DeriveGraphQlUri"/>.
/// Validates: Requirement 6.1 — GraphQL URL derivation for standard GitHub, GHE v3, and base URL.
/// </summary>
public class DeriveGraphQlUriTests
{
    /// <summary>
    /// Test helper that exposes the protected DeriveGraphQlUri method for testing.
    /// Uses the token-based constructor so the ApiUrl is correctly stored.
    /// </summary>
    private sealed class TestableGitHubProvider : GitHubProviderBase
    {
        public TestableGitHubProvider(string apiUrl)
            : base(new GitHubConnectionInfo(apiUrl, "test-owner", "test-repo"), "fake-token")
        {
        }

        public Uri ExposedDeriveGraphQlUri() => DeriveGraphQlUri();
    }

    [Fact]
    public void StandardGitHub_ReturnsApiGithubComGraphql()
    {
        // Standard GitHub (api.github.com) → https://api.github.com/graphql
        var provider = new TestableGitHubProvider("https://api.github.com");

        var result = provider.ExposedDeriveGraphQlUri();

        result.Should().Be(new Uri("https://api.github.com/graphql"));
    }

    [Fact]
    public void GheWithApiV3Suffix_ReplacesWithApiGraphql()
    {
        // GHE with /api/v3 suffix → replaces with /api/graphql
        var provider = new TestableGitHubProvider("https://github.example.com/api/v3");

        var result = provider.ExposedDeriveGraphQlUri();

        result.Should().Be(new Uri("https://github.example.com/api/graphql"));
    }

    [Fact]
    public void GheBaseUrl_AppendsGraphql()
    {
        // GHE with base URL (no /api/v3) → appends /graphql
        var provider = new TestableGitHubProvider("https://github.example.com");

        var result = provider.ExposedDeriveGraphQlUri();

        result.Should().Be(new Uri("https://github.example.com/graphql"));
    }

    [Fact]
    public void GheBaseUrlWithTrailingSlash_AppendsGraphqlWithoutDoubleSlash()
    {
        // Trailing slash should not produce double slashes
        var provider = new TestableGitHubProvider("https://github.example.com/");

        var result = provider.ExposedDeriveGraphQlUri();

        result.Should().Be(new Uri("https://github.example.com/graphql"));
    }

    [Fact]
    public void GheApiV3WithTrailingSlash_DoesNotMatch_AppendsGraphql()
    {
        // /api/v3/ (with trailing slash) doesn't EndsWith "/api/v3" — treated as base URL
        var provider = new TestableGitHubProvider("https://github.example.com/api/v3/");

        var result = provider.ExposedDeriveGraphQlUri();

        // The trailing slash means EndsWith("/api/v3") is false, so it appends /graphql
        result.Should().Be(new Uri("https://github.example.com/api/v3/graphql"));
    }
}
