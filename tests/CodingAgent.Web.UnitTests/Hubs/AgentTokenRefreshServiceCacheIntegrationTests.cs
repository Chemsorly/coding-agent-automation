using System.Net;
using System.Text.Json;
using System.Threading;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Moq;
using Moq.Protected;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Integration tests that verify <see cref="AgentTokenRefreshService"/> benefits from
/// the token cache inside <see cref="TokenVendingService"/> (issue #2575, Fix C).
///
/// Unlike the existing <see cref="AgentTokenRefreshServiceTests"/> which mock
/// <c>ITokenVendingService</c>, these tests use a <em>real</em>
/// <see cref="TokenVendingService"/> backed by a mock <see cref="HttpMessageHandler"/>.
/// This exercises the full path:
/// <c>RefreshTokenAsync → VendTokenAsync → GenerateAgentTokenAsync → cache</c>
/// and verifies that two consecutive calls for the same provider config result in
/// exactly one HTTP call to the GitHub installations endpoint.
/// </summary>
public sealed class AgentTokenRefreshServiceCacheIntegrationTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<ILogger> _mockLogger = new();

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string GenerateValidPrivateKeyBase64()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem));
    }

    /// <summary>
    /// Creates a mock HTTP handler that returns a successful token response for each call.
    /// The token string is unique per HTTP call (using an Interlocked counter) so tests
    /// can detect whether a second HTTP call was made.
    /// </summary>
    private static (Mock<HttpMessageHandler> Handler, HttpClient Client) MakeMockHttpClient(
        DateTimeOffset? expiresAt = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        var callCount = 0;

        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                var count = Interlocked.Increment(ref callCount);
                var expiry = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1);
                var body = JsonSerializer.Serialize(new
                {
                    token = $"ghs_token_{count}",
                    expires_at = expiry.ToString("O")
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                });
            });

        return (handler, new HttpClient(handler.Object));
    }

    private static ProviderConfig MakeGitHubConfig(string id = "repo-1") => new()
    {
        Id = id,
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        DisplayName = "Test Repo",
        Settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.PrivateKeyBase64] = GenerateValidPrivateKeyBase64(),
            [ProviderSettingKeys.ClientId] = "Iv1.abc123",
            [ProviderSettingKeys.InstallationId] = "12345678",
            [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
            [ProviderSettingKeys.Owner] = "test-owner",
            [ProviderSettingKeys.Repo] = "test-repo",
        }
    };

    private AgentTokenRefreshService CreateServiceWithRealVending(HttpClient httpClient)
    {
        var realVending = new TokenVendingService(_mockLogger.Object, httpClient);
        return new AgentTokenRefreshService(_mockFacade.Object, realVending, _mockLogger.Object);
    }

    // ── Cache integration: two RefreshTokenAsync calls hit GitHub exactly once ──

    /// <summary>
    /// Regression test for issue #2575, Fix C: the token cache in
    /// <see cref="TokenVendingService.GenerateAgentTokenAsync"/> must be exercised
    /// by the <see cref="AgentTokenRefreshService"/> refresh path.
    ///
    /// Two consecutive <c>RefreshTokenAsync</c> calls for the same provider config must
    /// result in exactly one HTTP call to the GitHub installations endpoint. The second call
    /// must receive the cached token without re-minting.
    ///
    /// Before Fix C, <c>GenerateAgentTokenAsync</c> always minted a fresh token, causing
    /// a GitHub API call per assignment poll and per agent token-refresh request. During a
    /// GitHub incident this multiplied load on the failing endpoint.
    /// </summary>
    [Fact]
    public async Task RefreshTokenAsync_TwoCallsSameConfig_ResultsInOneHttpCall()
    {
        // ARRANGE
        var (handler, httpClient) = MakeMockHttpClient();
        var service = CreateServiceWithRealVending(httpClient);
        var config = MakeGitHubConfig("repo-1");

        var run = new PipelineRun
        {
            RunId = "job-cache-1",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1"
        };

        _mockFacade.Setup(f => f.GetRun("job-cache-1")).Returns(run);
        _mockFacade
            .Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        // ACT — two consecutive refresh calls for the same provider config
        var result1 = await service.RefreshTokenAsync("job-cache-1", ProviderKind.Repository, CancellationToken.None);
        var result2 = await service.RefreshTokenAsync("job-cache-1", ProviderKind.Repository, CancellationToken.None);

        // ASSERT — same token returned, GitHub called only once
        result2.Token.Should().Be(result1.Token,
            "the second RefreshTokenAsync call must return the cached token without re-minting");
        result2.ExpiresAt.Should().Be(result1.ExpiresAt,
            "the cached expiry must be returned unchanged on the second call");

        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    /// <summary>
    /// Verifies that the cache is keyed per provider config: two calls for different
    /// configs (different installations) each result in one HTTP call (two total),
    /// not a cache collision.
    /// </summary>
    [Fact]
    public async Task RefreshTokenAsync_DifferentConfigs_MintSeparateTokens()
    {
        // ARRANGE
        var (handler, httpClient) = MakeMockHttpClient();
        var service = CreateServiceWithRealVending(httpClient);

        var config1 = MakeGitHubConfig("repo-1");
        var config2 = new ProviderConfig
        {
            Id = "repo-2",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo 2",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = GenerateValidPrivateKeyBase64(),
                [ProviderSettingKeys.ClientId] = "Iv1.abc123",
                [ProviderSettingKeys.InstallationId] = "99999999", // different installation
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo-2",
            }
        };

        var run1 = new PipelineRun { RunId = "job-A", IssueIdentifier = "o/r#1", IssueTitle = "A", IssueProviderConfigId = "i1", RepoProviderConfigId = "repo-1" };
        var run2 = new PipelineRun { RunId = "job-B", IssueIdentifier = "o/r#2", IssueTitle = "B", IssueProviderConfigId = "i2", RepoProviderConfigId = "repo-2" };

        _mockFacade.Setup(f => f.GetRun("job-A")).Returns(run1);
        _mockFacade.Setup(f => f.GetRun("job-B")).Returns(run2);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>())).ReturnsAsync(config1);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-2", ProviderKind.Repository, It.IsAny<CancellationToken>())).ReturnsAsync(config2);

        // ACT
        var result1 = await service.RefreshTokenAsync("job-A", ProviderKind.Repository, CancellationToken.None);
        var result2 = await service.RefreshTokenAsync("job-B", ProviderKind.Repository, CancellationToken.None);

        // ASSERT — two distinct tokens (different cache keys), two HTTP calls
        result2.Token.Should().NotBe(result1.Token,
            "different installations must produce distinct tokens with separate cache entries");
        handler.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }
}
