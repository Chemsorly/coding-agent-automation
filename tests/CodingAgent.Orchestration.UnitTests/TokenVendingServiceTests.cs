using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Hosting;
using Moq;
using Moq.Protected;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.UnitTests;

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
    [InlineData("privateKeyBase64", new[] { "clientId", "installationId" })]
    [InlineData("clientId", new[] { "privateKeyBase64", "installationId" })]
    [InlineData("installationId", new[] { "privateKeyBase64", "clientId" })]
    public async Task GenerateAgentTokenAsync_MissingSetting_Throws(string missingKey, string[] presentKeys)
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var settings = new Dictionary<string, string>();
        foreach (var key in presentKeys)
            settings[key] = key == "privateKeyBase64" ? "dGVzdA==" : "value-123";

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
    public async Task GenerateAgentTokenAsync_InvalidInstallationId_ThrowsInvalidOperation()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var config = new ProviderConfig
        {
            Id = "rp-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "123",
                [ProviderSettingKeys.InstallationId] = "not-a-number"
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        ex.Message.Should().Contain("installationId");
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_InvalidPemContent_ThrowsInvalidOperation()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        // Base64 of "not a pem key"
        var notPemBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not a pem key"));
        var config = new ProviderConfig
        {
            Id = "rp-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = notPemBase64,
                [ProviderSettingKeys.ClientId] = "123",
                [ProviderSettingKeys.InstallationId] = "456"
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        ex.Message.Should().Contain("PEM");
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
                    ["executablePath"] = "/usr/bin/kiro-cli"
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
    public async Task PrepareAgentConfigsAsync_NonCriticalConfigWithPrivateKey_StripsKeyOnFailure()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        // This config has a privateKeyBase64 but it's invalid, so token generation will fail
        var notPemBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not a pem key"));
        var configs = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Repo",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.PrivateKeyBase64] = notPemBase64,
                    [ProviderSettingKeys.ClientId] = "123",
                    [ProviderSettingKeys.InstallationId] = "456",
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo"
                }
            }
        };

        // Use a non-matching repoConfigId so this tests the non-critical fallback path
        var result = await service.PrepareAgentConfigsAsync(configs, "other-repo-id", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("rp-1");
        result[0].Settings.Should().NotContainKey(ProviderSettingKeys.PrivateKeyBase64);
        result[0].Settings.Should().ContainKey(ProviderSettingKeys.Owner);
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_CriticalConfig_ThrowsWithDescriptiveMessage()
    {
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

        // Config ID matches repoConfigId — this is a critical provider
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None));

        ex.Message.Should().Contain("Test Repo");
        ex.Message.Should().Contain("repo-1");
        ex.InnerException.Should().NotBeNull();
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
                    ["executablePath"] = "/usr/bin/kiro-cli",
                }
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        result[0].Settings[ProviderSettingKeys.Model].Should().Be("claude-sonnet-4");
        result[0].Settings["executablePath"].Should().Be("/usr/bin/kiro-cli");
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

    #region PrepareAgentConfigsAsync preserves SteeringContent

    /// <summary>
    /// Verifies that PrepareAgentConfigsAsync preserves SteeringContent through the
    /// non-GitHub-App clone path (no privateKeyBase64). This guards against the regression
    /// where CloneWithSettings omitted SteeringContent from the object initializer.
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_PreservesSteeringContent()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        const string expectedSteering = "# Repo Steering\n\nUse conventional commits. Always run tests before pushing.";

        var configs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "myorg/my-repo",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "myorg",
                    [ProviderSettingKeys.Repo] = "my-repo",
                    [ProviderSettingKeys.BaseBranch] = "main"
                },
                SteeringContent = expectedSteering
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].SteeringContent.Should().Be(expectedSteering);
    }

    /// <summary>
    /// Verifies that PrepareAgentConfigsAsync preserves null SteeringContent without error.
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_NullSteeringContent_PreservedAsNull()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());

        var configs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "myorg/my-repo",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "myorg",
                    [ProviderSettingKeys.Repo] = "my-repo",
                    [ProviderSettingKeys.BaseBranch] = "main"
                }
                // SteeringContent intentionally not set (null)
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].SteeringContent.Should().BeNull();
    }

    #endregion

    #region PrepareAgentConfigsAsync preserves all ProviderConfig properties

    /// <summary>
    /// Verifies that PrepareAgentConfigsAsync preserves Secrets, SetupSteps, RequiredLabels,
    /// and BlacklistedPaths through the non-GitHub-App path (no privateKeyBase64).
    /// **Validates: Requirements 2.1, 2.2, 2.3**
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_PreservesSecretsSetupStepsAndBlacklistProperties()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());

        var secrets = new Dictionary<string, string>
        {
            ["NUGET_FEED_TOKEN"] = "ghp_abc123secretvalue",
            ["PRIVATE_FEED_URL"] = "https://nuget.pkg.github.com/myorg/index.json"
        };

        var setupSteps = new List<SetupStep>
        {
            new() { Name = "Configure NuGet feed", Command = "dotnet nuget add source \"$PRIVATE_FEED_URL\" --name github --username x --password \"$NUGET_FEED_TOKEN\"" },
            new() { Name = "Restore dependencies", Command = "dotnet restore" }
        };

        var requiredLabels = new List<string> { "dotnet", "dotnet10" };
        var blacklistedPaths = new List<string> { "docs/", "*.md" };

        var configs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "myorg/private-project",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "myorg",
                    [ProviderSettingKeys.Repo] = "private-project",
                    [ProviderSettingKeys.BaseBranch] = "main"
                },
                RepositoryRole = RepositoryRole.Work,
                RequiredLabels = requiredLabels,
                BlacklistedPaths = blacklistedPaths,
                Secrets = secrets,
                SetupSteps = setupSteps
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        result.Should().HaveCount(1);
        var output = result[0];

        // Verify Secrets are preserved unchanged (Requirement 2.2)
        output.Secrets.Should().NotBeNull();
        output.Secrets.Should().BeEquivalentTo(secrets);

        // Verify SetupSteps are preserved unchanged (Requirement 2.3)
        output.SetupSteps.Should().NotBeNull();
        output.SetupSteps.Should().HaveCount(2);
        output.SetupSteps![0].Name.Should().Be("Configure NuGet feed");
        output.SetupSteps[0].Command.Should().Be("dotnet nuget add source \"$PRIVATE_FEED_URL\" --name github --username x --password \"$NUGET_FEED_TOKEN\"");
        output.SetupSteps[1].Name.Should().Be("Restore dependencies");
        output.SetupSteps[1].Command.Should().Be("dotnet restore");

        // Verify RequiredLabels are preserved (Requirement 2.1 — latent bug fix)
        output.RequiredLabels.Should().NotBeNull();
        output.RequiredLabels.Should().BeEquivalentTo(requiredLabels);

        // Verify BlacklistedPaths are preserved (Requirement 2.1 — latent bug fix)
        output.BlacklistedPaths.Should().NotBeNull();
        output.BlacklistedPaths.Should().BeEquivalentTo(blacklistedPaths);
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
    public bool PrepareAgentConfigsAsync_NonCriticalConfigWithPrivateKey_StripsPrivateKeyBase64(
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

        // Act: Use a non-matching repoConfigId so this tests the non-critical fallback path
        var result = service.PrepareAgentConfigsAsync(
            new List<ProviderConfig> { config }, "non-matching-repo-id", CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert: privateKeyBase64 must NOT be present in the result
        return result.Count == 1 && !result[0].Settings.ContainsKey(ProviderSettingKeys.PrivateKeyBase64);
    }

    /// <summary>
    /// Property 7: PrepareAgentConfigsAsync throws for critical provider token failure
    /// When token generation fails for a config whose ID matches repoConfigId,
    /// PrepareAgentConfigsAsync SHALL throw InvalidOperationException.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool PrepareAgentConfigsAsync_CriticalConfigWithPrivateKey_ThrowsOnFailure(
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

        // Act & Assert: When config ID matches repoConfigId, token failure should throw
        try
        {
            service.PrepareAgentConfigsAsync(
                new List<ProviderConfig> { config }, configId.Get, CancellationToken.None)
                .GetAwaiter().GetResult();
            return false; // Should have thrown
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains(config.DisplayName) && ex.Message.Contains(config.Id);
        }
    }

    #endregion

    #region TimeProvider injection and cache eviction

    /// <summary>
    /// Prerequisite test: verifies that both DateTimeOffset.UtcNow reads in
    /// GenerateAgentTokenAsync were replaced with _timeProvider.GetUtcNow().
    ///
    /// A cache hit on the second call (same time, same key) proves the fast path
    /// respects the injected clock. A cache miss after advancing the clock past the
    /// renewal buffer proves the slow-path double-check also uses the injected clock.
    /// </summary>
    [Fact]
    public async Task GenerateAgentTokenAsync_UsesTimeProvider_ForCacheHitCheck()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(startTime);

        var callCount = 0;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                callCount++;
                // Token expires 1 hour from whatever "now" is at mint time
                var expiresAt = clock.GetUtcNow().AddHours(1).ToString("O");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = $"ghs_token_{callCount}", expires_at = expiresAt }),
                        System.Text.Encoding.UTF8,
                        "application/json")
                });
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = new TokenVendingService(_mockLogger.Object, httpClient, clock);

        // First call — mints from GitHub
        var (token1, _) = await service.GenerateAgentTokenAsync(config, CancellationToken.None);
        callCount.Should().Be(1);
        token1.Should().Be("ghs_token_1");

        // Second call at the same time — cache hit, no HTTP call
        var (token2, _) = await service.GenerateAgentTokenAsync(config, CancellationToken.None);
        callCount.Should().Be(1, "second call should be a cache hit at the same clock time");
        token2.Should().Be("ghs_token_1");

        // Advance clock into the renewal buffer (token expires at start+1h; buffer is 5 min;
        // so advancing to start+56min triggers a fresh mint)
        clock.Advance(TimeSpan.FromMinutes(56));
        var (token3, _) = await service.GenerateAgentTokenAsync(config, CancellationToken.None);
        callCount.Should().Be(2, "advancing past the renewal buffer should trigger a new mint");
        token3.Should().Be("ghs_token_2");
    }

    /// <summary>
    /// Verifies that TrimExpiredCacheEntries removes an expired token entry
    /// and removes its corresponding semaphore entry from the dictionary.
    /// Both CacheCount and SemaphoreCount must reach 0 after the sweep.
    /// </summary>
    [Fact]
    public async Task TrimExpiredCacheEntries_RemovesExpiredEntry_AndRemovesMatchingSemaphore()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(startTime);

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                // Token expires 1 hour from now — well outside the renewal buffer,
                // so the first call will be cached and subsequent calls are cache hits.
                var expiresAt = clock.GetUtcNow().AddHours(1).ToString("O");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = "ghs_token", expires_at = expiresAt }),
                        System.Text.Encoding.UTF8,
                        "application/json")
                });
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = new TokenVendingService(_mockLogger.Object, httpClient, clock);

        // Seed the cache
        await service.GenerateAgentTokenAsync(config, CancellationToken.None);
        service.CacheCount.Should().Be(1);
        service.SemaphoreCount.Should().Be(1);

        // Advance past expiry (startTime + 1 hour)
        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));

        // Trim
        service.TrimExpiredCacheEntries();

        service.CacheCount.Should().Be(0, "expired token entry must be removed from _tokenCache");
        service.SemaphoreCount.Should().Be(0, "semaphore entry must be removed from _mintSemaphores when its token expires");
    }

    /// <summary>
    /// Verifies that TrimExpiredCacheEntries does NOT remove a cache entry whose
    /// token has not yet expired.
    /// </summary>
    [Fact]
    public async Task TrimExpiredCacheEntries_DoesNotRemoveValidEntry()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(startTime);

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { token = "ghs_token", expires_at = startTime.AddHours(1).ToString("O") }),
                    System.Text.Encoding.UTF8,
                    "application/json")
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = new TokenVendingService(_mockLogger.Object, httpClient, clock);

        // Seed the cache
        await service.GenerateAgentTokenAsync(config, CancellationToken.None);
        service.CacheCount.Should().Be(1);

        // Trim without advancing the clock
        service.TrimExpiredCacheEntries();

        service.CacheCount.Should().Be(1, "non-expired entry must not be removed by housekeeping");
        service.SemaphoreCount.Should().Be(1, "semaphore for a non-expired entry must not be removed");
    }

    /// <summary>
    /// Acceptance criterion test: demonstrates that the cache count does not grow
    /// unboundedly when the same expired key is re-requested.
    ///
    /// Without eviction, repeated re-requests for the same key after each expiry
    /// leave stale entries in _tokenCache (each bypassed on the fast path but never
    /// cleaned up). With eviction, CacheCount returns to 1 after each trim cycle.
    /// </summary>
    [Fact]
    public async Task TrimExpiredCacheEntries_CacheCountDoesNotGrowUnboundedly_WhenSameExpiredKeyIsReRequested()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(startTime);

        var httpCallCount = 0;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                httpCallCount++;
                // Token is valid for 1 hour from the current fake time.
                // This ensures the token is safely outside the 5-minute renewal buffer,
                // so subsequent requests within the same clock instant are cache hits.
                var expiresAt = clock.GetUtcNow().AddHours(1).ToString("O");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = $"ghs_token_{httpCallCount}", expires_at = expiresAt }),
                        System.Text.Encoding.UTF8,
                        "application/json")
                });
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = new TokenVendingService(_mockLogger.Object, httpClient, clock);

        // ── Round 1: make 3 requests for the same key ─────────────────────
        for (var i = 0; i < 3; i++)
            await service.GenerateAgentTokenAsync(config, CancellationToken.None);

        // Only 1 HTTP call should have been made (the other 2 are cache hits)
        httpCallCount.Should().Be(1, "repeated requests for the same key within its lifetime must be cache hits");
        service.CacheCount.Should().Be(1);
        service.SemaphoreCount.Should().Be(1);

        // ── Expire and trim ───────────────────────────────────────────────
        // Advance past startTime + 1 hour (the token expiry)
        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));
        service.TrimExpiredCacheEntries();

        service.CacheCount.Should().Be(0, "expired entry must be removed after trim");
        service.SemaphoreCount.Should().Be(0, "expired semaphore must be removed after trim");

        // ── Round 2: make 3 more requests for the same key ───────────────
        for (var i = 0; i < 3; i++)
            await service.GenerateAgentTokenAsync(config, CancellationToken.None);

        // Exactly 1 additional HTTP call for the re-mint (not 3 more)
        httpCallCount.Should().Be(2, "only one re-mint should occur — the subsequent 2 calls are cache hits");
        service.CacheCount.Should().Be(1, "cache must contain exactly 1 entry after re-mint, not accumulate stale entries");
        service.SemaphoreCount.Should().Be(1, "semaphore map must contain exactly 1 entry, not accumulate stale entries");
    }

    /// <summary>
    /// Verifies that TokenCacheHousekeepingService calls TrimExpiredCacheEntries on each timer tick.
    /// Uses a very short sweep interval and a seeded expired entry so the tick effect is observable
    /// without needing to subclass TokenVendingService.
    /// </summary>
    [Fact]
    public async Task TokenCacheHousekeepingService_EvictsExpiredEntries_OnEachTick()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(startTime);

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                // Token expires 1 hour from now so the initial mint is cached.
                var expiresAt = clock.GetUtcNow().AddHours(1).ToString("O");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = "ghs_token", expires_at = expiresAt }),
                        System.Text.Encoding.UTF8,
                        "application/json")
                });
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = new TokenVendingService(_mockLogger.Object, httpClient, clock);

        // Seed the cache with a token that expires in 1 hour
        await service.GenerateAgentTokenAsync(config, CancellationToken.None);
        service.CacheCount.Should().Be(1);

        // Advance clock past expiry BEFORE starting the housekeeping service
        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));

        // Start housekeeping service with a very short sweep interval
        using var cts = new CancellationTokenSource();
        var housekeepingService = new TokenCacheHousekeepingService(
            service,
            _mockLogger.Object,
            sweepInterval: TimeSpan.FromMilliseconds(50),
            timeProvider: clock);

        await ((IHostedService)housekeepingService).StartAsync(cts.Token);

        // Wait for the tick to fire and evict the expired entry. The bound is a hang detector only,
        // so a stalled test host cannot fail the test.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (service.CacheCount > 0 && DateTimeOffset.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(10);
        }

        await cts.CancelAsync();
        await ((IHostedService)housekeepingService).StopAsync(CancellationToken.None);

        service.CacheCount.Should().Be(0, "TokenCacheHousekeepingService must evict expired entries on each tick");
        service.SemaphoreCount.Should().Be(0, "TokenCacheHousekeepingService must remove expired semaphore entries on each tick");
    }

    /// <summary>
    /// AC#1: Verifies that a trim that runs during an in-flight mint does not cause
    /// GenerateAgentTokenAsync to throw ObjectDisposedException.
    ///
    /// Sequence: seed cache → advance clock past expiry → start Caller A (blocks in HTTP handler)
    /// → trim (removes the semaphore) → complete mint → assert Caller A returns the token.
    ///
    /// Before the fix, TrimExpiredCacheEntries called Dispose() on the removed semaphore, so
    /// sem.Release() in the holder's finally block threw ObjectDisposedException.
    /// After the fix, the semaphore is only removed from the dictionary; Release() succeeds.
    /// </summary>
    [Fact]
    public async Task TrimExpiredCacheEntries_DuringInFlightMint_DoesNotThrowObjectDisposedException()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(startTime);

        var callCount = 0;
        var handlerEntered = new ManualResetEventSlim(false);
        var mintBlocker = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Seed call: return T1 immediately
                    var expiresAt1 = clock.GetUtcNow().AddHours(1).ToString("O");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new { token = "ghs_seed", expires_at = expiresAt1 }),
                            System.Text.Encoding.UTF8, "application/json")
                    });
                }
                // Caller A's mint: signal that the handler has been entered, then block until mintBlocker completes
                handlerEntered.Set();
                return mintBlocker.Task;
            });

        var service = new TokenVendingService(_mockLogger.Object, new HttpClient(mockHandler.Object), clock);

        // Seed the cache: first call mints T1 (expires startTime + 1h)
        await service.GenerateAgentTokenAsync(config, CancellationToken.None);
        service.CacheCount.Should().Be(1);

        // Advance clock past T1's expiry
        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));

        // Start Caller A: misses fast path (T1 expired), acquires semaphore, blocks in MintTokenFromGitHubAsync
        var mintTask = service.GenerateAgentTokenAsync(config, CancellationToken.None);

        // Wait until Caller A is inside the HTTP handler (confirms it holds the semaphore)
        handlerEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("Caller A should enter the handler within 10 s");

        // Trim: sees T1 as expired (Caller A has not yet written T2), removes T1 and the semaphore.
        // Before the fix, this calls removedSem.Dispose(), causing sem.Release() in the finally block to throw.
        service.TrimExpiredCacheEntries();
        service.CacheCount.Should().Be(0, "expired T1 must be removed by the trim");

        // Complete the mint: Caller A finishes MintTokenFromGitHubAsync and calls sem.Release() in finally
        var mintedExpiresAt = clock.GetUtcNow().AddHours(1).ToString("O");
        mintBlocker.SetResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { token = "ghs_minted", expires_at = mintedExpiresAt }),
                System.Text.Encoding.UTF8, "application/json")
        });

        // Must return the minted token — NOT throw ObjectDisposedException.
        // WaitAsync(10s) surfaces a TimeoutException instead of hanging indefinitely if a
        // regression causes the task to deadlock rather than throw.
        var (token, _) = await mintTask.WaitAsync(TimeSpan.FromSeconds(10));
        token.Should().Be("ghs_minted");
        service.CacheCount.Should().Be(1, "T2 must be stored in the cache after the successful mint");
    }

    /// <summary>
    /// AC#2: Verifies that a caller already waiting on the same key's semaphore during a trim
    /// completes normally after the first mint finishes, rather than hanging indefinitely.
    ///
    /// Before the fix, sem.Release() threw ObjectDisposedException (the semaphore was disposed by
    /// the trim), so Release() was never called and Caller B's WaitAsync never returned.
    /// After the fix, Release() succeeds and Caller B is unblocked.
    /// </summary>
    [Fact]
    public async Task TrimExpiredCacheEntries_WaiterOnTrimmedSemaphore_CompletesAfterMintFinishes()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(startTime);

        var callCount = 0;
        var handlerEntered = new ManualResetEventSlim(false);
        var mintBlocker = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Seed call: return T1 immediately
                    var expiresAt1 = clock.GetUtcNow().AddHours(1).ToString("O");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new { token = "ghs_seed", expires_at = expiresAt1 }),
                            System.Text.Encoding.UTF8, "application/json")
                    });
                }
                // Caller A's mint: signal entry, then block until mintBlocker is completed
                handlerEntered.Set();
                return mintBlocker.Task;
            });

        var service = new TokenVendingService(_mockLogger.Object, new HttpClient(mockHandler.Object), clock);

        // Seed the cache
        await service.GenerateAgentTokenAsync(config, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));

        // Caller A: misses fast path (T1 expired), acquires semaphore, blocks in MintTokenFromGitHubAsync
        var taskA = service.GenerateAgentTokenAsync(config, CancellationToken.None);
        handlerEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("Caller A should be in the handler within 10 s");

        // Caller B: retrieves the same semaphore instance via GetOrAdd, then blocks on WaitAsync
        // (the semaphore is currently held by Caller A)
        var taskB = service.GenerateAgentTokenAsync(config, CancellationToken.None);

        // Allow Caller B's async continuation to be scheduled and reach sem.WaitAsync.
        // NOTE: GenerateTokenAsync has no async suspension point before sem.WaitAsync — all code
        // between the method entry and `await sem.WaitAsync(ct)` is synchronous (validation, TryGetValue,
        // GetOrAdd). This means when `var taskB = ...` returns, Caller B is already registered as a
        // waiter on S1. The Task.Delay is therefore purely defensive — it guards against future refactors
        // that introduce an early await before the semaphore acquisition.
        await Task.Delay(50);

        // Trim: removes the semaphore from the dictionary.
        // Before the fix: calls Dispose() on the semaphore; Caller A's finally throws ObjectDisposedException
        // and never calls sem.Release(), so Caller B hangs indefinitely on WaitAsync.
        service.TrimExpiredCacheEntries();

        // Complete the mint: Caller A writes T2, then calls sem.Release() in finally
        var mintedExpiresAt = clock.GetUtcNow().AddHours(1).ToString("O");
        mintBlocker.SetResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { token = "ghs_minted", expires_at = mintedExpiresAt }),
                System.Text.Encoding.UTF8, "application/json")
        });

        // Both callers must complete without exception.
        // Caller B acquires the semaphore after Caller A releases it, hits the double-check cache
        // hit (T2 is now in _tokenCache), and returns T2 without making a further HTTP call.
        // WaitAsync(10s) surfaces a TimeoutException instead of stalling the runner if a regression
        // causes sem.Release() to throw (so Caller B's WaitAsync never returns).
        await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(10));
        var (tokenA, _) = await taskA;
        var (tokenB, _) = await taskB;

        tokenA.Should().Be("ghs_minted",
            "Caller A must return the minted token, not throw ObjectDisposedException");
        tokenB.Should().Be("ghs_minted",
            "Caller B must receive the token via double-check cache hit after Caller A releases the semaphore");
        callCount.Should().Be(2,
            "only one HTTP mint must occur — Caller B must have queued on Caller A's semaphore and " +
            "hit the double-check cache hit, not acquired a fresh semaphore and triggered a third HTTP call");
    }

    /// <summary>
    /// AC#4: Verifies that a fresh cache entry stored for the same key between the trim's expiry
    /// check and its TryRemove call is NOT deleted. This tests the value-aware
    /// TryRemove(KeyValuePair&lt;K,V&gt;) fix for the TOCTOU race.
    ///
    /// Sequence: seed cache (T1) → advance clock past expiry → set hook to store T2 synchronously
    /// between the expiry check and TryRemove → call TrimExpiredCacheEntries → assert T2 survives
    /// and is returned as a cache hit on the next call without a new mint.
    ///
    /// Before the fix (key-only TryRemove): TryRemove(key, out _) deletes T2, CacheCount drops to 0,
    /// and the subsequent GenerateAgentTokenAsync triggers a third HTTP mint (httpCallCount = 3).
    /// After the fix (value-aware TryRemove): TryRemove(new KVP(key, T1)) returns false because
    /// the dictionary now holds T2 (not T1), so T2 is preserved and the subsequent call is a
    /// cache hit (httpCallCount stays at 2).
    /// </summary>
    [Fact]
    public async Task TrimExpiredCacheEntries_FreshEntryStoredBetweenCheckAndRemoval_IsNotDeleted()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var config = CreateRepoConfigWithValidKey(privateKeyBase64);
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(startTime);

        var httpCallCount = 0;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                httpCallCount++;
                var expiresAt = clock.GetUtcNow().AddHours(1).ToString("O");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = $"ghs_token_{httpCallCount}", expires_at = expiresAt }),
                        System.Text.Encoding.UTF8, "application/json")
                });
            });

        var service = new TokenVendingService(_mockLogger.Object, new HttpClient(mockHandler.Object), clock);

        // Step 1: Seed — mint T1 (expires startTime + 1h)
        await service.GenerateAgentTokenAsync(config, CancellationToken.None);
        httpCallCount.Should().Be(1);
        service.CacheCount.Should().Be(1);

        // Step 2: Advance clock past T1's expiry
        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));

        // Step 3: Set the hook to fire between the expiry check and TryRemove inside the trim.
        // The hook stores T2 by calling GenerateAgentTokenAsync synchronously.
        // (The mock handler returns Task.FromResult, so the entire call chain completes synchronously,
        // and .GetAwaiter().GetResult() returns immediately without blocking.)
        // The hook clears itself so it fires only once.
        service.OnBeforeCacheRemove = () =>
        {
            service.OnBeforeCacheRemove = null; // self-clear; run once
            service.GenerateAgentTokenAsync(config, CancellationToken.None).GetAwaiter().GetResult();
        };

        // Step 4: Trim
        //   - TryGetValue sees T1 (expired) → expiry check passes
        //   - Hook fires → stores T2 in _tokenCache (httpCallCount = 2)
        //   - TryRemove(new KVP(key, T1)):
        //       OLD code: TryRemove(key, out _) → removes T2! (TOCTOU bug)
        //       NEW code: value doesn't match T2 → returns false → T2 preserved!
        service.TrimExpiredCacheEntries();

        service.CacheCount.Should().Be(1, "the fresh T2 entry must survive; value-aware TryRemove must not delete it");
        service.SemaphoreCount.Should().Be(1, "the semaphore for T2 must not be removed");
        httpCallCount.Should().Be(2, "the hook must have triggered exactly one re-mint");

        // Step 5: Subsequent call must be a cache hit — T2 is still valid
        var (cachedToken, _) = await service.GenerateAgentTokenAsync(config, CancellationToken.None);
        httpCallCount.Should().Be(2, "T2 is still valid; no new HTTP call must occur");
        cachedToken.Should().Be("ghs_token_2", "cached T2 must be returned directly");
    }

    #endregion

    #region PrepareAgentConfigsAsync — readOnlyConfigIds

    /// <summary>
    /// A GitHub App config whose ID is in readOnlyConfigIds must receive a contents:read token.
    /// The config must NOT be marked clone-only — secrets and setup steps are preserved.
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_WithReadOnlyConfigId_GitHubApp_VendsReadOnlyToken()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            Secrets = new Dictionary<string, string> { ["MY_SECRET"] = "secret-value" },
            SetupSteps = new List<SetupStep> { new SetupStep { Name = "echo", Command = "echo hi" } },
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = privateKeyBase64,
                [ProviderSettingKeys.ClientId] = "Iv1.brain123",
                [ProviderSettingKeys.InstallationId] = "99999",
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Repo] = "brain-repo"
            }
        };

        HttpRequestMessage? capturedRequest = null;
        string? capturedRequestBody = null;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (req, ct) =>
            {
                capturedRequest = req;
                capturedRequestBody = await req.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = "ghs_read_token", expires_at = "2026-12-01T00:00:00Z" }),
                        System.Text.Encoding.UTF8, "application/json")
                };
            });

        var service = new TokenVendingService(_mockLogger.Object, new HttpClient(mockHandler.Object));
        var readOnlySet = new HashSet<string> { "brain-1" };

        var result = await service.PrepareAgentConfigsAsync(
            new[] { brainConfig }, "repo-1", CancellationToken.None,
            readOnlyConfigIds: readOnlySet);

        // Token was vended
        result.Should().HaveCount(1);
        result[0].Settings.Should().ContainKey(ProviderSettingKeys.Token);
        result[0].Settings[ProviderSettingKeys.Token].Should().Be("ghs_read_token");
        result[0].Settings.Should().NotContainKey(ProviderSettingKeys.PrivateKeyBase64);

        // The request body must include contents:read
        capturedRequest.Should().NotBeNull();
        capturedRequestBody.Should().NotBeNull();
        capturedRequestBody!.Should().Contain("\"read\"", "read-only brain token must request contents:read");
        capturedRequestBody.Should().NotContain("\"write\"", "read-only brain token must NOT request contents:write");

        // Config must NOT be clone-only — secrets and setup steps must survive
        result[0].Secrets.Should().NotBeNull("brain config must not be marked clone-only");
        result[0].SetupSteps.Should().NotBeNull("brain config must not be marked clone-only");
    }

    /// <summary>
    /// A config NOT in readOnlyConfigIds must still receive a write token (unchanged behavior).
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_WithReadOnlyConfigId_OtherConfigsGetWriteToken()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = privateKeyBase64,
                [ProviderSettingKeys.ClientId] = "Iv1.abc",
                [ProviderSettingKeys.InstallationId] = "11111",
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Repo] = "work-repo"
            }
        };
        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = privateKeyBase64,
                [ProviderSettingKeys.ClientId] = "Iv1.abc",
                [ProviderSettingKeys.InstallationId] = "22222",
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Repo] = "brain-repo"
            }
        };

        var capturedRequestBodies = new List<string>();
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (req, _) =>
            {
                capturedRequestBodies.Add(await req.Content!.ReadAsStringAsync());
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = "ghs_token", expires_at = "2026-12-01T00:00:00Z" }),
                        System.Text.Encoding.UTF8, "application/json")
                };
            });

        var service = new TokenVendingService(_mockLogger.Object, new HttpClient(mockHandler.Object));
        // Only brain-1 is read-only; repo-1 should still get a write token
        var readOnlySet = new HashSet<string> { "brain-1" };

        await service.PrepareAgentConfigsAsync(
            new[] { repoConfig, brainConfig }, "repo-1", CancellationToken.None,
            readOnlyConfigIds: readOnlySet);

        capturedRequestBodies.Should().HaveCount(2);
        // repo-1 (work repo, NOT in readOnlyConfigIds) → write token
        capturedRequestBodies[0].Should().Contain("\"write\"",
            "work repo config not in readOnlyConfigIds must get a write token");
        // brain-1 (in readOnlyConfigIds) → read-only token
        capturedRequestBodies[1].Should().Contain("\"read\"",
            "brain config in readOnlyConfigIds must get a read-only token");
        capturedRequestBodies[1].Should().NotContain("\"write\"");
    }

    /// <summary>
    /// Passing null readOnlyConfigIds (the default) is backward-compatible:
    /// all GitHub App configs get write tokens as before.
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_NullReadOnlyConfigIds_AllGetWriteToken()
    {
        var privateKeyBase64 = GenerateTestRsaPrivateKeyBase64();
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = privateKeyBase64,
                [ProviderSettingKeys.ClientId] = "Iv1.abc",
                [ProviderSettingKeys.InstallationId] = "11111",
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Repo] = "work-repo"
            }
        };

        string? capturedRequestBody = null;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (req, _) =>
            {
                capturedRequestBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = "ghs_token", expires_at = "2026-12-01T00:00:00Z" }),
                        System.Text.Encoding.UTF8, "application/json")
                };
            });

        var service = new TokenVendingService(_mockLogger.Object, new HttpClient(mockHandler.Object));

        // No readOnlyConfigIds — defaults to null
        await service.PrepareAgentConfigsAsync(
            new[] { repoConfig }, "repo-1", CancellationToken.None);

        capturedRequestBody.Should().Contain("\"write\"",
            "default call (null readOnlyConfigIds) must produce a write token");
    }

    /// <summary>
    /// A GitLab/PAT config listed in readOnlyConfigIds is handled by the non-GitHub-App branch
    /// and is not affected by the flag (no crash, AccessToken is passed through unchanged).
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_ReadOnlyConfigId_NonGitHubApp_UnchangedPath()
    {
        var gitlabConfig = new ProviderConfig
        {
            Id = "gitlab-brain",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "GitLab Brain",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "glpat-secret-token",
                [ProviderSettingKeys.Repo] = "org/brain-repo"
            }
        };

        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var readOnlySet = new HashSet<string> { "gitlab-brain" };

        // Should not throw — GitLab/PAT configs are copied through unchanged
        var result = await service.PrepareAgentConfigsAsync(
            new[] { gitlabConfig }, "repo-1", CancellationToken.None,
            readOnlyConfigIds: readOnlySet);

        result.Should().HaveCount(1);
        // The AccessToken is moved to Token key (standard PAT passthrough)
        result[0].Settings.Should().ContainKey(ProviderSettingKeys.Token);
        result[0].Settings[ProviderSettingKeys.Token].Should().Be("glpat-secret-token");
        result[0].Settings.Should().NotContainKey(ProviderSettingKeys.AccessToken,
            "AccessToken must be moved to Token key in the non-GitHub-App path");
    }

    #endregion
}
