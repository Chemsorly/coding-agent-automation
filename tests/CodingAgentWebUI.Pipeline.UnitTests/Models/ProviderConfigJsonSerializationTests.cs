using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for ProviderConfig JSON serialization round-trip with Secrets and SetupSteps.
/// **Validates: Requirements 6.1, 6.2, 6.3**
/// </summary>
public class ProviderConfigJsonSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = PipelineJsonOptions.Default;

    /// <summary>
    /// Verifies that a ProviderConfig with Secrets and SetupSteps survives a JSON
    /// round-trip via PipelineJsonOptions.Default, and that the serialized JSON uses
    /// camelCase property names.
    /// **Validates: Requirements 6.1, 6.2, 6.3**
    /// </summary>
    [Fact]
    public void ProviderConfig_WithSecretsAndSetupSteps_JsonRoundTrip_PreservesAllValues()
    {
        // Arrange
        var original = new ProviderConfig
        {
            Id = "test-provider-id",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "myorg/private-project",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "myorg",
                ["repo"] = "private-project"
            },
            RepositoryRole = RepositoryRole.Work,
            Secrets = new Dictionary<string, string>
            {
                ["NUGET_FEED_TOKEN"] = "ghp_abc123secretvalue",
                ["PRIVATE_FEED_URL"] = "https://nuget.pkg.github.com/myorg/index.json"
            },
            SetupSteps =
            [
                new SetupStep
                {
                    Name = "Configure private NuGet feed",
                    Command = "dotnet nuget add source \"$PRIVATE_FEED_URL\" --name github --username x --password \"$NUGET_FEED_TOKEN\" --store-password-in-clear-text"
                },
                new SetupStep
                {
                    Name = "Restore dependencies",
                    Command = "dotnet restore"
                }
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ProviderConfig>(json, JsonOptions);

        // Assert — round-trip preserves all values
        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(original.Id);
        deserialized.Kind.Should().Be(original.Kind);
        deserialized.ProviderType.Should().Be(original.ProviderType);
        deserialized.DisplayName.Should().Be(original.DisplayName);
        deserialized.RepositoryRole.Should().Be(original.RepositoryRole);

        // Secrets preserved
        deserialized.Secrets.Should().NotBeNull();
        deserialized.Secrets.Should().HaveCount(2);
        deserialized.Secrets!["NUGET_FEED_TOKEN"].Should().Be("ghp_abc123secretvalue");
        deserialized.Secrets["PRIVATE_FEED_URL"].Should().Be("https://nuget.pkg.github.com/myorg/index.json");

        // SetupSteps preserved
        deserialized.SetupSteps.Should().NotBeNull();
        deserialized.SetupSteps.Should().HaveCount(2);
        deserialized.SetupSteps![0].Name.Should().Be("Configure private NuGet feed");
        deserialized.SetupSteps[0].Command.Should().Be(
            "dotnet nuget add source \"$PRIVATE_FEED_URL\" --name github --username x --password \"$NUGET_FEED_TOKEN\" --store-password-in-clear-text");
        deserialized.SetupSteps[1].Name.Should().Be("Restore dependencies");
        deserialized.SetupSteps[1].Command.Should().Be("dotnet restore");

        // Assert — camelCase naming in serialized JSON (Requirement 6.3)
        json.Should().Contain("\"secrets\"");
        json.Should().Contain("\"setupSteps\"");
        json.Should().Contain("\"name\"");
        json.Should().Contain("\"command\"");

        // Verify camelCase for other properties too
        json.Should().Contain("\"id\"");
        json.Should().Contain("\"kind\"");
        json.Should().Contain("\"providerType\"");
        json.Should().Contain("\"displayName\"");
    }

    /// <summary>
    /// Verifies backward compatibility: an existing repo provider JSON without secrets/setupSteps
    /// deserializes successfully with Secrets and SetupSteps as null, and all other properties intact.
    /// **Validates: Requirements 6.1**
    /// </summary>
    [Fact]
    public void ProviderConfig_WithoutSecretsOrSetupSteps_Deserializes_WithNullsAndNoExceptions()
    {
        // Arrange — a pre-existing config JSON that does NOT contain "secrets" or "setupSteps"
        const string existingJson = """
            {
              "id": "7ffaaa62-0406-4a5f-801e-e0a191099124",
              "kind": "Repository",
              "providerType": "GitHub",
              "displayName": "myorg/legacy-project",
              "settings": {
                "apiUrl": "https://api.github.com",
                "owner": "myorg",
                "repo": "legacy-project",
                "baseBranch": "main"
              },
              "repositoryRole": "Work",
              "requiredLabels": ["dotnet", "dotnet10"],
              "blacklistedPaths": ["docs/", "README.md"],
              "blacklistMode": "WarnAndExclude"
            }
            """;

        // Act — should not throw
        var deserialized = JsonSerializer.Deserialize<ProviderConfig>(existingJson, JsonOptions);

        // Assert — no exception, result is valid
        deserialized.Should().NotBeNull();

        // Secrets and SetupSteps are null (not present in JSON)
        deserialized!.Secrets.Should().BeNull();
        deserialized.SetupSteps.Should().BeNull();

        // All other properties are correctly deserialized
        deserialized.Id.Should().Be("7ffaaa62-0406-4a5f-801e-e0a191099124");
        deserialized.Kind.Should().Be(ProviderKind.Repository);
        deserialized.ProviderType.Should().Be("GitHub");
        deserialized.DisplayName.Should().Be("myorg/legacy-project");
        deserialized.RepositoryRole.Should().Be(RepositoryRole.Work);
        deserialized.Settings.Should().ContainKey("apiUrl").WhoseValue.Should().Be("https://api.github.com");
        deserialized.Settings.Should().ContainKey("owner").WhoseValue.Should().Be("myorg");
        deserialized.Settings.Should().ContainKey("repo").WhoseValue.Should().Be("legacy-project");
        deserialized.Settings.Should().ContainKey("baseBranch").WhoseValue.Should().Be("main");
        deserialized.RequiredLabels.Should().NotBeNull();
        deserialized.RequiredLabels.Should().BeEquivalentTo(new[] { "dotnet", "dotnet10" });
        deserialized.BlacklistedPaths.Should().NotBeNull();
        deserialized.BlacklistedPaths.Should().BeEquivalentTo(new[] { "docs/", "README.md" });
        deserialized.BlacklistMode.Should().Be(BlacklistMode.WarnAndExclude);
    }
}
