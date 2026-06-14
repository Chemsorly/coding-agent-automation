using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure;
using Moq;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Unit tests for ProviderFactory configuration wiring.
/// </summary>
public class ProviderFactoryTests
{
    /// <summary>
    /// Generates a valid base64-encoded RSA private key PEM string for test configs.
    /// </summary>
    private static string GenerateValidPrivateKeyBase64()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));
    }

    // --- REQ-4.5: ExternalCiPollInterval wiring ---

    [Fact]
    public async Task CreatePipelineProviderAsync_UsesPollIntervalFromPipelineConfiguration()
    {
        // Arrange — configure a specific poll interval
        var expectedInterval = TimeSpan.FromSeconds(45);
        var pipelineConfig = new PipelineConfiguration
        {
            ExternalCiPollInterval = expectedInterval
        };

        var mockConfigStore = new Mock<IPipelineConfigStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipelineConfig);

        var factory = new ProviderFactory(mockConfigStore.Object);

        var providerConfig = new ProviderConfig
        {
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "CI Provider",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.ClientId] = "Iv1.testclientid123",
                [ProviderSettingKeys.InstallationId] = "12345",
                [ProviderSettingKeys.PrivateKeyBase64] = GenerateValidPrivateKeyBase64(),
                [ProviderSettingKeys.Owner] = "testowner",
                [ProviderSettingKeys.Repo] = "testrepo"
            }
        };

        // Act
        var provider = await factory.CreatePipelineProviderAsync(providerConfig, CancellationToken.None);

        // Assert — verify the poll interval was passed through via reflection
        var pollIntervalField = typeof(GitHubActionsPipelineProvider)
            .GetField("_pollInterval", BindingFlags.NonPublic | BindingFlags.Instance);
        pollIntervalField.Should().NotBeNull("GitHubActionsPipelineProvider should have a _pollInterval field");

        var actualInterval = (TimeSpan)pollIntervalField!.GetValue(provider)!;
        actualInterval.Should().Be(expectedInterval,
            "ProviderFactory should pass PipelineConfiguration.ExternalCiPollInterval to the pipeline provider (REQ-4.5)");
    }

    [Fact]
    public async Task CreatePipelineProviderAsync_DefaultPollInterval_IsThirtySeconds()
    {
        // Arrange — use default PipelineConfiguration
        var pipelineConfig = new PipelineConfiguration();
        var mockConfigStore = new Mock<IPipelineConfigStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipelineConfig);

        var factory = new ProviderFactory(mockConfigStore.Object);

        var providerConfig = new ProviderConfig
        {
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "CI Provider",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.ClientId] = "Iv1.testclientid456",
                [ProviderSettingKeys.InstallationId] = "67890",
                [ProviderSettingKeys.PrivateKeyBase64] = GenerateValidPrivateKeyBase64(),
                [ProviderSettingKeys.Owner] = "testowner",
                [ProviderSettingKeys.Repo] = "testrepo"
            }
        };

        // Act
        var provider = await factory.CreatePipelineProviderAsync(providerConfig, CancellationToken.None);

        // Assert — default ExternalCiPollInterval is 30 seconds
        var pollIntervalField = typeof(GitHubActionsPipelineProvider)
            .GetField("_pollInterval", BindingFlags.NonPublic | BindingFlags.Instance);
        var actualInterval = (TimeSpan)pollIntervalField!.GetValue(provider)!;
        actualInterval.Should().Be(TimeSpan.FromSeconds(30),
            "default ExternalCiPollInterval should be 30 seconds");
    }

    // --- Agent provider tests removed: KiroCliAgentProvider moved to CodingAgentWebUI.Agent project ---

    // --- REQ-11: Runtime Configuration Refresh for ProviderFactory ---

    [Fact]
    public async Task CreatePipelineProviderAsync_ResolvesCurrentConfigPerCreation_NotStartupSnapshot()
    {
        // Arrange — mock store returns different configs on successive calls
        var firstConfig = new PipelineConfiguration { ExternalCiPollInterval = TimeSpan.FromSeconds(15) };
        var secondConfig = new PipelineConfiguration { ExternalCiPollInterval = TimeSpan.FromSeconds(90) };

        var callCount = 0;
        var mockConfigStore = new Mock<IPipelineConfigStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++callCount == 1 ? firstConfig : secondConfig);

        var factory = new ProviderFactory(mockConfigStore.Object);

        var providerConfig = new ProviderConfig
        {
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "CI Provider",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.ClientId] = "Iv1.testclientid789",
                [ProviderSettingKeys.InstallationId] = "11111",
                [ProviderSettingKeys.PrivateKeyBase64] = GenerateValidPrivateKeyBase64(),
                [ProviderSettingKeys.Owner] = "testowner",
                [ProviderSettingKeys.Repo] = "testrepo"
            }
        };

        var pollIntervalField = typeof(GitHubActionsPipelineProvider)
            .GetField("_pollInterval", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Act — create first provider
        var provider1 = await factory.CreatePipelineProviderAsync(providerConfig, CancellationToken.None);
        var interval1 = (TimeSpan)pollIntervalField.GetValue(provider1)!;

        // Act — create second provider (simulates config change between creations)
        var provider2 = await factory.CreatePipelineProviderAsync(providerConfig, CancellationToken.None);
        var interval2 = (TimeSpan)pollIntervalField.GetValue(provider2)!;

        // Assert — each creation uses the CURRENT config, not a captured snapshot
        interval1.Should().Be(TimeSpan.FromSeconds(15));
        interval2.Should().Be(TimeSpan.FromSeconds(90));
        mockConfigStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
