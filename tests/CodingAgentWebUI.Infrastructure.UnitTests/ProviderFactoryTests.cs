using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
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
    public void CreatePipelineProvider_UsesPollIntervalFromPipelineConfiguration()
    {
        // Arrange — configure a specific poll interval
        var expectedInterval = TimeSpan.FromSeconds(45);
        var pipelineConfig = new PipelineConfiguration
        {
            ExternalCiPollInterval = expectedInterval
        };

        var factory = new ProviderFactory(pipelineConfig);

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
        var provider = factory.CreatePipelineProvider(providerConfig);

        // Assert — verify the poll interval was passed through via reflection
        var pollIntervalField = typeof(GitHubActionsPipelineProvider)
            .GetField("_pollInterval", BindingFlags.NonPublic | BindingFlags.Instance);
        pollIntervalField.Should().NotBeNull("GitHubActionsPipelineProvider should have a _pollInterval field");

        var actualInterval = (TimeSpan)pollIntervalField!.GetValue(provider)!;
        actualInterval.Should().Be(expectedInterval,
            "ProviderFactory should pass PipelineConfiguration.ExternalCiPollInterval to the pipeline provider (REQ-4.5)");
    }

    [Fact]
    public void CreatePipelineProvider_DefaultPollInterval_IsThirtySeconds()
    {
        // Arrange — use default PipelineConfiguration
        var pipelineConfig = new PipelineConfiguration();
        var factory = new ProviderFactory(pipelineConfig);

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
        var provider = factory.CreatePipelineProvider(providerConfig);

        // Assert — default ExternalCiPollInterval is 30 seconds
        var pollIntervalField = typeof(GitHubActionsPipelineProvider)
            .GetField("_pollInterval", BindingFlags.NonPublic | BindingFlags.Instance);
        var actualInterval = (TimeSpan)pollIntervalField!.GetValue(provider)!;
        actualInterval.Should().Be(TimeSpan.FromSeconds(30),
            "default ExternalCiPollInterval should be 30 seconds");
    }

    // --- Agent provider tests removed: KiroCliAgentProvider moved to CodingAgentWebUI.Agent project ---
}
