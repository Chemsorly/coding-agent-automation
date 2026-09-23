using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Hosting;
using Moq;
using Moq.Protected;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests;

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
    public async Task PrepareAgentConfigsAsync_NonCriticalConfigWithPrivateKey_StripsKeyOnFailure()
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

        // Use a non-matching repoConfigId so this tests the non-critical fallback path
        var result = await service.PrepareAgentConfigsAsync(configs, "other-repo-id", CancellationToken.None);

        // Should strip the private key even on failure
        result.Should().HaveCount(1);
        result[0].Settings.Should().NotContainKey(ProviderSettingKeys.PrivateKeyBase64);
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
                    [ProviderSettingKeys.ExecutablePath] = "/usr/bin/kiro-cli",
                    [ProviderSettingKeys.Timeout] = "300"
                }
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        result[0].Settings[ProviderSettingKeys.Model].Should().Be("claude-sonnet-4");
        result[0].Settings[ProviderSettingKeys.ExecutablePath].Should().Be("/usr/bin/kiro-cli");
        result[0].Settings[ProviderSettingKeys.Timeout].Should().Be("300");
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
    /// and disposes (removes) its corresponding semaphore entry.
    /// Both CacheCount and SemaphoreCount must reach 0 to satisfy the two
    /// acceptance criteria independently.
    /// </summary>
    [Fact]
    public async Task TrimExpiredCacheEntries_RemovesExpiredEntry_AndDisposesMatchingSemaphore()
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
        // TODO [WARNING]: SemaphoreCount == 0 proves the entry was removed from the dictionary,
        // but does not verify that Dispose() was actually called on the removed SemaphoreSlim.
        // To assert disposal explicitly, expose the removed instance (e.g., via an internal
        // TrimExpiredCacheEntries overload that returns removed semaphores) and assert
        // removedSem.IsDisposed via reflection, or use a mock/subclass that tracks Dispose calls.
        service.SemaphoreCount.Should().Be(0, "semaphore entry must be removed and disposed when its token expires");
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

        // Wait for the tick to fire and evict the expired entry
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
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

    #endregion
}
