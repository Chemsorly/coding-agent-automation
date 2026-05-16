using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using Moq.Protected;
using ILogger = Serilog.ILogger;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="TokenVendingService"/>.
/// Uses the internal constructor to inject a mock HttpClient.
/// </summary>
public class TokenVendingServiceTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    private static ProviderConfig CreateRepoConfig(
        string? privateKey = "dGVzdC1rZXk=", // base64 of "test-key"
        string? clientId = "client-123",
        string? installationId = "456",
        string? apiUrl = "https://api.github.com") => new()
    {
        Id = "repo-1",
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        DisplayName = "Test Repo",
        Settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.PrivateKeyBase64] = privateKey ?? "",
            [ProviderSettingKeys.ClientId] = clientId ?? "",
            [ProviderSettingKeys.InstallationId] = installationId ?? "",
            [ProviderSettingKeys.ApiUrl] = apiUrl ?? "",
            [ProviderSettingKeys.Owner] = "test-owner",
            [ProviderSettingKeys.Repo] = "test-repo",
            [ProviderSettingKeys.BaseBranch] = "main"
        }
    };

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new TokenVendingService(null!, new HttpClient());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullHttpClientFactory_Throws()
    {
        var act = () => new TokenVendingService(_mockLogger.Object, (IHttpClientFactory)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_NullConfig_Throws()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var act = () => service.GenerateAgentTokenAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(ProviderSettingKeys.PrivateKeyBase64, new[] { ProviderSettingKeys.ClientId, ProviderSettingKeys.InstallationId })]
    [InlineData(ProviderSettingKeys.ClientId, new[] { ProviderSettingKeys.PrivateKeyBase64, ProviderSettingKeys.InstallationId })]
    [InlineData(ProviderSettingKeys.InstallationId, new[] { ProviderSettingKeys.PrivateKeyBase64, ProviderSettingKeys.ClientId })]
    public async Task GenerateAgentTokenAsync_MissingSetting_Throws(string missingKey, string[] presentKeys)
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var settings = new Dictionary<string, string>();
        foreach (var key in presentKeys)
            settings[key] = key == ProviderSettingKeys.PrivateKeyBase64 ? "dGVzdA==" : "value-123";

        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = settings
        };

        var act = () => service.GenerateAgentTokenAsync(config, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{missingKey}*");
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_NullConfigs_Throws()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var act = () => service.PrepareAgentConfigsAsync(null!, "repo-1", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_NullRepoConfigId_Throws()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var act = () => service.PrepareAgentConfigsAsync(Array.Empty<ProviderConfig>(), null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_ConfigWithoutPrivateKey_PassedThrough()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var configs = new List<ProviderConfig>
        {
            new()
            {
                Id = "agent-1",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "Agent",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Model] = "auto",
                    [ProviderSettingKeys.ExecutablePath] = "/usr/bin/kiro-cli"
                }
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("agent-1");
        result[0].Settings.Should().ContainKey(ProviderSettingKeys.Model);
        result[0].Settings.Should().NotContainKey(ProviderSettingKeys.PrivateKeyBase64);
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_ConfigWithPrivateKey_StripsKeyOnFailure()
    {
        // Use a real HttpClient that will fail (no mock handler needed — the JWT generation
        // will fail because the private key is not a valid PEM)
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var configs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Test Repo",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.PrivateKeyBase64] = "bm90LWEtcmVhbC1rZXk=", // "not-a-real-key"
                    [ProviderSettingKeys.ClientId] = "client-123",
                    [ProviderSettingKeys.InstallationId] = "456",
                    [ProviderSettingKeys.Owner] = "test",
                    [ProviderSettingKeys.Repo] = "test"
                }
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        // Should strip the private key even on failure
        result.Should().HaveCount(1);
        result[0].Settings.Should().NotContainKey(ProviderSettingKeys.PrivateKeyBase64);
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_PreservesOtherSettings()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var configs = new List<ProviderConfig>
        {
            new()
            {
                Id = "agent-1",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "Agent",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Model] = "claude-sonnet-4",
                    [ProviderSettingKeys.ExecutablePath] = "/usr/bin/kiro-cli",
                    ["timeout"] = "300"
                }
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        result[0].Settings[ProviderSettingKeys.Model].Should().Be("claude-sonnet-4");
        result[0].Settings[ProviderSettingKeys.ExecutablePath].Should().Be("/usr/bin/kiro-cli");
        result[0].Settings["timeout"].Should().Be("300");
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_PreservesProviderMetadata()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var configs = new List<ProviderConfig>
        {
            new()
            {
                Id = "my-id",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "My Agent",
                Settings = new Dictionary<string, string>()
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        result[0].Id.Should().Be("my-id");
        result[0].Kind.Should().Be(ProviderKind.Agent);
        result[0].ProviderType.Should().Be("KiroCli");
        result[0].DisplayName.Should().Be("My Agent");
    }

    #region GenerateAgentTokenAsync with valid RSA key

    private static string GenerateTestRsaPrivateKeyBase64()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem));
    }

    private static ProviderConfig CreateRepoConfigWithValidKey(string privateKeyBase64) => new()
    {
        Id = "repo-valid",
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        DisplayName = "Test Repo",
        Settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.PrivateKeyBase64] = privateKeyBase64,
            [ProviderSettingKeys.ClientId] = "Iv1.abc123",
            [ProviderSettingKeys.InstallationId] = "12345",
            [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
            [ProviderSettingKeys.Owner] = "test-owner",
            [ProviderSettingKeys.Repo] = "test-repo"
        }
    };

    [Fact]
    public async Task GenerateAgentTokenAsync_WithValidRsaKeyAndMockHttpHandler_ReturnsValidNonEmptyToken()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { token = "ghs_test_token_123", expires_at = "2026-06-01T12:00:00Z" }),
                    System.Text.Encoding.UTF8,
                    "application/json")
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = new TokenVendingService(_mockLogger.Object, httpClient);

        var (token, expiresAt) = await service.GenerateAgentTokenAsync(config, CancellationToken.None);

        token.Should().NotBeNullOrEmpty();
        token.Should().Be("ghs_test_token_123");
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_WithSuccessfulHttpResponse_ExtractsTokenAndExpiration()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);
        var expectedExpiration = "2026-06-15T18:30:00Z";

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { token = "ghs_another_token", expires_at = expectedExpiration }),
                    System.Text.Encoding.UTF8,
                    "application/json")
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = new TokenVendingService(_mockLogger.Object, httpClient);

        var (token, expiresAt) = await service.GenerateAgentTokenAsync(config, CancellationToken.None);

        token.Should().Be("ghs_another_token");
        expiresAt.Should().Be(DateTimeOffset.Parse(expectedExpiration));
    }

    #endregion

    #region PrepareAgentConfigsAsync with empty config list

    [Fact]
    public async Task PrepareAgentConfigsAsync_WithEmptyConfigList_ReturnsEmptyList()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());

        var result = await service.PrepareAgentConfigsAsync(
            Array.Empty<ProviderConfig>(), "repo-1", CancellationToken.None);

        result.Should().BeEmpty();
    }

    #endregion

    #region Token request body serialization

    [Fact]
    public async Task GenerateAgentTokenAsync_WithoutIssuePermission_RequestBodyOmitsIssuesField()
    {
        // Regression test: when includeIssuePermission=false, the serialized JSON must NOT
        // contain "issues": null — GitHub rejects null as an invalid permission value (HTTP 422).
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);

        string? capturedRequestBody = null;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (req, _) =>
            {
                capturedRequestBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = "ghs_test", expires_at = "2026-06-01T12:00:00Z" }),
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = new TokenVendingService(_mockLogger.Object, httpClient);

        await service.GenerateAgentTokenAsync(config, CancellationToken.None, includeIssuePermission: false);

        capturedRequestBody.Should().NotBeNull();
        capturedRequestBody.Should().NotContain("\"issues\"",
            "null permission fields must be omitted from the request body, not sent as null");
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_WithIssuePermission_RequestBodyIncludesIssuesWrite()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);

        string? capturedRequestBody = null;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (req, _) =>
            {
                capturedRequestBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = "ghs_test", expires_at = "2026-06-01T12:00:00Z" }),
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = new TokenVendingService(_mockLogger.Object, httpClient);

        await service.GenerateAgentTokenAsync(config, CancellationToken.None, includeIssuePermission: true);

        capturedRequestBody.Should().NotBeNull();
        capturedRequestBody.Should().Contain("\"issues\":\"write\"");
    }

    #endregion

    #region Property 6: PrepareAgentConfigsAsync strips private keys

    /// <summary>
    /// Property 6: PrepareAgentConfigsAsync strips private keys
    /// For any ProviderConfig that contains a `privateKeyBase64` setting, after calling
    /// PrepareAgentConfigsAsync, the resulting config SHALL NOT contain the `privateKeyBase64`
    /// key in its Settings dictionary.
    /// **Validates: Requirements 14.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool PrepareAgentConfigsAsync_AnyConfigWithPrivateKey_StripsPrivateKeyBase64(
        NonEmptyString privateKeyValue,
        NonEmptyString configId,
        ProviderKind kind)
    {
        // Arrange: Create a ProviderConfig with a privateKeyBase64 setting
        var config = new ProviderConfig
        {
            Id = configId.Get,
            Kind = kind,
            ProviderType = "GitHub",
            DisplayName = "Test Config",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = privateKeyValue.Get,
                [ProviderSettingKeys.ClientId] = "client-123",
                [ProviderSettingKeys.InstallationId] = "456",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo"
            }
        };

        var service = new TokenVendingService(new Mock<ILogger>().Object, new HttpClient());

        // Act: PrepareAgentConfigsAsync will fail token generation (invalid key)
        // but should still strip the privateKeyBase64 setting
        var result = service.PrepareAgentConfigsAsync(
            new List<ProviderConfig> { config }, configId.Get, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert: privateKeyBase64 must NOT be present in the result
        return result.Count == 1 && !result[0].Settings.ContainsKey(ProviderSettingKeys.PrivateKeyBase64);
    }

    #endregion
}
