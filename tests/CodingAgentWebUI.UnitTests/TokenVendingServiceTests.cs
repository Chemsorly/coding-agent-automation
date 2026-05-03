using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using Moq.Protected;
using ILogger = Serilog.ILogger;

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
            ["privateKeyBase64"] = privateKey ?? "",
            ["clientId"] = clientId ?? "",
            ["installationId"] = installationId ?? "",
            ["apiUrl"] = apiUrl ?? "",
            ["owner"] = "test-owner",
            ["repo"] = "test-repo",
            ["baseBranch"] = "main"
        }
    };

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new TokenVendingService(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_NullConfig_Throws()
    {
        var service = new TokenVendingService(_mockLogger.Object);
        var act = () => service.GenerateAgentTokenAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData("privateKeyBase64", new[] { "clientId", "installationId" })]
    [InlineData("clientId", new[] { "privateKeyBase64", "installationId" })]
    [InlineData("installationId", new[] { "privateKeyBase64", "clientId" })]
    public async Task GenerateAgentTokenAsync_MissingSetting_Throws(string missingKey, string[] presentKeys)
    {
        var service = new TokenVendingService(_mockLogger.Object);
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
        var service = new TokenVendingService(_mockLogger.Object);
        var act = () => service.PrepareAgentConfigsAsync(null!, "repo-1", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_NullRepoConfigId_Throws()
    {
        var service = new TokenVendingService(_mockLogger.Object);
        var act = () => service.PrepareAgentConfigsAsync(Array.Empty<ProviderConfig>(), null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_ConfigWithoutPrivateKey_PassedThrough()
    {
        var service = new TokenVendingService(_mockLogger.Object);
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
                    ["model"] = "auto",
                    ["executablePath"] = "/usr/bin/kiro-cli"
                }
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("agent-1");
        result[0].Settings.Should().ContainKey("model");
        result[0].Settings.Should().NotContainKey("privateKeyBase64");
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_ConfigWithPrivateKey_StripsKeyOnFailure()
    {
        // Use a real HttpClient that will fail (no mock handler needed — the JWT generation
        // will fail because the private key is not a valid PEM)
        var service = new TokenVendingService(_mockLogger.Object);
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
                    ["privateKeyBase64"] = "bm90LWEtcmVhbC1rZXk=", // "not-a-real-key"
                    ["clientId"] = "client-123",
                    ["installationId"] = "456",
                    ["owner"] = "test",
                    ["repo"] = "test"
                }
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        // Should strip the private key even on failure
        result.Should().HaveCount(1);
        result[0].Settings.Should().NotContainKey("privateKeyBase64");
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_PreservesOtherSettings()
    {
        var service = new TokenVendingService(_mockLogger.Object);
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
                    ["model"] = "claude-sonnet-4",
                    ["executablePath"] = "/usr/bin/kiro-cli",
                    ["timeout"] = "300"
                }
            }
        };

        var result = await service.PrepareAgentConfigsAsync(configs, "repo-1", CancellationToken.None);

        result[0].Settings["model"].Should().Be("claude-sonnet-4");
        result[0].Settings["executablePath"].Should().Be("/usr/bin/kiro-cli");
        result[0].Settings["timeout"].Should().Be("300");
    }

    [Fact]
    public async Task PrepareAgentConfigsAsync_PreservesProviderMetadata()
    {
        var service = new TokenVendingService(_mockLogger.Object);
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
}
