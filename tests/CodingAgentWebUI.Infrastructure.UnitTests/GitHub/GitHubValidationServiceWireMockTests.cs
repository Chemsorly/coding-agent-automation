using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitHub;

/// <summary>
/// WireMock-based tests for GitHubValidationService.
/// Tests validate credential checking and repository listing via HTTP-level interception.
/// The GitHubAppAuthService token exchange is stubbed so no real GitHub credentials are needed.
/// </summary>
public class GitHubValidationServiceWireMockTests : WireMockTestBase
{
    private const string ClientId = "Iv1.testclient123";
    private const long InstallationId = 12345L;

    private static string GenerateValidPrivateKeyBase64()
    {
        using var rsa = RSA.Create(2048);
        var pemString = rsa.ExportRSAPrivateKeyPem();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(pemString));
    }

    /// <summary>
    /// Stubs the token exchange endpoint that GitHubAppAuthService calls.
    /// POST /api/v3/app/installations/{id}/access_tokens returns a fake token.
    /// </summary>
    private void StubTokenExchange()
    {
        StubPost(ApiPath($"/app/installations/{InstallationId}/access_tokens"), new
        {
            token = "ghs_fake_installation_token_123",
            expires_at = DateTimeOffset.UtcNow.AddHours(1).ToString("o"),
            permissions = new { issues = "write", contents = "write" }
        }, 201);
    }

    private void StubTokenExchangeUnauthorized()
    {
        StubError(ApiPath($"/app/installations/{InstallationId}/access_tokens"), 401,
            new { message = "Bad credentials" });
    }

    #region ValidateAppCredentialsAsync

    [Fact]
    public async Task ValidateAppCredentialsAsync_NoOwnerRepo_Success_ReturnsValidMessage()
    {
        StubTokenExchange();
        StubGet(ApiPath("/installation/repositories"), new
        {
            total_count = 3,
            repositories = new[]
            {
                new { id = 1, full_name = "org/repo1", name = "repo1", owner = new { login = "org", id = 1 } },
                new { id = 2, full_name = "org/repo2", name = "repo2", owner = new { login = "org", id = 2 } },
                new { id = 3, full_name = "org/repo3", name = "repo3", owner = new { login = "org", id = 3 } }
            }
        });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateAppCredentialsAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None);

        success.Should().BeTrue();
        message.Should().Contain("3 repository(ies) accessible");
    }

    [Fact]
    public async Task ValidateAppCredentialsAsync_WithOwnerRepo_Success_ReturnsPermissions()
    {
        StubTokenExchange();
        StubGet(ApiPath("/repos/test-owner/test-repo"), new
        {
            id = 1,
            name = "test-repo",
            full_name = "test-owner/test-repo",
            owner = new { login = "test-owner", id = 1 },
            permissions = new { pull = true, push = true, admin = false }
        });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateAppCredentialsAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None,
            owner: "test-owner", repo: "test-repo");

        success.Should().BeTrue();
        message.Should().Contain("test-owner/test-repo");
        message.Should().Contain("read");
        message.Should().Contain("write");
    }

    [Fact]
    public async Task ValidateAppCredentialsAsync_InvalidCredentials_ReturnsFalse()
    {
        StubTokenExchangeUnauthorized();

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateAppCredentialsAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None);

        success.Should().BeFalse();
        message.Should().Contain("failed");
    }

    [Fact]
    public async Task ValidateAppCredentialsAsync_InvalidPrivateKey_ReturnsFalse()
    {
        var invalidKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("not-a-pem-key"));

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateAppCredentialsAsync(
            Server.Url!, ClientId, InstallationId, invalidKey, CancellationToken.None);

        success.Should().BeFalse();
        message.Should().Contain("failed");
    }

    #endregion

    #region ListRepositoriesWithAppAsync

    [Fact]
    public async Task ListRepositoriesWithAppAsync_Success_ReturnsRepositories()
    {
        StubTokenExchange();
        StubGet(ApiPath("/installation/repositories"), new
        {
            total_count = 2,
            repositories = new[]
            {
                new { id = 1, full_name = "org/repo1", name = "repo1", owner = new { login = "org", id = 1 } },
                new { id = 2, full_name = "org/repo2", name = "repo2", owner = new { login = "org", id = 2 } }
            }
        });

        var service = new GitHubValidationService();
        var result = await service.ListRepositoriesWithAppAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].FullName.Should().Be("org/repo1");
        result[0].Owner.Should().Be("org");
        result[0].Name.Should().Be("repo1");
        result[1].FullName.Should().Be("org/repo2");
    }

    [Fact]
    public async Task ListRepositoriesWithAppAsync_Empty_ReturnsEmptyList()
    {
        StubTokenExchange();
        StubGet(ApiPath("/installation/repositories"), new
        {
            total_count = 0,
            repositories = Array.Empty<object>()
        });

        var service = new GitHubValidationService();
        var result = await service.ListRepositoriesWithAppAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    #endregion
}
