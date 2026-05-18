using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for TokenVendingService — validates input validation, config stripping,
/// and error handling paths.
/// </summary>
public class TokenVendingServiceTests
{
    private readonly Mock<ILogger> _mockLogger;

    public TokenVendingServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new TokenVendingService(null!, new HttpClient()));
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_NullConfig_ThrowsArgumentNull()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.GenerateAgentTokenAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_MissingPrivateKey_ThrowsInvalidOperation()
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
                [ProviderSettingKeys.ClientId] = "123",
                [ProviderSettingKeys.InstallationId] = "456"
                // Missing privateKeyBase64
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        ex.Message.Should().Contain("privateKeyBase64");
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_MissingClientId_ThrowsInvalidOperation()
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
                [ProviderSettingKeys.InstallationId] = "456"
                // Missing clientId
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        ex.Message.Should().Contain("clientId");
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_MissingInstallationId_ThrowsInvalidOperation()
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
                [ProviderSettingKeys.ClientId] = "123"
                // Missing installationId
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        ex.Message.Should().Contain("installationId");
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
    public async Task PrepareAgentConfigsAsync_NullConfigs_ThrowsArgumentNull()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.PrepareAgentConfigsAsync(null!, "rp-1", CancellationToken.None));
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_NullRepoConfigId_ThrowsArgumentNull()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.PrepareAgentConfigsAsync(new List<ProviderConfig>(), null!, CancellationToken.None));
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_ConfigWithoutPrivateKey_PassedThrough()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var configs = new List<ProviderConfig>
        {
            new()
            {
                Id = "ap-1",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "Agent",
                Settings = new Dictionary<string, string>
                {
                    ["executablePath"] = "/usr/bin/kiro-cli",
                    ["timeout"] = "30"
                }
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "rp-1", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("ap-1");
        result[0].Settings.Should().ContainKey("executablePath");
        result[0].Settings.Should().NotContainKey("privateKeyBase64");
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_ConfigWithPrivateKey_StripsKeyOnFailure()
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

        var result = await service.PrepareAgentConfigsAsync(configs, "rp-1", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("rp-1");
        result[0].Settings.Should().NotContainKey("privateKeyBase64");
        result[0].Settings.Should().ContainKey("owner");
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_EmptyList_ReturnsEmpty()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());

        var result = await service.PrepareAgentConfigsAsync(new List<ProviderConfig>(), "rp-1", CancellationToken.None);

        result.Should().BeEmpty();
    }
}
