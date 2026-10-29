using CodingAgent.Contracts.Services;
using AwesomeAssertions;

namespace CodingAgent.Web.UnitTests.Hubs;

public class AgentLinkSanitizerTests
{
    // Req 12.8: BrainInboxFileUrl must be an absolute https URL or be dropped.

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("http://example.com/x", null)]
    [InlineData("/relative/path", null)]
    [InlineData("ftp://example.com/x", null)]
    public void HttpsOrNull_NonHttps_ReturnsNull(string? input, string? expected)
    {
        AgentLinkSanitizer.HttpsOrNull(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/commit/abc123", "https://github.com/owner/repo/commit/abc123")]
    [InlineData("https://sonarcloud.io/dashboard?id=x", "https://sonarcloud.io/dashboard?id=x")]
    public void HttpsOrNull_ValidHttps_Passes(string input, string expected)
    {
        AgentLinkSanitizer.HttpsOrNull(input).Should().Be(expected);
    }
}
