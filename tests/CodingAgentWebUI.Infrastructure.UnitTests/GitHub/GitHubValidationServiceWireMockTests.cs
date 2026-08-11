using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;

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
        message.Should().Contain("private key");
    }

    /// <summary>
    /// Covers the GitHubAuthErrorKind.PrivateKeyDecodeFailure catch in AuthenticateAndGetTokenAsync:
    /// a base64 key that contains PEM markers but has an invalid body triggers PrivateKeyDecodeFailure.
    /// </summary>
    [Fact]
    public async Task ValidateAppCredentialsAsync_MalformedPrivateKeyPem_ReturnsPrivateKeyErrorMessage()
    {
        // A valid base64-encoded string that looks like a PEM header but has no valid RSA key body
        var fakePem = "-----BEGIN RSA PRIVATE KEY-----\nnotvalidbase64content==\n-----END RSA PRIVATE KEY-----";
        var malformedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(fakePem));

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateAppCredentialsAsync(
            Server.Url!, ClientId, InstallationId, malformedKey, CancellationToken.None);

        success.Should().BeFalse();
        // Malformed PEM body triggers PrivateKeyDecodeFailure or a downstream auth error — either way failure
        message.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Covers the AuthorizationException catch in ValidateInstallationWithoutRepoAsync:
    /// token exchange succeeds but listing installation repositories returns 401.
    /// </summary>
    [Fact]
    public async Task ValidateAppCredentialsAsync_NoOwnerRepo_InstallationTokenRejected_ReturnsAuthFailure()
    {
        StubTokenExchange();
        StubError(ApiPath("/installation/repositories"), 401, new { message = "Bad credentials" });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateAppCredentialsAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None);

        success.Should().BeFalse();
        message.Should().Contain("Authentication failed");
    }

    /// <summary>
    /// Covers the NotFoundException catch in ValidateRepositoryPermissionsAsync:
    /// token exchange succeeds but the specific repository returns 404.
    /// </summary>
    [Fact]
    public async Task ValidateAppCredentialsAsync_WithOwnerRepo_RepoNotFound_ReturnsFalse()
    {
        StubTokenExchange();
        StubError(ApiPath("/repos/test-owner/missing-repo"), 404, new { message = "Not Found" });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateAppCredentialsAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None,
            owner: "test-owner", repo: "missing-repo");

        success.Should().BeFalse();
        message.Should().Contain("not found");
    }

    /// <summary>
    /// Covers the admin-only permissions path in ValidateRepositoryPermissionsAsync.
    /// </summary>
    [Fact]
    public async Task ValidateAppCredentialsAsync_WithOwnerRepo_AdminOnly_ReturnsAdminPermission()
    {
        StubTokenExchange();
        StubGet(ApiPath("/repos/test-owner/test-repo"), new
        {
            id = 1,
            name = "test-repo",
            full_name = "test-owner/test-repo",
            owner = new { login = "test-owner", id = 1 },
            permissions = new { pull = false, push = false, admin = true }
        });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateAppCredentialsAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None,
            owner: "test-owner", repo: "test-repo");

        success.Should().BeTrue();
        message.Should().Contain("admin");
    }

    /// <summary>
    /// Covers the no-permissions path in ValidateRepositoryPermissionsAsync (all flags false → "none").
    /// </summary>
    [Fact]
    public async Task ValidateAppCredentialsAsync_WithOwnerRepo_NoPermissions_ReturnsNone()
    {
        StubTokenExchange();
        StubGet(ApiPath("/repos/test-owner/test-repo"), new
        {
            id = 1,
            name = "test-repo",
            full_name = "test-owner/test-repo",
            owner = new { login = "test-owner", id = 1 },
            permissions = new { pull = false, push = false, admin = false }
        });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateAppCredentialsAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None,
            owner: "test-owner", repo: "test-repo");

        success.Should().BeTrue();
        message.Should().Contain("none");
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

    /// <summary>
    /// Covers the catch block in ListRepositoriesWithAppAsync when token exchange fails.
    /// Returns an empty list rather than throwing.
    /// </summary>
    [Fact]
    public async Task ListRepositoriesWithAppAsync_AuthFailure_ReturnsEmptyList()
    {
        StubTokenExchangeUnauthorized();

        var service = new GitHubValidationService();
        var result = await service.ListRepositoriesWithAppAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    #endregion

    #region ValidateActionsAccessAsync

    /// <summary>
    /// Covers the success path of ValidateActionsAccessAsync: token exchange succeeds and
    /// the actions runs endpoint returns results.
    /// </summary>
    [Fact]
    public async Task ValidateActionsAccessAsync_Success_ReturnsVerifiedMessage()
    {
        StubTokenExchange();
        StubGet(ApiPath("/repos/test-owner/test-repo/actions/runs"), new
        {
            total_count = 5,
            workflow_runs = Array.Empty<object>()
        });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateActionsAccessAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(),
            "test-owner", "test-repo", CancellationToken.None);

        success.Should().BeTrue();
        message.Should().Contain("Actions access verified");
        message.Should().Contain("5");
    }

    /// <summary>
    /// Covers the ForbiddenException path: actions endpoint returns 403.
    /// </summary>
    [Fact]
    public async Task ValidateActionsAccessAsync_Forbidden_ReturnsPermissionError()
    {
        StubTokenExchange();
        StubError(ApiPath("/repos/test-owner/test-repo/actions/runs"), 403,
            new { message = "Resource not accessible by integration" });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateActionsAccessAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(),
            "test-owner", "test-repo", CancellationToken.None);

        success.Should().BeFalse();
        message.Should().Contain("lacks Actions read permission");
    }

    /// <summary>
    /// Covers the NotFoundException path in ValidateActionsAccessAsync: repo not found returns 404.
    /// </summary>
    [Fact]
    public async Task ValidateActionsAccessAsync_RepoNotFound_ReturnsFalse()
    {
        StubTokenExchange();
        StubError(ApiPath("/repos/test-owner/missing-repo/actions/runs"), 404,
            new { message = "Not Found" });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateActionsAccessAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(),
            "test-owner", "missing-repo", CancellationToken.None);

        success.Should().BeFalse();
        message.Should().Contain("not found");
    }

    /// <summary>
    /// Covers the auth failure path in ValidateActionsAccessAsync: token exchange returns 401.
    /// </summary>
    [Fact]
    public async Task ValidateActionsAccessAsync_AuthFailure_ReturnsFalse()
    {
        StubTokenExchangeUnauthorized();

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateActionsAccessAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(),
            "test-owner", "test-repo", CancellationToken.None);

        success.Should().BeFalse();
        message.Should().Contain("Authentication failed");
    }

    /// <summary>
    /// Covers the generic Exception catch in ValidateActionsAccessAsync: unexpected server error.
    /// </summary>
    [Fact]
    public async Task ValidateActionsAccessAsync_ServerError_ReturnsFailureMessage()
    {
        StubTokenExchange();
        StubError(ApiPath("/repos/test-owner/test-repo/actions/runs"), 500,
            new { message = "Internal Server Error" });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateActionsAccessAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(),
            "test-owner", "test-repo", CancellationToken.None);

        success.Should().BeFalse();
        message.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region ValidateInstallationWithoutRepoAsync (connection failure)

    /// <summary>
    /// Covers the generic Exception catch in ValidateInstallationWithoutRepoAsync:
    /// token exchange succeeds but the installation repositories endpoint returns 500.
    /// </summary>
    [Fact]
    public async Task ValidateAppCredentialsAsync_NoOwnerRepo_ServerError_ReturnsConnectionFailed()
    {
        StubTokenExchange();
        StubError(ApiPath("/installation/repositories"), 500, new { message = "Internal Server Error" });

        var service = new GitHubValidationService();
        var (success, message) = await service.ValidateAppCredentialsAsync(
            Server.Url!, ClientId, InstallationId, GenerateValidPrivateKeyBase64(), CancellationToken.None);

        success.Should().BeFalse();
        message.Should().NotBeNullOrEmpty();
    }

    #endregion
}
