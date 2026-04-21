using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using KiroCliLib.Core;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Providers;
using Moq;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Unit tests for ProviderFactory configuration wiring.
/// </summary>
public class ProviderFactoryTests
{
    private static readonly Mock<IKiroCliOrchestrator> MockOrchestrator = new();

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

        var factory = new ProviderFactory(MockOrchestrator.Object, pipelineConfig);

        var providerConfig = new ProviderConfig
        {
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "CI Provider",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["clientId"] = "Iv1.testclientid123",
                ["installationId"] = "12345",
                ["privateKeyBase64"] = GenerateValidPrivateKeyBase64(),
                ["owner"] = "testowner",
                ["repo"] = "testrepo"
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
        var factory = new ProviderFactory(MockOrchestrator.Object, pipelineConfig);

        var providerConfig = new ProviderConfig
        {
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "CI Provider",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["clientId"] = "Iv1.testclientid456",
                ["installationId"] = "67890",
                ["privateKeyBase64"] = GenerateValidPrivateKeyBase64(),
                ["owner"] = "testowner",
                ["repo"] = "testrepo"
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

    // --- Model passthrough tests ---

    [Fact]
    public void CreateAgentProvider_WithModelInConfig_PassesModelToProvider()
    {
        var pipelineConfig = new PipelineConfiguration();
        var factory = new ProviderFactory(MockOrchestrator.Object, pipelineConfig);

        var providerConfig = new ProviderConfig
        {
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>
            {
                ["model"] = "claude-sonnet-4.6",
                ["executablePath"] = "/usr/bin/kiro-cli"
            }
        };

        var provider = factory.CreateAgentProvider(providerConfig);
        provider.Should().BeOfType<KiroCliAgentProvider>();
        ((KiroCliAgentProvider)provider).Model.Should().Be("claude-sonnet-4.6");
    }

    [Fact]
    public void CreateAgentProvider_WithoutModelInConfig_ModelIsNull()
    {
        var pipelineConfig = new PipelineConfiguration();
        var factory = new ProviderFactory(MockOrchestrator.Object, pipelineConfig);

        var providerConfig = new ProviderConfig
        {
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
        };

        var provider = factory.CreateAgentProvider(providerConfig);
        ((KiroCliAgentProvider)provider).Model.Should().BeNull();
    }
}
