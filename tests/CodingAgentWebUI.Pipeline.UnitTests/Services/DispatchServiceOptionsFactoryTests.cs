using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="DispatchServiceOptionsFactory"/>.
/// Validates the extracted InitializeOptions logic reads all 7 config keys correctly.
/// Issue #1630: eliminates duplicated InitializeOptions across 3 sites.
/// </summary>
[Collection("EnvironmentVariables")]
public class DispatchServiceOptionsFactoryTests
{
    [Fact]
    public void Create_ReadsAllConfigKeys()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "30",
                ["WorkDistribution:Dispatch:RateLimitPerSecond"] = "5",
                ["WorkDistribution:CredentialPools:Kiro:0"] = "pvc-1",
                ["WorkDistribution:CredentialPools:Kiro:1"] = "pvc-2",
                ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
                ["WorkDistribution:AgentApiKeySecretName"] = "my-api-key-secret",
                ["WorkDistribution:AgentServiceAccountName"] = "my-service-account",
                ["WorkDistribution:Namespace"] = "my-namespace",
                ["WorkDistribution:OpencodeConfigSecretName"] = "opencode-config-secret"
            })
            .Build();

        // Act
        var options = DispatchServiceOptionsFactory.Create(config);

        // Assert
        options.PollIntervalSeconds.Should().Be(30);
        options.RateLimitPerSecond.Should().Be(5);
        options.KiroPvcPool.Should().BeEquivalentTo(["pvc-1", "pvc-2"]);
        options.OrchestratorUrl.Should().Be("http://orchestrator:8080");
        options.AgentApiKeySecretName.Should().Be("my-api-key-secret");
        options.AgentServiceAccountName.Should().Be("my-service-account");
        options.Namespace.Should().Be("my-namespace");
        options.OpencodeConfigSecretName.Should().Be("opencode-config-secret");
    }

    [Fact]
    public void Create_MissingNamespaceConfig_FallsBackToEnvironmentVariable()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "10"
                // No Namespace key
            })
            .Build();

        // Set POD_NAMESPACE env var
        var previousValue = Environment.GetEnvironmentVariable("POD_NAMESPACE");
        try
        {
            Environment.SetEnvironmentVariable("POD_NAMESPACE", "test-ns-from-env");

            // Act
            var options = DispatchServiceOptionsFactory.Create(config);

            // Assert
            options.Namespace.Should().Be("test-ns-from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAMESPACE", previousValue);
        }
    }

    [Fact]
    public void Create_MissingNamespaceConfigAndEnvVar_FallsBackToDefault()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "10"
                // No Namespace key
            })
            .Build();

        // Ensure POD_NAMESPACE is not set
        var previousValue = Environment.GetEnvironmentVariable("POD_NAMESPACE");
        try
        {
            Environment.SetEnvironmentVariable("POD_NAMESPACE", null);

            // Act
            var options = DispatchServiceOptionsFactory.Create(config);

            // Assert
            options.Namespace.Should().Be("default");
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAMESPACE", previousValue);
        }
    }

    [Fact]
    public void Create_MissingOptionalStringKeys_ReturnsEmptyStrings()
    {
        // Arrange — only PollIntervalSeconds provided
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "10"
            })
            .Build();

        var previousValue = Environment.GetEnvironmentVariable("POD_NAMESPACE");
        try
        {
            Environment.SetEnvironmentVariable("POD_NAMESPACE", null);

            // Act
            var options = DispatchServiceOptionsFactory.Create(config);

            // Assert
            options.OrchestratorUrl.Should().Be("");
            options.AgentApiKeySecretName.Should().Be("");
            options.AgentServiceAccountName.Should().Be("");
            options.OpencodeConfigSecretName.Should().Be("");
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAMESPACE", previousValue);
        }
    }

    [Fact]
    public void Create_NoPvcPoolConfig_UsesDefaultEmptyList()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "10"
            })
            .Build();

        // Act
        var options = DispatchServiceOptionsFactory.Create(config);

        // Assert — KiroPvcPool defaults to empty list
        options.KiroPvcPool.Should().BeEmpty();
    }

    [Fact]
    public void Create_DispatchSectionBinds_PollIntervalAndRateLimit()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "42",
                ["WorkDistribution:Dispatch:RateLimitPerSecond"] = "20"
            })
            .Build();

        // Act
        var options = DispatchServiceOptionsFactory.Create(config);

        // Assert
        options.PollIntervalSeconds.Should().Be(42);
        options.RateLimitPerSecond.Should().Be(20);
    }
}
